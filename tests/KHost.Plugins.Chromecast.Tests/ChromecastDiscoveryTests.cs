using KHost.Plugins.Chromecast;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>Discovery state, without touching the network.</summary>
public class ChromecastDiscoveryTests : IDisposable
{
    private readonly IMessageBroker _broker = Substitute.For<IMessageBroker>();

    private readonly ChromecastDisplayProvider _display;

    public ChromecastDiscoveryTests()
    {
        _display = new ChromecastDisplayProvider(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions(),
            _broker);
    }

    [Fact]
    public void IsDiscovering_IsFalse_UntilAsked()
    {
        // Browsing sweeps the whole network, so nothing starts it on the app's behalf.
        Assert.False(_display.IsDiscovering);
        Assert.Empty(_display.Devices);
    }

    [Fact]
    public async Task StopDiscovery_IsANoOp_WhenItWasNeverStarted()
    {
        await _display.StopDiscoveryAsync();

        Assert.False(_display.IsDiscovering);
    }

    [Fact]
    public async Task StopDiscovery_AnnouncesDisplaysChanged_SoThePageRedraws()
    {
        await _display.StopDiscoveryAsync();

        await _broker.Received(1).PublishAsync(Arg.Any<DisplaysChanged>());
    }

    [Fact]
    public async Task StopDiscovery_AlsoAnnouncesPluginTableChanged_SoAnOpenDeviceTableRereads()
    {
        await _display.StopDiscoveryAsync();

        // The dialog is generic and never hears DisplaysChanged; without this it sits stale.
        await _broker.Received(1).PublishAsync(Arg.Any<PluginTableChanged>());
    }

    public void Dispose()
    {
        _display.Dispose();
        GC.SuppressFinalize(this);
    }
}
