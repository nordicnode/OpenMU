// <copyright file="BotManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Collections.Concurrent;
using System.Linq;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Manages the lifecycle of server-side bots.
/// </summary>
/// <remarks>
/// A bot reuses the connection-less <see cref="OfflinePlayer"/> together with its MU Helper AI,
/// but is spawned in a fully standalone way: the account is loaded fresh in the bot's own
/// persistence context (see <see cref="OfflinePlayer.InitializeAsync"/>), so there is no
/// cross-context attach of entities owned by another player - which is the root cause of the
/// known data-corruption issue of the <c>/offlevel</c> handover. Because each bot drives a
/// distinct character in its own context, several bots can animate different characters of the
/// same account at once (the shared account row is only attached, never modified).
/// </remarks>
public sealed class BotManager
{
    private readonly ConcurrentDictionary<string, BotPlayer> _bots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The health supervisor per account, keyed by login name. It has to outlive the <see cref="BotPlayer"/>
    /// instance: a restart throws the bot away and spawns a new one for the same account, and only a supervisor
    /// which survived that can tell a bot which heals from one which breaks itself again within minutes.
    /// </summary>
    private readonly ConcurrentDictionary<string, BotHealthSupervisor> _health = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _nextHealthPruneUtc;

    /// <summary>
    /// Gets a snapshot of the currently active bots.
    /// </summary>
    public IReadOnlyCollection<BotPlayer> Bots => this._bots.Values.ToList();

    /// <summary>
    /// Gets the number of currently active bots, without taking a snapshot of them - the periodic task
    /// asks every second whether there is anything to stop.
    /// </summary>
    public int BotCount => this._bots.Count;

