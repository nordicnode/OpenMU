// <copyright file="BotChatTopicCooldown.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The minimum time between two unasked bot chat lines of one topic, e.g. two greetings. The admin
/// sets one entry per topic in the bot chat configuration.
/// </summary>
public class BotChatTopicCooldown
{
    /// <summary>
    /// Gets or sets the topic of the line.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatTopicCooldown_Category_Name), Description = nameof(PlugInResources.BotChatTopicCooldown_Category_Description))]
    public BotChatCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the minimum time between two unasked lines of the topic by the same bot;
    /// 0 switches the cooldown off.
    /// </summary>
    [Display(ResourceType = typeof(PlugInResources), Name = nameof(PlugInResources.BotChatTopicCooldown_Cooldown_Name), Description = nameof(PlugInResources.BotChatTopicCooldown_Cooldown_Description))]
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);
}
