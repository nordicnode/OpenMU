// <copyright file="Index.razor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Pages;

using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Web.AdminPanel.Operations;
using MUnique.OpenMU.Web.AdminPanel.Properties;
using MUnique.OpenMU.Web.Shared.Services;

/// <summary>
/// The start page of the admin panel, which shows a dashboard with statistics about the server.
/// </summary>
public partial class Index : IDisposable
{
    private IList<IManageableServer> _servers = [];

    private AccountStatistics? _accountStatistics;

    private GuildStatistics? _guildStatistics;

    private bool _isLoadingStatistics = true;

    private bool _isGlobalMessageAvailable;

    private string? _stuckBotsAlarm;

    private int? _botCount;

    private DateTime? _statisticsLoadedAtUtc;

    private bool _isBotFeatureAvailable;

    [Inject]
    private IServerProvider ServerProvider { get; set; } = null!;

    [Inject]
    private BotAccountService BotService { get; set; } = null!;

    [Inject]
    private ServerReadOperations ReadOperations { get; set; } = null!;

    [Inject]
    private IServiceProvider ServiceProvider { get; set; } = null!;

    [Inject]
    private ILogger<Index> Logger { get; set; } = null!;

    /// <inheritdoc />
    public void Dispose()
    {
        this.ServerProvider.PropertyChanged -= this.OnServerProviderPropertyChanged;
        this.UnsubscribeFromServers();
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync().ConfigureAwait(true);
        this.ServerProvider.PropertyChanged += this.OnServerProviderPropertyChanged;
        this.SubscribeToServers();

        // The game servers are only available in the all-in-one deployment.
        this._isGlobalMessageAvailable = this.ServiceProvider.GetService<IDictionary<int, IGameServer>>() is not null;
        this._isBotFeatureAvailable = this.BotService.IsBotFeatureAvailable();

        await this.LoadStatisticsAsync().ConfigureAwait(true);
    }

    private static string GetLoadText(IManageableServer server)
    {
        if (server.ServerState == ServerState.Timeout || server.Type is ServerType.Discord or ServerType.Undefined)
        {
            return Resources.DashboardNotAvailable;
        }

        return server.Type == ServerType.GameServer && server.MaximumConnections < int.MaxValue
            ? $"{server.CurrentConnections:N0} / {server.MaximumConnections:N0}"
            : string.Format(Resources.DashboardConnections, server.CurrentConnections.ToString("N0"));
    }

    private static string GetServerStateClass(ServerState state)
    {
        return state switch
        {
            ServerState.Started => "server-state--running",
            ServerState.Stopped or ServerState.Timeout => "server-state--stopped",
            _ => "server-state--pending",
        };
    }

    private string GetStatisticText(int? value)
    {
        if (value is { } notNull)
        {
            return notNull.ToString("N0");
        }

        return this._isLoadingStatistics ? "…" : Resources.DashboardNotAvailable;
    }

    private string GetGuildDetails()
    {
        var details = new List<string>(2);
        if (this._guildStatistics is { } guilds)
        {
            details.Add(string.Format(Resources.DashboardAlliances, guilds.AllianceCount.ToString("N0")));
        }

        if (this._accountStatistics is { } accounts)
        {
            details.Add(string.Format(Resources.DashboardCharacters, accounts.CharacterCount.ToString("N0")));
        }

        return string.Join(" · ", details);
    }

    private async Task LoadStatisticsAsync()
    {
        try
        {
            this._accountStatistics = await this.ReadOperations.ReadAccountStatisticsAsync(7, default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            this.Logger.LogWarning(ex, "Couldn't load the account statistics.");
        }

        try
        {
            this._guildStatistics = await this.ReadOperations.ReadGuildStatisticsAsync(default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            this.Logger.LogWarning(ex, "Couldn't load the guild statistics.");
        }

        try
        {
            // Bot data is only available from in-process game servers.
            if (this._isBotFeatureAvailable)
            {
                this._botCount = (await this.BotService.GetAsync(0, int.MaxValue).ConfigureAwait(true)).Count;
                this._stuckBotsAlarm = this.BotService.StuckAlarm;
            }
        }
        catch (Exception ex)
        {
            this.Logger.LogWarning(ex, "Couldn't load the bot alarm.");
        }

        this._statisticsLoadedAtUtc = DateTime.UtcNow;
        this._isLoadingStatistics = false;
    }

    private async Task RefreshStatisticsAsync()
    {
        if (this._isLoadingStatistics)
        {
            return;
        }

        this._isLoadingStatistics = true;
        this._accountStatistics = null;
        this._guildStatistics = null;
        this._botCount = null;
        this._stuckBotsAlarm = null;
        await this.LoadStatisticsAsync().ConfigureAwait(true);
    }

    private void OnServerProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IServerProvider.Servers))
        {
            return;
        }

        this.UnsubscribeFromServers();
        this.SubscribeToServers();
        _ = this.InvokeAsync(this.StateHasChanged);
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = this.InvokeAsync(this.StateHasChanged);
    }

    private void SubscribeToServers()
    {
        this._servers = this.ServerProvider.Servers;
        foreach (var server in this._servers)
        {
            server.PropertyChanged += this.OnServerPropertyChanged;
        }
    }

    private void UnsubscribeFromServers()
    {
        foreach (var server in this._servers)
        {
            server.PropertyChanged -= this.OnServerPropertyChanged;
        }
    }
}