    /// <summary>
    /// Spawns a bot which drives a specific character of the given account.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="loginName">The login name of an existing account.</param>
    /// <param name="characterSlot">The character slot to drive; <c>null</c> drives the first character by slot.</param>
    /// <returns><c>true</c> if a bot was started; <c>false</c> if it could not be started or was already active.</returns>
    public async ValueTask<bool> SpawnBotAsync(IGameContext gameContext, string loginName, byte? characterSlot = null)
    {
        if (string.IsNullOrWhiteSpace(loginName))
        {
            return false;
        }

        var bot = new BotPlayer(gameContext);
        var added = false;
        string? key = null;
        try
        {
            // Load the account through the bot's OWN persistence context (no cross-context attach).
            var account = await bot.PersistenceContext.GetAccountByLoginNameAsync(loginName).ConfigureAwait(false);
            var character = characterSlot is { } slot
                ? account?.Characters.FirstOrDefault(c => c.CharacterSlot == slot)
                : account?.Characters.OrderBy(c => c.CharacterSlot).FirstOrDefault();
            if (account is null || character is null)
            {
                bot.Logger.LogWarning("Bot account '{LoginName}' (slot {Slot}) could not be loaded or has no character.", loginName, characterSlot);
                await bot.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            key = GetKey(loginName, character.CharacterSlot);
            if (!this._bots.TryAdd(key, bot))
            {
                // Already animating this character.
                await bot.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            added = true;

            // Provide AI settings so the bot actually hunts (a missing config means a single-tile range).
            bot.MuHelperSettings = new BotMuHelperSettings();

            // The account's health record, not a fresh one: a bot which was restarted a minute ago must be known
            // as such, so the next restart escalation continues instead of starting over.
            bot.Health = this.HealthFor(loginName);

            // The behavior persona and the session seed are derived from the identity, so a bot keeps its
            // style across restarts and a report can be replayed exactly from the logged seed.
            var configuration = BotFeaturePlugIn.GetConfiguration(gameContext);
            bot.Persona = BotPersonaProfile.PickPersona(configuration?.PersonaMix, loginName, character.Name);
            var seed = BotPersonaProfile.DeriveSeed(loginName, character.Name, configuration?.RandomSeed ?? 0);
            bot.SessionRandom = new BotSessionRandom(seed);
            bot.Logger.LogInformation(
                "Bot {Character} plays as {Persona} (session seed {Seed}).",
                character.Name,
                bot.Persona,
                seed);

            if (!await bot.InitializeAsync(loginName, character.Name).ConfigureAwait(false))
            {
                await this.RemoveAndDisposeAsync(key, bot).ConfigureAwait(false);
                return false;
            }

            BotSkillProgressionPlugIn.CatchUpPendingProgress(bot);

            bot.Logger.LogDebug("Bot started for account '{LoginName}', character '{Character}'.", loginName, character.Name);
            return true;
        }
        catch (Exception ex)
        {
            bot.Logger.LogError(ex, "Failed to spawn bot for account '{LoginName}' (slot {Slot}).", loginName, characterSlot);
            if (added && key is not null)
            {
                await this.RemoveAndDisposeAsync(key, bot).ConfigureAwait(false);
            }
            else
            {
                await bot.DisposeAsync().ConfigureAwait(false);
            }

            return false;
        }
    }

    /// <summary>
    /// Stops and removes all currently active bots.
    /// </summary>
    /// <returns>The task.</returns>
    public async ValueTask StopAllAsync()
    {
        foreach (var key in this._bots.Keys.ToList())
        {
            if (this._bots.TryRemove(key, out var bot))
            {
                await StopAndDisposeAsync(bot, key, "shutdown").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Determines whether the given character of the given account is currently animated by a bot.
    /// </summary>
    /// <param name="loginName">The account login name.</param>
    /// <param name="slot">The character slot.</param>
    public bool IsActive(string loginName, byte slot) => this._bots.ContainsKey(GetKey(loginName, slot));

    /// <summary>
    /// Stops one randomly chosen bot (used by the presence rotation, so the population ebbs and flows
    /// like a real player base). The bot leaves its party cleanly first, then disconnects - which also
    /// saves its progress, like a regular logout.
    /// </summary>
    /// <returns>The name of the stopped bot's character, or null if no bot was active.</returns>
    public async ValueTask<string?> StopRandomBotAsync()
    {
        var key = this._bots.Keys.ToList().SelectRandom();
        if (key is null || !this._bots.TryRemove(key, out var bot))
        {
            return null;
        }

        var name = bot.Name;
        if (bot.IsDisposed || bot.IsDisposing)
        {
            // Already torn down concurrently (e.g. by the server shutdown disconnecting it) -
            // nothing left to stop or dispose.
            return name;
        }

        try
        {
            if (bot.Party is { } party)
            {
                await party.KickMySelfAsync(bot).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // A failed party goodbye must not skip the stop below - the party cleans up a
            // disconnected member itself.
            bot.Logger.LogWarning(ex, "Bot '{Key}' couldn't leave its party for the presence rotation.", key);
        }

        await StopAndDisposeAsync(bot, key, "presence rotation").ConfigureAwait(false);
        return name;
    }

    /// <summary>
    /// Stops the given bot like a regular logout and immediately brings the same character back
    /// online - the ghost equivalent of a player relogging. Used after the master evolution: the
    /// master class's base attributes (master experience rate, master points per level) and the
    /// master level stat are only mounted when the character enters the world, so the class change
    /// must be followed by a fresh world entry before master experience can flow.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="bot">The bot to restart.</param>
    /// <returns><c>true</c> if the bot came back online.</returns>
    public async ValueTask<bool> RestartBotAsync(IGameContext gameContext, BotPlayer bot)
    {
        // Captured before the stop - the disposed bot loses its account and character.
        var loginName = bot.Account?.LoginName;
        var characterSlot = bot.SelectedCharacter?.CharacterSlot;
        if (loginName is null
            || characterSlot is not { } slot
            || !this._bots.TryRemove(GetKey(loginName, slot), out var removed))
        {
            return false;
        }

        if (removed.IsDisposed || removed.IsDisposing)
        {
            // Already torn down concurrently (e.g. by the server shutdown disconnecting it) - no
            // stop or dispose left to do, just bring the character back online.
            return await this.SpawnBotAsync(gameContext, loginName, slot).ConfigureAwait(false);
        }

        if (removed.Party is { } party)
        {
            try
            {
                await party.KickMySelfAsync(removed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed party goodbye must not skip the stop below - the party cleans up a
                // disconnected member itself.
                removed.Logger.LogWarning(ex, "Bot '{Login}/{Slot}' couldn't leave its party for the restart.", loginName, slot);
            }
        }

        try
        {
            await removed.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The old instance may still be (partially) alive - put it back under management and
            // don't spawn a second player driving the same character (two persistence contexts
            // saving one character would be last-write-wins data loss). Retried on a later pass.
            removed.Logger.LogError(ex, "Error while stopping bot '{Login}/{Slot}' for a restart; keeping the old instance.", loginName, slot);
            this._bots.TryAdd(GetKey(loginName, slot), removed);
            return false;
        }

        // The stopped instance is done for good - release its persistence context and the tracked
        // account graph before the fresh one loads them again.
        try
        {
            await removed.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            removed.Logger.LogWarning(ex, "Error while disposing bot '{Login}/{Slot}' for a restart; spawning a fresh instance anyway.", loginName, slot);
        }

        return await this.SpawnBotAsync(gameContext, loginName, slot).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups a share of the active bots into small hunting parties of level-wise similar characters.
    /// Members follow their leader, the elf heals the group, buffs are shared
    /// and the party experience bonus applies. The rest keeps hunting solo - and a bot on a market
    /// shift keeps selling (see <see cref="BotMarketShift"/>).
    /// </summary>
    /// <param name="gameContext">The game context (provides the party manager).</param>
    public async ValueTask FormPartiesAsync(IGameContext gameContext)
    {
        await this.RebalancePartiesAsync().ConfigureAwait(false);

        var candidates = this._bots.Values
            .Where(b => b.Party is null && b.Attributes is not null)
            .OrderBy(BotResetHandler.GetEffectiveLevel)
            .ToList();

        var now = DateTime.UtcNow;
        var formation = BotPartyComposer.Compose(candidates, bot => BotPartyIntentPlanner.GetIntent(bot, now), now);
        await this.CreatePartiesAsync(gameContext, formation.Plans).ConfigureAwait(false);
        this.LogWaitingBuffers(formation.UnplacedBuffers);
    }

    /// <summary>
    /// Gets the health supervisor of the given account, creating it on first use and dropping the ones which have
    /// been quiet for <see cref="BotHealthPolicy.ForgetAfter"/>. Without the pruning the map would grow with every
    /// account which ever ran a bot - with the presence rotation that is every bot, all the time.
    /// </summary>
    /// <param name="loginName">The account login name.</param>
    /// <returns>The account's health supervisor.</returns>
    internal BotHealthSupervisor HealthFor(string loginName)
    {
        this.PruneHealth();
        return this._health.GetOrAdd(loginName, _ => new BotHealthSupervisor());
    }

    private static string GetKey(string loginName, byte slot) => $"{loginName}/{slot}";

    /// <summary>
    /// Stops the bot like a regular logout (which saves its progress) and releases its resources.
    /// A stopped-but-not-disposed player keeps its persistence context - and with it the whole tracked
    /// account graph - alive; with the presence rotation stopping bots around the clock, that adds up
    /// to a leak. Mirrors the teardown of the <see cref="Offline.OfflinePlayerManager"/>: the removal
    /// from the game context normally happens through the disconnect event, and when the stop threw
    /// before it could raise that event, the disposal removes the bot as its own safety net (see
    /// <see cref="Player.DisposeAsyncCore"/>).
    /// </summary>
    private static async ValueTask StopAndDisposeAsync(BotPlayer bot, string key, string reason)
    {
        if (bot.IsDisposed || bot.IsDisposing)
        {
            // Already torn down concurrently (e.g. by the server shutdown disconnecting it) -
            // nothing left to stop or dispose.
            return;
        }

        try
        {
            await bot.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bot.Logger.LogError(ex, "Error while stopping bot '{Key}' ({Reason}).", key, reason);
        }
        finally
        {
            try
            {
                await bot.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposal must not fail the teardown - e.g. Npgsql throws when the underlying
                // connection is still opening while it is being closed during a shutdown race.
                bot.Logger.LogWarning(ex, "Error while disposing bot '{Key}' ({Reason}).", key, reason);
            }
        }
    }

    /// <summary>
    /// Drops the health supervisors which have nothing left to remember, so the per-account map doesn't grow with
    /// every account which ever ran a bot. Runs at most every five minutes, from the spawn path.
    /// </summary>
    private void PruneHealth()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < this._nextHealthPruneUtc)
        {
            return;
        }

        this._nextHealthPruneUtc = now + TimeSpan.FromMinutes(5);
        foreach (var (loginName, supervisor) in this._health)
        {
            if (supervisor.CanForget())
            {
                this._health.TryRemove(loginName, out _);
            }
        }
    }

    private async ValueTask CreatePartiesAsync(IGameContext gameContext, IReadOnlyList<BotPartyComposer.FormedParty> plans)
    {
        foreach (var plan in plans)
        {
            var members = plan.Members;
            var party = gameContext.PartyManager.CreateParty();
            var added = 0;

            // The goal is set before the first member joins: a merchant's next tick checks the party
            // goal when it decides whether its shift continues (see BotMarketPlanner.GetEndReason).
            foreach (var member in members)
            {
                member.PartyGoal = plan.Goal;
            }

            foreach (var member in members)
            {
                if (!await party.AddAsync(member).ConfigureAwait(false))
                {
                    break;
                }

                added++;
            }

            var leader = members[0];
            if (added < members.Count)
            {
                leader.Logger.LogWarning(
                    "Bot party around '{Leader}' lost {Missing} of {Count} members on join; they regroup on the next pass.",
                    leader.Name,
                    members.Count - added,
                    members.Count);

                for (var i = added; i < members.Count; i++)
                {
                    members[i].PartyGoal = BotPartyGoal.Experience;
                }
            }

            leader.Logger.LogDebug(
                "Formed bot party of {Count} around '{Leader}' (level {Level}, goal {Goal}).",
                added,
                leader.Name,
                BotResetHandler.GetEffectiveLevel(leader),
                plan.Goal);
        }
    }

    /// <summary>
    /// Dissolves bot-only parties which no longer fit their goal (a member logged out, the group
    /// out-leveled the gap, or the event, invasion or shift is over), so the next composition pass can
    /// regroup their members. Parties with a human are never touched.
    /// </summary>
    private async ValueTask RebalancePartiesAsync()
    {
        var now = DateTime.UtcNow;
        var parties = this._bots.Values
            .Select(bot => bot.Party)
            .OfType<Party>()
            .Distinct()
            .ToList();
        foreach (var party in parties)
        {
            if (!BotPartyComposer.IsStale(party, now))
            {
                continue;
            }

            var members = party.PartyList.OfType<OfflinePlayer>().ToList();
            var goal = members[0].PartyGoal;
            foreach (var member in members)
            {
                try
                {
                    await party.KickMySelfAsync(member).ConfigureAwait(false);
                    member.PartyGoal = BotPartyGoal.Experience;
                }
                catch (Exception ex)
                {
                    member.Logger.LogWarning(ex, "Bot '{Name}' couldn't leave its stale party for rebalancing.", member.Name);
                }
            }

            if (members.Count > 0)
            {
                members[0].Logger.LogDebug(
                    "Dissolved a stale bot party of {Count} around '{Leader}' for rebalancing (goal {Goal}).",
                    members.Count,
                    members[0].Name,
                    goal);
            }
        }
    }

    private void LogWaitingBuffers(IReadOnlyList<OfflinePlayer> waitingBuffers)
    {
        foreach (var buffer in waitingBuffers)
        {
            buffer.Logger.LogDebug("Buffer '{Name}' found no party and hunts solo.", buffer.Name);
        }
    }

    /// <summary>
    /// Drops a bot which never got as far as playing. It was already added to the game context if its
    /// initialization failed after <see cref="Player.SetSelectedCharacterAsync"/> was reached, and it is
    /// never disconnected on this path - the disposal takes it out of the player list (see
    /// <see cref="Player.DisposeAsyncCore"/>).
    /// </summary>
    /// <param name="key">The key of the bot.</param>
    /// <param name="bot">The bot to drop.</param>
    private async ValueTask RemoveAndDisposeAsync(string key, BotPlayer bot)
    {
        this._bots.TryRemove(key, out _);
        await bot.DisposeAsync().ConfigureAwait(false);
    }
}
