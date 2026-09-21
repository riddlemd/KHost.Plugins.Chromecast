using KHost.Abstractions.Interactions;
using KHost.Abstractions.Interactions.Requests;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Services;
using KHost.Abstractions.Models.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>The Plugins-page button is how the device list is reached now.</summary>
public class ChromecastButtonTests : IDisposable
{
    private readonly IInteractionDispatcher _dispatcher = Substitute.For<IInteractionDispatcher>();

    private readonly ChromecastDisplayProvider _display;

    public ChromecastButtonTests()
        => _display = new ChromecastDisplayProvider(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions(),
            Substitute.For<IMessageBroker>(),
            _dispatcher);

    [Fact]
    public async Task InvokeButton_AsksTheHostToDrawTheDeviceTable()
    {
        await _display.InvokeButtonAsync(ChromecastDisplayProvider.DevicesButtonKey);

        await _dispatcher.Received(1).RequestAsync(
            Arg.Is<ShowPluginTableRequest>(r => r.Title == "Chromecast devices"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeButton_IsANoOp_ForAKeyThisPluginDoesNotDeclare()
    {
        await _display.InvokeButtonAsync("something-else");

        await _dispatcher.DidNotReceive().RequestAsync(
            Arg.Any<ShowPluginTableRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DescribeButton_KeepsTheManifestLabel_ForAKeyThisPluginDoesNotDeclare()
        => Assert.Equal(PluginButtonState.Default, _display.DescribeButton("something-else"));

    [Fact]
    public void ButtonLabel_IsNull_WhenNothingIsConnected()
    {
        var provider = Substitute.For<IDisplayProvider>();
        provider.Devices.Returns([Device("d1", "Living Room", connected: false)]);

        // A null label is what leaves the manifest's own wording in place.
        Assert.Null(ChromecastDeviceTable.ButtonLabelFor(provider));
    }

    [Fact]
    public void ButtonLabel_NamesTheDevice_SoTheRowSaysWhatTheRoomIsWatching()
    {
        var provider = Substitute.For<IDisplayProvider>();
        provider.Devices.Returns([Device("d1", "Kitchen"), Device("d2", "Living Room", connected: true)]);

        Assert.Equal("Showing on Living Room", ChromecastDeviceTable.ButtonLabelFor(provider));
    }

    private static DisplayDevice Device(string id, string name, bool connected = false)
        => new() { Id = id, Name = name, IsConnected = connected };

    public void Dispose()
    {
        _display.Dispose();
        GC.SuppressFinalize(this);
    }
}
