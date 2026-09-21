using KHost.Abstractions.Messaging;
using System.Net;
using System.Net.Sockets;
using KHost.Plugins.Chromecast;
using Sharpcaster.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>No receiver attached; unlike <see cref="CastServiceTests"/> these need no emulator.</summary>
public class ChromecastDisconnectedTests : IDisposable
{
    private readonly ChromecastDisplayProvider _display = new(
        NullLogger<ChromecastDisplayProvider>.Instance,
        new ChromecastDisplayProvider.ServiceOptions(),
        Substitute.For<IMessageBroker>());

    [Fact]
    public void AFreshServiceIsIdleAndUnconnected()
    {
        Assert.Empty(_display.Devices);
        Assert.Null(_display.ConnectedDeviceId);
        Assert.False(_display.IsDiscovering);
    }

    [Fact]
    public async Task ConnectAsync_IsRefused_WhenNothingHasBeenDiscovered()
    {
        Assert.False(await _display.ConnectAsync("No Such TV"));
        Assert.Null(_display.ConnectedDeviceId);
    }

    [Fact]
    public async Task Transport_IsSilentlyIgnored_WhenNothingIsConnected()
    {
        // A song plays whether or not anyone is casting, so none of these may throw.
        await _display.LoadAsync("http://192.168.1.10:5251/media/abc/stream.m3u8", TimeSpan.Zero);
        await _display.PlayAsync();
        await _display.SeekAsync(TimeSpan.FromSeconds(10));
        await _display.PauseAsync();
        await _display.StopAsync();

        Assert.Null(_display.ConnectedDeviceId);
    }

    [Fact]
    public void ThereIsNoSession_WhileNothingIsConnected()
        // Null is what tells a caller there is no receiver holding the song; a stale id would
        // have it believe the television already has it.
        => Assert.Null(_display.SessionId);

    [Fact]
    public async Task DisconnectAsync_IsHarmless_WhenNothingIsConnected()
    {
        await _display.DisconnectAsync();

        Assert.Null(_display.ConnectedDeviceId);
    }

    [Fact]
    public async Task ConnectAsync_GivesUp_WhenTheReceiverNeverAnswers()
    {
        // A receiver that accepts but never answers. Sharpcaster's connect takes no token, so
        // without a bound of our own the Screens dialog says "connecting" for the whole night.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();

        var accepting = silent.AcceptTcpClientAsync();
        var endpoint = (IPEndPoint)silent.LocalEndpoint;

        using var cast = new ChromecastDisplayProvider(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions { ConnectTimeout = TimeSpan.FromMilliseconds(300) },
            Substitute.For<IMessageBroker>());

        cast.Remember(new ChromecastReceiver
        {
            Name = "Silent TV",
            DeviceUri = new Uri($"https://{endpoint.Address}/"),
            Port = endpoint.Port,
        });

        var connecting = cast.ConnectAsync("Silent TV");

        Assert.Same(connecting, await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.False(await connecting);
        Assert.Null(cast.ConnectedDeviceId);

        if (accepting.IsCompletedSuccessfully) (await accepting).Dispose();
    }

    [Fact]
    public async Task StopDiscoveryAsync_IsHarmless_WhenDiscoveryNeverStarted()
    {
        await _display.StopDiscoveryAsync();

        Assert.False(_display.IsDiscovering);
        Assert.Empty(_display.Devices);
    }

    [Fact]
    public async Task NoPlaybackStatusIsReported_WhileNothingIsConnected()
    {
        var reported = 0;
        _display.PlaybackStatusChanged += (_, _) => reported++;

        await _display.LoadAsync("http://192.168.1.10:5251/media/abc/stream.m3u8", TimeSpan.Zero);
        await _display.PlayAsync();

        // The fallback clock must not tick from a receiver that was never there.
        Assert.Equal(0, reported);
    }

    public void Dispose() => _display.Dispose();
}
