// <copyright file="BotChatCategory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// The topic of a bot chat line. A bot keeps a cooldown per topic, so it doesn't greet, congratulate or
/// comment on wings again and again - even when the players or places differ.
/// </summary>
public enum BotChatCategory
{
    /// <summary>
    /// A conversation between two bots near a player.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Conversation_Name))]
    Conversation,

    /// <summary>
    /// A greeting when a player enters the game.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Greeting_Name))]
    Greeting,

    /// <summary>
    /// A congratulation for a level-up or a reset.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Congratulation_Name))]
    Congratulation,

    /// <summary>
    /// A comment on the bot's own level milestone.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_LevelMilestone_Name))]
    LevelMilestone,

    /// <summary>
    /// A comment on wings, own ones or those of a player.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Wings_Name))]
    Wings,

    /// <summary>
    /// A reminder of an opened event in a town.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_EventReminder_Name))]
    EventReminder,

    /// <summary>
    /// A call-out of a notable monster such as a golden one.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_NotableMonster_Name))]
    NotableMonster,

    /// <summary>
    /// An advert of a merchant bot.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Merchant_Name))]
    Merchant,

    /// <summary>
    /// A comment on an excellent or ancient item a player's kill dropped.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_RareDrop_Name))]
    RareDrop,

    /// <summary>
    /// A comment after a player killed the bot.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Killed_Name))]
    Killed,

    /// <summary>
    /// A thank-you to a player who killed a monster next to a bot in trouble.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatCategory_Rescue_Name))]
    Rescue,
}
