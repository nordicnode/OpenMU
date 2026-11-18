// <copyright file="BotPersona.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The play style a bot leans towards. It does not change what the bot is allowed to do - it shifts a few
/// bounded dials (how much of a beating it accepts, when it heads to a merchant, how quickly it answers)
/// so the population does not all behave like the same character.
/// </summary>
internal enum BotPersona
{
    /// <summary>The middle of the road: the behavior of the bots before personas existed.</summary>
    Balanced,

    /// <summary>Fights tougher monsters and accepts more damage per hit.</summary>
    Aggressive,

    /// <summary>Fights easier monsters and keeps more headroom.</summary>
    Cautious,

    /// <summary>Goes to a merchant earlier to sell and restock.</summary>
    Trader,

    /// <summary>Answers invitations and chat faster.</summary>
    Social,
}
