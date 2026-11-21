// <copyright file="CharacterWriteOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>Edits offline characters under an exclusive login-server reservation.</summary>
/// <param name="persistence">The persistence provider.</param>
/// <param name="configurations">The configuration source.</param>
/// <param name="login">The login coordinator.</param>
/// <param name="executor">The audit executor.</param>
/// <param name="confirmations">The preview coordinator.</param>
/// <param name="settings">The write policy.</param>
public sealed class CharacterWriteOperations(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations, ILoginServer login, AdminOperationExecutor executor, AdminChangeConfirmation confirmations, AdminWriteSettings settings)
{
    private static readonly IReadOnlyDictionary<string, AttributeDefinition> EditableStats = new Dictionary<string, AttributeDefinition>(StringComparer.Ordinal)
    {
        ["level"] = Stats.Level, ["masterLevel"] = Stats.MasterLevel, ["resets"] = Stats.Resets,
        ["strength"] = Stats.BaseStrength, ["agility"] = Stats.BaseAgility, ["vitality"] = Stats.BaseVitality,
        ["energy"] = Stats.BaseEnergy, ["leadership"] = Stats.BaseLeadership,
    };

    /// <summary>Updates level, stats, money or position of an offline character after preview.</summary>
    /// <param name="characterName">The exact character name.</param>
    /// <param name="patch">JSON object: level, masterLevel, resets, strength, agility, vitality, energy, leadership, money, mapNumber, x, y.</param>
    /// <param name="apply">Whether to apply the confirmed preview.</param>
    /// <param name="confirmationToken">The preview token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The preview or applied result.</returns>
    [AdminOperation(AdminRole.Operator, ReadOnly = false)]
    public ValueTask<JsonObject> UpdateAsync(string characterName, string patch, bool apply, string? confirmationToken, CancellationToken cancellationToken)
        => this.ChangeAsync(characterName, patch, null, 0, false, apply, confirmationToken, cancellationToken);

    /// <summary>Adds one plain item to an offline character's bag or vault, respecting item dimensions.</summary>
    /// <param name="characterName">The exact character name.</param>
    /// <param name="itemId">The item definition identifier.</param>
    /// <param name="level">The item level, bounded by the definition.</param>
    /// <param name="vault">Whether to use the account vault instead of the bag.</param>
    /// <param name="apply">Whether to apply the preview.</param>
    /// <param name="confirmationToken">The preview token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The proposed slot and item, or applied result.</returns>
    [AdminOperation(AdminRole.Operator, ReadOnly = false)]
    public ValueTask<JsonObject> AddItemAsync(string characterName, Guid itemId, byte level, bool vault, bool apply, string? confirmationToken, CancellationToken cancellationToken)
        => this.ChangeAsync(characterName, "{}", itemId, level, vault, apply, confirmationToken, cancellationToken);

    /// <summary>
    /// Reverts the audited field values of a previous <c>characters_update</c>, refusing the reversal when the
    /// character no longer matches what that change left behind.
    /// </summary>
    /// <param name="entry">The successful audit entry to reverse.</param>
    /// <param name="apply">Whether to apply the reversal.</param>
    /// <param name="confirmationToken">The reversal preview token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The preview or applied reversal.</returns>
    internal ValueTask<JsonObject> RevertAsync(AdminAuditEntry entry, bool apply, string? confirmationToken, CancellationToken cancellationToken)
    {
        var change = JsonNode.Parse(entry.ResultJson)?["change"] as JsonObject;
        var recorded = JsonNode.Parse(entry.ArgumentsJson);
        var characterName = (recorded?["arguments"] ?? recorded)?["characterName"]?.GetValue<string>();
        if (change is not { } reversible || reversible["kind"]?.GetValue<string>() != "character" || change?["before"] is not JsonObject || change["after"]?["requestedValues"] is not JsonObject)
        {
            throw new AdminOperationException("This audit entry has no complete reversible character diff. Only field updates of a character can be reverted.");
        }

        if (string.IsNullOrEmpty(characterName))
        {
            throw new AdminOperationException("The audit entry does not name the changed character. Revert refused.");
        }

        return this.ChangeAsync(characterName, "{}", null, 0, false, apply, confirmationToken, cancellationToken, "audit_revert", reversible);
    }

