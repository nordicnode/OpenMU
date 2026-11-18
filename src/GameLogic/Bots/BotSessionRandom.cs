// <copyright file="BotSessionRandom.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The random source of one bot session. Its seed is derived from the bot's identity (and an optional
/// server salt) and logged when the bot starts, so a report about a patterned bot can be replayed exactly
/// by seeding the same value: same persona, same ground picks, same reaction delays.
/// </summary>
/// <remarks>
/// A deterministic sequence is the point: sharing the engine's process-wide <see cref="Rand"/> made a
/// misbehaving bot impossible to reproduce, because the values it drew depended on every other bot on the
/// server. The generator is the well-known xorshift64* - small, fast and good enough for behavior noise,
/// not for anything security related. Access is serialized, because a bot's ticks and party requests may
/// arrive on different threads; the lock only covers the handful of arithmetic operations.
/// </remarks>
internal sealed class BotSessionRandom : IRandomizer
{
    private readonly object _gate = new();
    private ulong _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotSessionRandom"/> class.
    /// </summary>
    /// <param name="seed">The session seed; 0 is replaced by a fixed constant, so the sequence never dies.</param>
    internal BotSessionRandom(ulong seed)
    {
        this.Seed = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        this._state = this.Seed;
    }

    /// <summary>
    /// Gets the seed the sequence started from. Log it before a bot acts: it is all a report needs to be
    /// replayed.
    /// </summary>
    internal ulong Seed { get; }

    /// <inheritdoc />
    public bool NextRandomBool() => this.NextDouble() < 0.5;

    /// <inheritdoc />
    public bool NextRandomBool(int percent) => this.NextInt(0, 100) < Math.Clamp(percent, 0, 100);

    /// <inheritdoc />
    public bool NextRandomBool(int chance, int basis)
        => basis > 0 && this.NextInt(0, basis) < Math.Clamp(chance, 0, basis);

    /// <inheritdoc />
    public bool NextRandomBool(double chance) => this.NextDouble() < Math.Clamp(chance, 0.0, 1.0);

    /// <inheritdoc />
    public int NextInt(int min, int max)
    {
        if (max <= min)
        {
            return min;
        }

        return (int)(min + (long)(this.NextDouble() * (max - (long)min)));
    }

    /// <inheritdoc />
    public int NextInt(uint min, uint max)
    {
        if (max <= min)
        {
            return (int)min;
        }

        return (int)(min + (uint)(this.NextDouble() * (max - (double)min)));
    }

    /// <inheritdoc />
    public uint NextUInt(uint min, uint max) => (uint)this.NextInt(min, max);

    /// <inheritdoc />
    public double NextDouble()
    {
        lock (this._gate)
        {
            // xorshift64*: 53 significant bits, which is what a double can carry without artifacts.
            var x = this._state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            this._state = x;
            return ((x * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
        }
    }

    /// <summary>
    /// Draws a delay between two bounds, for the human-like reaction times.
    /// </summary>
    /// <param name="min">The inclusive minimum.</param>
    /// <param name="max">The exclusive maximum.</param>
    /// <returns>The delay.</returns>
    internal TimeSpan NextTimeSpan(TimeSpan min, TimeSpan max)
        => max <= min ? min : min + TimeSpan.FromMilliseconds(this.NextDouble() * (max - min).TotalMilliseconds);
}
