// <copyright file="AdminChangeConfirmation.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.DataProtection;
using Nito.AsyncEx;

/// <summary>Issues and verifies short-lived, single-use confirmations for previewed administrative changes.</summary>
/// <remarks>
/// <para>
/// A confirmation is a protected, self-contained token: the caller-bound digest of the operation, arguments and
/// complete change, its expiry and a nonce are serialized and encrypted with the host's data protection key ring.
/// Nothing is kept in a process-local dictionary, so a preview issued before a restart - or on another replica
/// which shares the key ring - can still be confirmed. Without a shared key ring a token from another instance is
/// refused, exactly as before.
/// </para>
/// <para>
/// Replay protection is per process: a consumed nonce is remembered until it expires. A token replayed on another
/// instance is bound to the operation's arguments and to the change it recorded, so it is only accepted again when
/// recomputing that change yields exactly the same values - otherwise the digest differs and the replay is refused.
/// Operations whose recorded change stays identical are idempotent by construction: repeating them writes the same
/// values again.
/// </para>
/// </remarks>
public sealed class AdminChangeConfirmation
{
    /// <summary>The upper bound of remembered nonces; a safety valve, not a usage limit.</summary>
    private const int MaximumRetainedConsumptions = 4096;

    /// <summary>The minimum interval between two full passes over the remembered nonces.</summary>
    private const int PruneIntervalSeconds = 60;

    private const string ProtectorPurpose = "MUnique.OpenMU.AdminPanel.ChangeConfirmations";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private long _nextPruneUtcTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminChangeConfirmation"/> class with an ephemeral key.
    /// Tokens of this instance are only valid for this instance; use the constructor which takes the host's
    /// data protection provider to share them across restarts and replicas.
    /// </summary>
    /// <param name="time">The clock, overridable for expiry tests.</param>
    public AdminChangeConfirmation(TimeProvider time)
        : this(time, new EphemeralDataProtectionProvider())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AdminChangeConfirmation"/> class.</summary>
    /// <param name="time">The clock, overridable for expiry tests.</param>
    /// <param name="dataProtectionProvider">
    /// The host's data protection provider. Its persisted key ring makes confirmations survive a restart and be
    /// accepted by other replicas which share it.
    /// </param>
    public AdminChangeConfirmation(TimeProvider time, IDataProtectionProvider dataProtectionProvider)
    {
        this._time = time;
        this._protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>Gets the configuration write gate. Acquire before loading state and hold through audit completion.</summary>
    /// <remarks>
    /// The gate serializes writes of this process. Across processes the durable boundaries keep the invariants:
    /// configuration edits run in a serializable transaction, offline character edits hold a login reservation,
    /// and live actions are validated by the owning game server.
    /// </remarks>
    internal AsyncLock Gate { get; } = new();

    /// <summary>Issues a single-use preview bound to the caller, operation, arguments and complete relevant state.</summary>
    /// <param name="actor">The authorized caller.</param>
    /// <param name="operation">The invariant operation name.</param>
    /// <param name="arguments">The exact validated operation arguments.</param>
    /// <param name="change">The safe proposed diff and state fingerprints.</param>
    internal JsonObject Preview(AdminActor actor, string operation, object arguments, JsonObject change)
    {
        this.PruneConsumed();
        var expires = this._time.GetUtcNow().AddMinutes(5);
        var confirmation = new Confirmation(
            Binding(actor, operation, arguments, change),
            expires,
            actor.Id,
            actor.Frontend,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var token = Convert.ToBase64String(this._protector.Protect(JsonSerializer.SerializeToUtf8Bytes(confirmation)));
        return new JsonObject
        {
            ["summary"] = "Preview only. Review the diff, then repeat exactly the same arguments with apply=true and the confirmationToken.",
            ["change"] = change.DeepClone(),
            ["affectedObjects"] = 1,
            ["restartRequired"] = !operation.StartsWith("characters_", StringComparison.Ordinal),
            ["restartGuidance"] = "Changes use the existing configuration notification path. Restart affected game servers when their subsystem does not reload these settings.",
            ["confirmationToken"] = token,
            ["expiresAt"] = expires.ToString("O"),
        };
    }

    /// <summary>Consumes a confirmation atomically; mismatches, expiry and replays fail closed.</summary>
    /// <param name="actor">The authorized caller.</param>
    /// <param name="operation">The invariant operation name.</param>
    /// <param name="arguments">The exact validated operation arguments.</param>
    /// <param name="change">The safe proposed diff and state fingerprints.</param>
    /// <param name="token">The single-use confirmation token.</param>
    internal void Consume(AdminActor actor, string operation, object arguments, JsonObject change, string? token)
    {
        if (this.Read(token) is not { } confirmation
            || confirmation.ExpiresAt <= this._time.GetUtcNow()
            || confirmation.Digest != Binding(actor, operation, arguments, change))
        {
            throw InvalidConfirmation();
        }

        this.PruneConsumed();
        if (!this._consumed.TryAdd(confirmation.Nonce, confirmation.ExpiresAt))
        {
            throw InvalidConfirmation();
        }
    }

    private static string Binding(AdminActor actor, string operation, object arguments, JsonObject change)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { actor.Id, actor.Frontend, operation, arguments, change }))));

    private static AdminOperationException InvalidConfirmation()
        => new("The confirmation is invalid, expired, already used, or the state changed. Request a new preview.");

    private void PruneConsumed()
    {
        var now = this._time.GetUtcNow();
        var overLimit = this._consumed.Count > MaximumRetainedConsumptions;
        if (!overLimit && now.UtcTicks < Interlocked.Read(ref this._nextPruneUtcTicks))
        {
            return;
        }

        Interlocked.Exchange(ref this._nextPruneUtcTicks, now.AddSeconds(PruneIntervalSeconds).UtcTicks);
        foreach (var entry in this._consumed.Where(p => p.Value <= now).ToArray())
        {
            this._consumed.TryRemove(entry.Key, out _);
        }

        if (this._consumed.Count <= MaximumRetainedConsumptions)
        {
            return;
        }

        // Bound the memory by forgetting the nonces closest to their expiry first. A token which expired in the
        // meantime is refused by Read anyway; only one which is still valid loses its replay protection early.
        foreach (var entry in this._consumed.OrderBy(p => p.Value).Take(this._consumed.Count - MaximumRetainedConsumptions).ToArray())
        {
            this._consumed.TryRemove(entry.Key, out _);
        }
    }

    private Confirmation? Read(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var payload = this._protector.Unprotect(Convert.FromBase64String(token));
            return JsonSerializer.Deserialize<Confirmation>(payload);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record Confirmation(string Digest, DateTimeOffset ExpiresAt, string ActorId, string Frontend, string Nonce);
}