    private static JsonObject Snapshot(Character character) => new()
    {
        ["id"] = character.Id.ToString(),
        ["stats"] = JsonSerializer.SerializeToNode(character.Attributes.OrderBy(a => a.Definition.Id).Select(a => new { id = a.Definition.Id, value = a.Value })),
        ["money"] = character.Inventory?.Money, ["mapId"] = character.CurrentMap?.GetId().ToString(),
        ["x"] = character.PositionX, ["y"] = character.PositionY,
    };

    private static void ValidateStats(JsonObject patch, Character character, GameConfiguration config)
    {
        foreach (var (field, value) in patch)
        {
            if (EditableStats.TryGetValue(field, out var definition))
            {
                var existing = character.Attributes.SingleOrDefault(a => a.Definition.Id == definition.Id);
                var maximum = field == "level" ? config.MaximumLevel : field == "masterLevel" ? config.MaximumMasterLevel : existing?.Definition.MaximumValue ?? int.MaxValue;
                if (value is not JsonValue scalar || !scalar.TryGetValue<int>(out var number) || number < (field == "level" ? 1 : 0) || number > maximum
                    || existing is null)
                {
                    throw new AdminOperationException("A stat is missing or outside its configured range. Use characters_get to inspect it.", AdminOperationErrorKind.InvalidInput);
                }
            }
            else if (field is "money" or "mapNumber" or "x" or "y")
            {
                if (value is not JsonValue scalar || !scalar.TryGetValue<int>(out var number) || number < 0
                    || (field is "x" or "y" && number > byte.MaxValue) || (field == "money" && number > config.MaximumInventoryMoney)
                    || (field == "mapNumber" && config.Maps.All(m => m.Number != number)))
                {
                    throw new AdminOperationException("Money or position is outside its configured range.", AdminOperationErrorKind.InvalidInput);
                }
            }
            else
            {
                throw new AdminOperationException($"Unknown {nameof(character)} field. Allowed fields: level, masterLevel, resets, strength, agility, vitality, energy, leadership, money, mapNumber, x, y.", AdminOperationErrorKind.InvalidInput);
            }
        }
    }

    private static void ApplyFieldToSnapshot(JsonObject snapshot, string field, JsonNode? value, GameConfiguration config)
    {
        if (EditableStats.TryGetValue(field, out var definition))
        {
            var stat = (snapshot["stats"] as JsonArray)?.FirstOrDefault(s => s?["id"]?.GetValue<Guid>() == definition.Id)
                ?? throw new AdminOperationException("The recorded character state is incomplete. Revert refused.");
            stat["value"] = value!.GetValue<int>();
        }
        else if (field == "mapNumber")
        {
            var map = config.Maps.SingleOrDefault(m => m.Number == value!.GetValue<int>())
                ?? throw new AdminOperationException("The recorded map no longer exists. Revert refused.");
            snapshot["mapId"] = map.GetId().ToString();
        }
        else
        {
            snapshot[field] = value!.DeepClone();
        }
    }

    private static (JsonObject RevertPatch, JsonObject ExpectedState) BuildRevert(JsonObject auditChange, GameConfiguration config)
    {
        var before = auditChange["before"]!.AsObject();
        var requested = auditChange["after"]!["requestedValues"]!.AsObject();
        var revert = new JsonObject();
        if (before["stats"] is JsonArray stats)
        {
            foreach (var (field, definition) in EditableStats)
            {
                if (stats.FirstOrDefault(s => s?["id"]?.GetValue<Guid>() == definition.Id) is { } stat)
                {
                    revert[field] = (int)(stat["value"] ?? stat["Value"])!.GetValue<float>();
                }
            }
        }

        if (!revert.ContainsKey("level"))
        {
            throw new AdminOperationException("The recorded character state is incomplete. Revert refused.");
        }

        foreach (var field in new[] { "money", "x", "y" })
        {
            if (before[field] is { } value)
            {
                revert[field] = value.GetValue<int>();
            }
            else if (requested.ContainsKey(field))
            {
                throw new AdminOperationException($"The recorded character had no {field}, which cannot be restored. Revert refused.");
            }
        }

        if (before["mapId"]?.GetValue<string>() is { } mapIdText)
        {
            if (!Guid.TryParse(mapIdText, out var mapId) || config.Maps.FirstOrDefault(m => m.GetId() == mapId) is not { } map)
            {
                throw new AdminOperationException("The recorded map no longer exists. Revert refused.");
            }

            revert["mapNumber"] = map.Number;
        }
        else if (requested.ContainsKey("mapNumber"))
        {
            throw new AdminOperationException("The recorded character had no map, which cannot be restored. Revert refused.");
        }

        var expected = before.DeepClone().AsObject();
        foreach (var (field, value) in requested)
        {
            ApplyFieldToSnapshot(expected, field, value, config);
        }

        return (revert, expected);
    }

