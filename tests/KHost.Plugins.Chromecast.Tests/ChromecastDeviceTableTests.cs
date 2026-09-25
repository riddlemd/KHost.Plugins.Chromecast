using KHost.Abstractions.Interactions;
using KHost.Abstractions.Interactions.Requests;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>The device list the console used to own, now described by the plugin.</summary>
public class ChromecastDeviceTableTests
{
    private readonly IDisplayProvider _provider = Substitute.For<IDisplayProvider>();

    public ChromecastDeviceTableTests() => _provider.Name.Returns("Chromecast");

    private static DisplayDevice Device(string id, string name, bool connected = false, string? model = null)
        => new() { Id = id, Name = name, IsConnected = connected, Model = model, Address = "192.168.1.5" };

    [Fact]
    public void RowFor_ADisconnectedDevice_OffersToShowOnIt()
    {
        var row = ChromecastDeviceTable.RowFor(_provider, Device("d1", "Living Room"));

        Assert.False(row.IsCurrent);
        Assert.Equal("Living Room", row.Fields[ChromecastDeviceTable.NameKey]);
        Assert.Equal("Show here", Assert.Single(row.Actions).DisplayName);
    }

    [Fact]
    public void RowFor_TheConnectedDevice_OffersToStopInstead()
    {
        var row = ChromecastDeviceTable.RowFor(_provider, Device("d1", "Living Room", connected: true));

        // The row in effect is marked so the host can see it without reading the status column.
        Assert.True(row.IsCurrent);
        Assert.Equal("Showing", row.Fields[ChromecastDeviceTable.StatusKey]);
        Assert.Equal("Stop", Assert.Single(row.Actions).DisplayName);
    }

    [Fact]
    public async Task RowFor_ShowHere_ConnectsToThatDeviceAndNoOther()
    {
        var row = ChromecastDeviceTable.RowFor(_provider, Device("d2", "Kitchen"));

        await Assert.Single(row.Actions).PerformAsync(CancellationToken.None);

        await _provider.Received(1).ConnectAsync("d2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RowFor_Stop_DisconnectsRatherThanConnecting()
    {
        var row = ChromecastDeviceTable.RowFor(_provider, Device("d2", "Kitchen", connected: true));

        await Assert.Single(row.Actions).PerformAsync(CancellationToken.None);

        await _provider.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().ConnectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAction_StartsBrowsing_WhenItIsOff()
    {
        _provider.IsDiscovering.Returns(false);

        var action = ChromecastDeviceTable.SearchAction(_provider);

        Assert.Equal("Search", action.DisplayName);
        Assert.False(action.IsActive);

        await action.PerformAsync(CancellationToken.None);
        await _provider.Received(1).StartDiscoveryAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAction_StopsBrowsing_WhenItIsOn()
    {
        _provider.IsDiscovering.Returns(true);

        var action = ChromecastDeviceTable.SearchAction(_provider);

        // Drawn pressed, or a host cannot tell a running sweep from an idle one.
        Assert.Equal("Searching", action.DisplayName);
        Assert.True(action.IsActive);

        await action.PerformAsync(CancellationToken.None);
        await _provider.Received(1).StopDiscoveryAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContentFor_IsReadAfresh_SoTheSearchButtonFollowsTheSweep()
    {
        _provider.Devices.Returns([]);
        _provider.IsDiscovering.Returns(false);

        var request = ChromecastDeviceTable.RequestFor(_provider);
        var before = await request.LoadAsync(CancellationToken.None);

        _provider.IsDiscovering.Returns(true);
        var after = await request.LoadAsync(CancellationToken.None);

        Assert.Equal("Search", Assert.Single(before.Actions).DisplayName);
        Assert.Equal("Searching", Assert.Single(after.Actions).DisplayName);
        Assert.Equal("Looking for devices…", after.EmptyMessage);
    }

    [Fact]
    public async Task ContentFor_ReturnsARowPerDevice()
    {
        _provider.Devices.Returns([Device("d1", "Living Room"), Device("d2", "Kitchen", connected: true)]);

        var content = await ChromecastDeviceTable.RequestFor(_provider).LoadAsync(CancellationToken.None);

        Assert.Equal(["Living Room", "Kitchen"], content.Rows.Select(r => r.Fields[ChromecastDeviceTable.NameKey]));
    }

    [Fact]
    public async Task ContentFor_ASweepThatFoundNothing_SaysSoInsteadOfStillLooking()
    {
        using var display = new ChromecastDisplayProvider(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions(),
            Substitute.For<IMessageBroker>());

        display.SimulateDiscoveryArmed();
        display.ReportSweep(0);

        var content = await ChromecastDeviceTable.RequestFor(display).LoadAsync(CancellationToken.None);

        Assert.Equal("No receivers found.", content.EmptyMessage);
    }

    [Fact]
    public async Task ContentFor_ArmedButNoSweepHasFinishedYet_StillReadsLooking()
    {
        using var display = new ChromecastDisplayProvider(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions(),
            Substitute.For<IMessageBroker>());

        display.SimulateDiscoveryArmed();

        var content = await ChromecastDeviceTable.RequestFor(display).LoadAsync(CancellationToken.None);

        Assert.Equal("Looking for devices…", content.EmptyMessage);
    }

    [Fact]
    public void Columns_KeepTheNameWhenNarrow_AndDropTheRest()
    {
        var byKey = ChromecastDeviceTable.Columns.ToDictionary(c => c.Key);

        Assert.True(byKey[ChromecastDeviceTable.NameKey].Essential);
        Assert.False(byKey[ChromecastDeviceTable.ModelKey].Essential);
        Assert.False(byKey[ChromecastDeviceTable.AddressKey].Essential);
    }
}
