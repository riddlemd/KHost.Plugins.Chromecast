using KHost.Abstractions.Interactions.Requests;
using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Services;

namespace KHost.Plugins.Chromecast;

/// <summary>Describes the receiver list as a table the host can draw.</summary>
/// <remarks>This is the half that used to live in the console's Screens dialog. A plugin cannot
/// ship markup, so it hands over columns, rows and what each button does, and the host draws it.
/// </remarks>
internal static class ChromecastDeviceTable
{
    internal const string NameKey = "name";
    internal const string ModelKey = "model";
    internal const string AddressKey = "address";
    internal const string StatusKey = "status";

    internal static IReadOnlyList<PluginTableColumn> Columns =>
    [
        new() { Key = NameKey, Header = "Receiver" },
        // Dropped when narrow: the name and whether it is connected are what a host is reading for.
        new() { Key = ModelKey, Header = "Model", Essential = false },
        new() { Key = AddressKey, Header = "Address", Kind = PluginTableColumnKind.Label, Essential = false },
        new() { Key = StatusKey, Header = "", Kind = PluginTableColumnKind.Label },
    ];

    internal static PluginTableRow RowFor(IDisplayProvider provider, DisplayDevice device) => new()
    {
        Id = device.Id,
        IsCurrent = device.IsConnected,
        Fields = new Dictionary<string, string>
        {
            [NameKey] = device.Name,
            [ModelKey] = device.Model ?? "",
            [AddressKey] = device.Address ?? "",
            [StatusKey] = device.IsConnected ? "Showing" : "",
        },
        Actions = device.IsConnected
            ? [new PluginTableAction
                {
                    DisplayName = "Stop",
                    Icon = "stop-fill",
                    Description = $"Stop showing on {device.Name}.",
                    PerformAsync = provider.DisconnectAsync,
                }]
            // Connecting replaces whatever was connected before, so a second row needs no warning.
            : [new PluginTableAction
                {
                    DisplayName = "Show here",
                    Icon = "display",
                    Description = $"Send the song to {device.Name}.",
                    PerformAsync = token => provider.ConnectAsync(device.Id, token),
                }],
    };

    internal static PluginTableAction SearchAction(IDisplayProvider provider) => new()
    {
        DisplayName = provider.IsDiscovering ? "Searching" : "Search",
        Icon = provider.IsDiscovering ? "broadcast" : "search",
        IsActive = provider.IsDiscovering,
        Description = provider.IsDiscovering
            ? "Searching the network. Click to stop."
            : "Browsing sweeps the whole network, so it stays off until you ask.",
        PerformAsync = provider.IsDiscovering
            ? provider.StopDiscoveryAsync
            : provider.StartDiscoveryAsync,
    };

    /// <summary>Read afresh on every refresh: the search button, the rows and the empty line all
    /// change together when discovery starts or stops.</summary>
    internal static PluginTableContent ContentFor(IDisplayProvider provider) => new()
    {
        Rows = [.. provider.Devices.Select(device => RowFor(provider, device))],
        Actions = [SearchAction(provider)],
        EmptyMessage = EmptyMessageFor(provider),
    };

    /// <summary>A completed empty sweep reads differently from one still in flight, or a background
    /// resweep that never tells the dialog anything new looks identical to a hang.</summary>
    private static string EmptyMessageFor(IDisplayProvider provider)
    {
        if (!provider.IsDiscovering) return "Not searching for devices.";

        return provider is ChromecastDisplayProvider { LastSweepFoundNothing: true }
            ? "No receivers found."
            : "Looking for devices…";
    }

    /// <summary>What the Plugins-page button says. Null keeps the manifest's own label.</summary>
    internal static string? ButtonLabelFor(IDisplayProvider provider)
        => provider.Devices.FirstOrDefault(device => device.IsConnected) is { } connected
            ? $"Showing on {connected.Name}"
            : null;

    internal static ShowPluginTableRequest RequestFor(IDisplayProvider provider) => new()
    {
        Title = $"{provider.Name} devices",
        Columns = Columns,
        LoadAsync = _ => Task.FromResult(ContentFor(provider)),
    };
}