    private async ValueTask<JsonObject> ChangeAsync(string characterName, string patch, Guid? itemId, byte level, bool vault, bool apply, string? token, CancellationToken cancellationToken, string operation = "", JsonObject? revertFrom = null)
    {
        var actor = await AdminChangeExecution.RequireAsync(executor, settings, AdminRoles.Operator, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        cancellationToken = timeout.Token;
        using var gate = apply ? await confirmations.Gate.LockAsync(cancellationToken).ConfigureAwait(false) : null;
        using var read = new ConfigurationReadScope();
        await read.LoadAsync(persistence, configurations, cancellationToken).ConfigureAwait(false);
        var config = read.Configuration;
        using var context = persistence.CreateNewPlayerContext(config);
        var account = await context.GetAccountByCharacterNameAsync(characterName, cancellationToken).ConfigureAwait(false)
            ?? throw new AdminOperationException("Character not found.", AdminOperationErrorKind.InvalidInput);
        if (account.State is not (AccountState.Normal or AccountState.Banned or AccountState.TemporarilyBanned))
        {
            await executor.RequireAsync(AdminRoles.Administrator, cancellationToken).ConfigureAwait(false);
        }

        var reservationId = RandomNumberGenerator.GetInt32(byte.MaxValue + 1, int.MaxValue);
        JsonObject? result = null;
        Exception? operationFailure = null;
        try
        {
            if (!await login.TryReserveAccountAsync(account.LoginName, reservationId).ConfigureAwait(false))
            {
                throw new AdminOperationException("The account is online, reserved, or its login state is unavailable. Disconnect it first; in distributed deployments, log in and out once to initialize a new account's login state.");
            }

            // Reload only after the login reservation: an online session may have saved just before it was acquired.
            using var edit = persistence.CreateNewPlayerContext(config);
            account = await edit.GetAccountByCharacterNameAsync(characterName, cancellationToken).ConfigureAwait(false)
                ?? throw new AdminOperationException("Character no longer exists.");
            var character = account.Characters.Single(c => c.Name == characterName);
            if (account.State is not (AccountState.Normal or AccountState.Banned or AccountState.TemporarilyBanned) || character.CharacterStatus == CharacterStatus.GameMaster)
            {
                await executor.RequireAsync(AdminRoles.Administrator, cancellationToken).ConfigureAwait(false);
            }

            var before = Snapshot(character);
            before["accountState"] = account.State.ToString();
            before["characterStatus"] = character.CharacterStatus.ToString();
            var changes = ConfigurationPatch.Parse(patch);
            if (revertFrom is not null)
            {
                (changes, var expectedState) = BuildRevert(revertFrom, config);
                if (!JsonNode.DeepEquals(before, expectedState))
                {
                    throw new AdminOperationException("The character changed after the recorded change. Revert refused; inspect the current character state.");
                }
            }

            ValidateStats(changes, character, config);
            var after = before.DeepClone().AsObject();
            ItemStorage? storage = null;
            DataModel.Configuration.Items.ItemDefinition? definition = null;
            byte? slot = null;
            if (itemId is { } definitionId)
            {
                definition = config.Items.SingleOrDefault(d => d.GetId() == definitionId)
                    ?? throw new AdminOperationException("Item definition not found.", AdminOperationErrorKind.InvalidInput);
                if (level > definition.MaximumItemLevel)
                {
                    throw new AdminOperationException("The requested item level exceeds its definition's maximum.", AdminOperationErrorKind.InvalidInput);
                }

                storage = vault ? account.Vault : character.Inventory;
                if (storage is null)
                {
                    throw new AdminOperationException("The character has no inventory or the account has no vault. Open it in game first.");
                }

                // Use detached storage/items for placement checks: Storage repairs malformed layouts in its constructor.
                // An item which finds no place, e.g. because its definition grew, waits for space in the inventory
                // when the storage is loaded in the game, so it takes no place here.
                var detached = new PreviewStorage(storage);
                Func<Item, bool> ignoreItemWithoutPlace = _ => false;
                var grid = vault ? new Storage(InventoryConstants.WarehouseSize, 0, 0, detached, ignoreItemWithoutPlace)
                    : new Storage(
                        InventoryConstants.GetInventorySize(0),
                        InventoryConstants.EquippableSlotsCount,
                        0,
                        new ItemStorageAdapter(detached, 0, InventoryConstants.GetInventorySize(0)),
                        ignoreItemWithoutPlace);
                slot = grid.CheckInvSpace(new Item { Definition = definition, Level = level });
                if (slot is null)
                {
                    throw new AdminOperationException("The base bag or vault has no free rectangle for this item. Free space first.");
                }

                before["storage"] = JsonSerializer.SerializeToNode(storage.Items.OrderBy(i => i.ItemSlot).Select(i => new { id = i.GetId(), i.ItemSlot, definitionId = i.Definition?.GetId(), i.Level, i.Durability }));
                after["addItem"] = JsonSerializer.SerializeToNode(new { itemId, level, vault, slot });
            }
            else
            {
                after["requestedValues"] = changes.DeepClone();
            }

            var operationName = string.IsNullOrEmpty(operation) ? itemId is null ? "characters_update" : "characters_add_item" : operation;
            result = await AdminChangeExecution.RunAsync(
                executor,
                confirmations,
                actor,
                AdminRoles.Operator,
                operationName,
                new { characterName, patch = revertFrom is null ? (object)patch : revertFrom, itemId, level, vault },
                new JsonObject { ["kind"] = "character", ["before"] = before, ["after"] = after },
                apply,
                token,
                async () =>
                {
                    foreach (var (field, value) in changes)
                    {
                        var number = value!.GetValue<int>();
                        if (EditableStats.TryGetValue(field, out var stat))
                        {
                            character.Attributes.Single(a => a.Definition.Id == stat.Id).Value = number;
                        }
                        else
                        {
                            switch (field)
                            {
                                case "money":
                                    (character.Inventory ?? throw new AdminOperationException("Inventory is missing.")).Money = number;
                                    break;
                                case "mapNumber":
                                    character.CurrentMap = config.Maps.Single(m => m.Number == number);
                                    break;
                                case "x":
                                    character.PositionX = (byte)number;
                                    break;
                                case "y":
                                    character.PositionY = (byte)number;
                                    break;
                                default:
                                    throw new AdminOperationException("Unknown character field.", AdminOperationErrorKind.InvalidInput);
                            }
                        }
                    }

                    if (definition is not null && storage is not null && slot is { } freeSlot)
                    {
                        var item = edit.CreateNew<Item>();
                        item.Definition = definition;
                        item.Level = level;
                        item.Durability = definition.Durability;
                        item.ItemSlot = freeSlot;
                        storage.Items.Add(item);
                    }

                    await AdminChangeExecution.SaveAsync(edit, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            // Also release after a lost reserve response; ownership checking makes this safe for online accounts.
            await login.ReleaseAccountReservationAsync(account.LoginName, reservationId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new AdminOperationException(
                "The offline edit or preview finished, but releasing its login reservation could not be confirmed. Check the current state and login coordination before retrying.",
                AdminOperationErrorKind.Uncertain,
                operationFailure is null ? exception : new AggregateException(operationFailure, exception));
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return result ?? throw new InvalidOperationException("The offline operation produced no result.");
    }

    private sealed class PreviewStorage : ItemStorage
    {
        internal PreviewStorage(ItemStorage source)
        {
            this.Items = source.Items.Select(i => new Item { Definition = i.Definition, ItemSlot = i.ItemSlot, Level = i.Level }).ToList();
        }
    }
}
