using System.Net.Sockets;
using KHost.Plugins.Chromecast;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>Drives a real CASTV2 receiver; skips unless the emulator listens on 127.0.0.1:8009.</summary>
public class ChromecastReceiverIntegrationTests : IAsyncLifetime
{
    // The emulator can be started under any --name; pinning one turns that into failures.
    private string DeviceName => _display.Devices.FirstOrDefault()?.Name
        ?? throw new InvalidOperationException(
            "Port 8009 is listening but no Cast device was discovered — is mDNS blocked?");

    private readonly ChromecastDisplayProvider _display = new(
        NullLogger<ChromecastDisplayProvider>.Instance,
        new ChromecastDisplayProvider.ServiceOptions
        {
            DiscoveryTimeout = TimeSpan.FromSeconds(5),
        },
        Substitute.For<IMessageBroker>());

    public async Task InitializeAsync() => await _display.StartDiscoveryAsync();

    public Task DisposeAsync()
    {
        _display.Dispose();
        return Task.CompletedTask;
    }

    [RequiresCastEmulatorFact]
    public void StartDiscovery_DiscoversTheReceiver() => Assert.NotEmpty(_display.Devices);

    [RequiresCastEmulatorFact]
    public async Task Connect_MarksTheDeviceConnected_WithoutMakingItAScreen()
    {
        Assert.True(await _display.ConnectAsync(DeviceName));

        Assert.Equal(DeviceName, _display.ConnectedDeviceId);
        Assert.Contains(_display.Devices, d => d.Name == DeviceName && d.IsConnected);
    }

    [RequiresCastEmulatorFact]
    public async Task Connect_IsIdempotent()
    {
        Assert.True(await _display.ConnectAsync(DeviceName));
        Assert.True(await _display.ConnectAsync(DeviceName));

        Assert.Single(_display.Devices, d => d.IsConnected);
    }

    [RequiresCastEmulatorFact]
    public async Task Connect_OpensASession()
    {
        await _display.ConnectAsync(DeviceName);

        Assert.NotNull(_display.SessionId);
    }

    [RequiresCastEmulatorFact]
    public async Task Connect_IsIdempotent_DownToTheSession()
    {
        await _display.ConnectAsync(DeviceName);
        var session = _display.SessionId;

        await _display.ConnectAsync(DeviceName);

        // The second call launches nothing, so reporting a new session would have the caller
        // reload a receiver that is already playing the song.
        Assert.Equal(session, _display.SessionId);
    }

    [RequiresCastEmulatorFact]
    public async Task Reconnecting_OpensADifferentSession()
    {
        await _display.ConnectAsync(DeviceName);
        var first = _display.SessionId;

        await _display.DisconnectAsync();
        await _display.ConnectAsync(DeviceName);

        // The app was launched again, so the receiver knows nothing, which is the whole reason
        // the id exists rather than a connected flag.
        Assert.NotNull(_display.SessionId);
        Assert.NotEqual(first, _display.SessionId);
    }

    [RequiresCastEmulatorFact]
    public async Task Connect_IsRefused_ForAnUnknownDevice()
        => Assert.False(await _display.ConnectAsync("No Such TV"));

    [RequiresCastEmulatorFact]
    public async Task OnlyOneDeviceIsEverConnected()
    {
        await _display.ConnectAsync(DeviceName);

        // Connecting elsewhere replaces rather than adds.
        Assert.Single(_display.Devices, d => d.IsConnected);
    }

    [RequiresCastEmulatorFact]
    public async Task Transport_ReachesTheReceiver()
    {
        await _display.ConnectAsync(DeviceName);

        await _display.LoadAsync(new DisplayLoad { StreamUrl = "http://192.168.1.10:5251/media/abc/stream.m3u8" });
        await _display.PlayAsync();
        await _display.SeekAsync(TimeSpan.FromSeconds(10));
        await _display.PauseAsync();
        await _display.StopAsync();

        Assert.Equal(DeviceName, _display.ConnectedDeviceId);
    }

    [RequiresCastEmulatorFact]
    public async Task Transport_IsSilentlyIgnored_WhenNothingIsConnected()
    {
        // A song plays whether or not anyone is casting.
        await _display.LoadAsync(new DisplayLoad { StreamUrl = "http://192.168.1.10:5251/media/abc/stream.m3u8" });
        await _display.PlayAsync();
        await _display.StopAsync();

        Assert.Null(_display.ConnectedDeviceId);
    }

    [RequiresCastEmulatorFact]
    public async Task Disconnect_LetsGoOfTheDevice()
    {
        await _display.ConnectAsync(DeviceName);

        await _display.DisconnectAsync();

        Assert.Null(_display.ConnectedDeviceId);
        Assert.Null(_display.SessionId);
        Assert.DoesNotContain(_display.Devices, d => d.IsConnected);
    }
}

/// <summary>xUnit 2 cannot skip at runtime, so the decision is made in the constructor.</summary>
public sealed class RequiresCastEmulatorFactAttribute : FactAttribute
{
    public RequiresCastEmulatorFactAttribute()
    {
        if (!EmulatorIsListening.Value)
            Skip = "the Chromecast emulator is not listening on 127.0.0.1:8009";
    }

    private static readonly Lazy<bool> EmulatorIsListening = new(() =>
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 8009).Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            return false;
        }
    });
}
