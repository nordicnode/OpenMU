// <copyright file="ChangeConfirmationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Web.AdminPanel.Auth;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>Verifies expiry, single-use confirmation and strict allowlist matching.</summary>
[TestFixture]
public sealed class ChangeConfirmationTests
{
    /// <summary>Expiry requires a fresh preview.</summary>
    [Test]
    public void ExpiredConfirmationsAreRefused()
    {
        var clock = new MutableClock();
        var confirmations = new AdminChangeConfirmation(clock);
        var actor = new AdminActor("key", "admin", "mcp", [AdminRoles.Administrator]);
        var change = new JsonObject { ["before"] = 1, ["after"] = 2 };
        var preview = confirmations.Preview(actor, "config_update", new { id = 1 }, change);
        var token = preview["confirmationToken"]!.GetValue<string>();
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Throws<AdminOperationException>(() => confirmations.Consume(actor, "config_update", new { id = 1 }, change, token));
    }

    /// <summary>A preview survives a host restart when the data protection key ring is persisted.</summary>
    [Test]
    public void RestartedHostWithTheSameKeyRingAcceptsConfirmations()
    {
        var provider = new EphemeralDataProtectionProvider();
        var actor = new AdminActor("key", "admin", "mcp", [AdminRoles.Administrator]);
        var change = new JsonObject { ["before"] = 1, ["after"] = 2 };
        var beforeRestart = new AdminChangeConfirmation(TimeProvider.System, provider);
        var token = beforeRestart.Preview(actor, "config_update", new { id = 1 }, change)["confirmationToken"]!.GetValue<string>();

        var afterRestart = new AdminChangeConfirmation(TimeProvider.System, provider);
        Assert.DoesNotThrow(() => afterRestart.Consume(actor, "config_update", new { id = 1 }, change, token));
    }

    /// <summary>A host without the issuing key ring refuses the token instead of trusting its payload.</summary>
    [Test]
    public void HostWithoutTheKeyRingRefusesConfirmations()
    {
        var actor = new AdminActor("key", "admin", "mcp", [AdminRoles.Administrator]);
        var change = new JsonObject { ["before"] = 1, ["after"] = 2 };
        var issuing = new AdminChangeConfirmation(TimeProvider.System, new EphemeralDataProtectionProvider());
        var token = issuing.Preview(actor, "config_update", new { id = 1 }, change)["confirmationToken"]!.GetValue<string>();
        var other = new AdminChangeConfirmation(TimeProvider.System, new EphemeralDataProtectionProvider());
        Assert.Throws<AdminOperationException>(() => other.Consume(actor, "config_update", new { id = 1 }, change, token));
    }

    /// <summary>A forged or truncated token is refused.</summary>
    [Test]
    public void MalformedConfirmationsAreRefused()
    {
        var confirmations = new AdminChangeConfirmation(TimeProvider.System);
        var actor = new AdminActor("key", "admin", "mcp", [AdminRoles.Administrator]);
        Assert.Throws<AdminOperationException>(() => confirmations.Consume(actor, "config_update", new { id = 1 }, new JsonObject(), "not-a-token"));
        Assert.Throws<AdminOperationException>(() => confirmations.Consume(actor, "config_update", new { id = 1 }, new JsonObject(), Convert.ToBase64String([1, 2, 3, 4])));
        Assert.Throws<AdminOperationException>(() => confirmations.Consume(actor, "config_update", new { id = 1 }, new JsonObject(), string.Empty));
    }

    /// <summary>A token is bound to the recorded change, so a recomputed change which differs is refused.</summary>
    [Test]
    public void RefusesConfirmationsForAChangedState()
    {
        var confirmations = new AdminChangeConfirmation(TimeProvider.System);
        var actor = new AdminActor("key", "admin", "mcp", [AdminRoles.Administrator]);
        var change = new JsonObject { ["kind"] = "character", ["before"] = new JsonObject { ["money"] = 0, ["stats"] = new JsonArray() }, ["after"] = new JsonObject { ["money"] = 100 } };
        var token = confirmations.Preview(actor, "characters_add_item", new { characterName = "Hero" }, change)["confirmationToken"]!.GetValue<string>();

        // Recomputing the preview after the state changed yields another change; its digest no longer matches.
        var laterChange = change.DeepClone().AsObject();
        laterChange["before"]!["money"] = 100;
        Assert.Throws<AdminOperationException>(() => confirmations.Consume(actor, "characters_add_item", new { characterName = "Hero" }, laterChange, token));
    }

    /// <summary>Even simultaneous matching applies can consume a token only once.</summary>
    [Test]
    public async Task ConfirmationIsAtomicallySingleUseAsync()
    {
        var confirmations = new AdminChangeConfirmation(TimeProvider.System);
        var actor = new AdminActor("key", "admin", "mcp", [AdminRoles.Administrator]);
        var change = new JsonObject { ["before"] = 1, ["after"] = 2 };
        var token = confirmations.Preview(actor, "config_update", new { id = 1 }, change)["confirmationToken"]!.GetValue<string>();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                confirmations.Consume(actor, "config_update", new { id = 1 }, change, token);
                return true;
            }
            catch (AdminOperationException)
            {
                return false;
            }
        }))).ConfigureAwait(false);
        Assert.That(results.Count(r => r), Is.EqualTo(1));
    }

    /// <summary>Only exact names and explicit area wildcards narrow a key.</summary>
    [TestCase("", "config_update", true)]
    [TestCase("players_*, server_overview", "players_ban", true)]
    [TestCase("players_*, server_overview", "server_stop", false)]
    [TestCase("accounts_get", "accounts_get_extra", false)]
    [TestCase("castle_siege_*", "castle_siege_queue_state", true)]
    [TestCase("*", "config_update", false)]
    [TestCase("config_*,", "config_update", false)]
    [TestCase("config*", "config_update", false)]
    public void ToolGroupsFailClosed(string groups, string tool, bool allowed)
        => Assert.That(AdminToolGroups.Allows(groups, tool), Is.EqualTo(allowed));

    /// <summary>Issuing previews keeps no state, so earlier tokens stay valid and each token is unique.</summary>
    [Test]
    public void PreviewIssuingKeepsEarlierTokensUsable()
    {
        var confirmations = new AdminChangeConfirmation(TimeProvider.System);
        var actor = new AdminActor("busy-key", "admin", "mcp", [AdminRoles.Administrator]);
        var change = new JsonObject();
        var first = confirmations.Preview(actor, "config_update", new { id = 1 }, change)["confirmationToken"]!.GetValue<string>();
        var tokens = Enumerable.Range(0, 100)
            .Select(_ => confirmations.Preview(actor, "config_update", new { id = 1 }, change)["confirmationToken"]!.GetValue<string>())
            .ToList();
        Assert.That(tokens.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(tokens.Count));
        Assert.DoesNotThrow(() => confirmations.Consume(actor, "config_update", new { id = 1 }, change, first));
    }

    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
