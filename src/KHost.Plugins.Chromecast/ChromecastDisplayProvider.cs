using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using KHost.Abstractions.Services;
using KHost.Abstractions.Models.Plugins;

// Aliased rather than importing the namespace: Sharpcaster has its own MediaStatus.
using MediaStreamSession = KHost.Abstractions.Models.MediaStreamSession;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using KHost.Common.Media;

namespace KHost.Plugins.Chromecast;

/// <summary>Drives one Chromecast receiver at a time.</summary>
/// <remarks>Bound by the host as an <see cref="IDisplayProvider"/>: the receiver is never a screen
/// and holds no role in the sync set, so playback drives it directly rather than broadcasting.
/// </remarks>
public sealed class ChromecastDisplayProvider : IDisplayProvider, IDisposable
{
    internal sealed class ServiceOptions
    {
        /// <summary>Google's Default Media Receiver, which plays a plain URL: no app of our own.</summary>
        public string ReceiverAppId { get; set; } = "CC1AD845";

        public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>How often a connected receiver is checked for a pulse.</summary>
        /// <remarks>Must catch a death well inside Sharpcaster's ten-second heartbeat.</remarks>
        public TimeSpan LivenessInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>How long a receiver has to answer before giving up.</summary>
        /// <remarks>mDNS can advertise an address nothing here can reach (a VPN interface does it).</remarks>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }

    private readonly ServiceOptions _options;
    private readonly ILogger<ChromecastDisplayProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, ChromecastReceiver> _discovered = [];
    private readonly IMessageBroker _broker;

    private ChromecastLocator? _locator;
    private ChromecastClient? _client;
    private string? _connectedDeviceId;

    // Bounded so a receiver refusing everything cannot become a relaunch loop.
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastRecoveryUtc = DateTime.MinValue;

    /// <summary>The port CASTV2 listens on; a receiver's DeviceUri carries no port of its own.</summary>
    private const int CastPort = 8009;

    private CancellationTokenSource? _liveness;
    private TimeSpan _streamStartOffset;
    private double _rate = 1.0;

    public event EventHandler<DisplayPlaybackStatus>? PlaybackStatusChanged;

    /// <summary>Names the transport wherever the console would otherwise hardcode "Cast".</summary>
    public string Name => "Chromecast";

    public ChromecastDisplayProvider(
        ILogger<ChromecastDisplayProvider> logger, IPluginContext context, IMessageBroker broker)
        : this(logger, OptionsFrom(context.BindSettings<ChromecastSettings>()), broker)
    {
    }

    /// <summary>The seam the tests build against: they supply timings without a plugin context.</summary>
    internal ChromecastDisplayProvider(
        ILogger<ChromecastDisplayProvider> logger, ServiceOptions options, IMessageBroker broker)
    {
        _logger = logger;
        _options = options;
        _broker = broker;
    }

    private static ServiceOptions OptionsFrom(ChromecastSettings settings) => new()
    {
        // Zero means "leave it alone": a host clearing a box must not get a timeout of nothing.
        DiscoveryTimeout = settings.DiscoverySeconds > 0
            ? TimeSpan.FromSeconds(settings.DiscoverySeconds)
            : new ServiceOptions().DiscoveryTimeout,
        ConnectTimeout = settings.ConnectTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds)
            : new ServiceOptions().ConnectTimeout,
    };

    public string? ConnectedDeviceId => _connectedDeviceId;

    public Guid? SessionId { get; private set; }

    public IReadOnlyList<DisplayDevice> Devices
    {
        get
        {
            _lock.Wait();
            try
            {
                return [.. _discovered.Select(entry => new DisplayDevice
                {
                    Id = entry.Key,
                    Name = entry.Value.Name ?? entry.Key,
                    Model = entry.Value.Model,
                    Address = entry.Value.DeviceUri?.Host,
                    IsConnected = entry.Key == _connectedDeviceId,
                })];
            }
            finally { _lock.Release(); }
        }
    }

    public bool IsDiscovering => _locator is not null;

    public async Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (_locator is not null) return;

        var locator = new ChromecastLocator();
        locator.ChromecastReceiverFound += OnReceiverFound;
        _locator = locator;

        _logger.LogInformation("Browsing for Cast receivers");

        // One sweep now so the page has something immediately, then keep listening.
        try
        {
            foreach (var receiver in await locator.FindReceiversAsync(_options.DiscoveryTimeout))
                Remember(receiver);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cast discovery sweep failed");
        }

        locator.StartContinuousDiscovery(TimeSpan.FromSeconds(30));
        RaiseStateChanged();
    }

    public Task StopDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        var locator = _locator;
        _locator = null;

        if (locator is not null)
        {
            locator.ChromecastReceiverFound -= OnReceiverFound;
            try { locator.StopContinuousDiscovery(); } catch { /* never started */ }
            _logger.LogInformation("Stopped browsing for Cast receivers");
        }

        _lock.Wait(cancellationToken);
        try
        {
            // The receiver being cast to stays listed, or there would be no way to stop it.
            foreach (var id in _discovered.Keys.Where(id => id != _connectedDeviceId).ToList())
                _discovered.Remove(id);
        }
        finally { _lock.Release(); }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public async Task<bool> ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_connectedDeviceId == deviceId) return true;

        // One song, one receiver. A second would be a room hearing it seconds out of step.
        if (_connectedDeviceId is not null) await DisconnectAsync(cancellationToken);

        ChromecastReceiver? receiver;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_discovered.TryGetValue(deviceId, out receiver)) return false;
        }
        finally { _lock.Release(); }

        var name = receiver!.Name ?? deviceId;
        _logger.LogInformation("Connecting to Cast device {Name} at {Address}", name, receiver.DeviceUri);

        var client = new ChromecastClient();

        try
        {
            var opening = OpenAsync(client, receiver);

            // Sharpcaster's connect takes no token, so the wait is bounded here instead; the attempt
            // runs to completion abandoned, not cancelled. The client below is torn down, never reused.
            if (await Task.WhenAny(opening, Task.Delay(_options.ConnectTimeout, cancellationToken)) != opening)
                throw new TimeoutException($"{name} did not answer within {_options.ConnectTimeout.TotalSeconds:0} seconds");

            await opening;
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not connect to Cast device {Name}: {Reason}", name, ex.Message);
            // Deliberately not the caller's token: this is the cleanup for a connection that
            // already failed, and cancelling it would leave the client undisposed.
            _ = Task.Run(async () =>
            {
                try { await client.DisconnectAsync(); } catch { /* already gone */ }
            }, CancellationToken.None);
            return false;
        }

        client.MediaChannel.StatusChanged += (_, status) => OnMediaStatus(status);
        client.Disconnected += (_, _) => OnDeviceDropped(deviceId, client);

        _client = client;
        _connectedDeviceId = deviceId;
        SessionId = Guid.NewGuid();
        _streamStartOffset = TimeSpan.Zero;
        _rate = 1.0;

        StartWatching(deviceId, receiver.DeviceUri!.Host);

        RaiseStateChanged();
        return true;
    }

    private async Task OpenAsync(ChromecastClient client, ChromecastReceiver receiver)
    {
        await client.ConnectChromecast(receiver);
        await client.LaunchApplicationAsync(_options.ReceiverAppId, false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        var name = _connectedDeviceId;

        StopWatching();

        _client = null;
        _connectedDeviceId = null;
        SessionId = null;

        if (client is null) return;

        _logger.LogInformation("Disconnecting from Cast device {Name}", name);

        try
        {
            await client.ReceiverChannel.StopApplication();
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Untidy disconnect from {Name}", name);
        }

        RaiseStateChanged();
    }

    public async Task LoadAsync(string streamUrl, TimeSpan startOffset, int tempo = 0, CancellationToken cancellationToken = default)
    {
        if (_client is not { } client) return;

        var reachable = MakeReachableFromDevice(streamUrl, LanAddress());
        _streamStartOffset = startOffset;
        _rate = StreamRate.FromTempo(tempo);

        _logger.LogInformation("Casting {Url} to {Name}", reachable, _connectedDeviceId);

        await GuardAsync(() => client.MediaChannel.LoadAsync(
            new Media { ContentUrl = reachable, StreamType = StreamType.Buffered }, autoPlay: false));
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
        => _client is { } c ? GuardAsync(c.MediaChannel.PlayAsync) : Task.CompletedTask;

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _client is { } c ? GuardAsync(c.MediaChannel.PauseAsync) : Task.CompletedTask;

    // No fade: Cast has no volume ramp, and faking one would move the TV's own level.
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _client is { } c ? GuardAsync(c.MediaChannel.StopAsync) : Task.CompletedTask;

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        => _client is { } c
            ? GuardAsync(() => c.MediaChannel.SeekAsync((position - _streamStartOffset).TotalSeconds / _rate))
            : Task.CompletedTask;

    /// <summary>A television switched off mid-song must not fail the performance.</summary>
    private async Task GuardAsync(Func<Task> action)
    {
        var client = _client;

        // Silences Sharpcaster's heartbeat across the write: pinging a gone receiver throws from its
        // async void on a timer thread, which nothing can catch and takes the host down with it.
        Hush(client);

        try
        {
            await action();
            Resume(client);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cast device {Name} refused a command: {Reason}", _connectedDeviceId, ex.Message);
        }

        await RecoverAsync();
    }

    private void Hush(ChromecastClient? client)
    {
        // Never fatal: a client torn down underneath us has no channel to quieten.
        try { client?.HeartbeatChannel.StopTimeoutTimer(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not stop the Cast heartbeat"); }
    }

    private void Resume(ChromecastClient? client)
    {
        try { client?.HeartbeatChannel.RestartTimeoutTimer(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not restart the Cast heartbeat"); }
    }

    private void StartWatching(string deviceId, string host)
    {
        StopWatching();

        var liveness = new CancellationTokenSource();
        _liveness = liveness;

        _ = Task.Run(() => WatchAsync(deviceId, host, liveness.Token));
    }

    private void StopWatching()
    {
        var liveness = _liveness;
        _liveness = null;

        liveness?.Cancel();
        liveness?.Dispose();
    }

    /// <summary>Watches for a receiver that died silently.</summary>
    /// <remarks>Sharpcaster's heartbeat throws via async void, uncatchable, so this probes instead.</remarks>
    private async Task WatchAsync(string deviceId, string host, CancellationToken cancellationToken)
    {
        var missed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(_options.LivenessInterval, cancellationToken); }
            catch (OperationCanceledException) { return; }

            if (await IsReachableAsync(host, cancellationToken))
            {
                missed = 0;
                continue;
            }

            // One refusal is a busy receiver, not a dead one. A Chromecast serving a room does
            // not always answer a second connection immediately.
            if (++missed < 2) continue;

            if (cancellationToken.IsCancellationRequested) return;

            _logger.LogWarning("Cast device {Name} stopped answering", deviceId);

            OnDeviceDropped(deviceId);
            await PickBackUpAsync(deviceId);
            return;
        }
    }

    private async Task<bool> IsReachableAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(_options.LivenessInterval);

            await probe.ConnectAsync(host, CastPort, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A refused command means the session is gone: a restart forgot what was launched.</summary>
    /// <remarks>Relaunch is the only way back; otherwise the connection is let go.</remarks>
    private async Task RecoverAsync()
    {
        if (_client is not { } client || _connectedDeviceId is not { } deviceId) return;

        // One attempt per interval: a receiver refusing everything would otherwise be relaunched
        // once per command for as long as the song lasts.
        if (DateTime.UtcNow - _lastRecoveryUtc < RecoveryInterval) return;

        _lastRecoveryUtc = DateTime.UtcNow;

        try
        {
            await client.LaunchApplicationAsync(_options.ReceiverAppId, false);

            // A new session, so whatever was playing has to be put back on it. Announcing the
            // change is how the caller learns it has a receiver that knows nothing.
            SessionId = Guid.NewGuid();

            _logger.LogInformation("Relaunched the receiver app on {Name}", deviceId);
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not relaunch on Cast device {Name}: {Reason}", deviceId, ex.Message);
        }

        // A broken pipe rather than a refused command: the receiver restarted, and the socket it
        // dropped cannot be launched on at all. It answers a fresh connection instead.
        OnDeviceDropped(deviceId);
        await PickBackUpAsync(deviceId);
    }

    /// <summary>One attempt at the same device on a new client.</summary>
    /// <remarks>A television left off just fails; the console never shows a cast reaching nothing.</remarks>
    private async Task PickBackUpAsync(string deviceId)
    {
        try
        {
            if (await ConnectAsync(deviceId))
                _logger.LogInformation("Picked Cast device {Name} back up", deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cast device {Name} did not come back: {Reason}", deviceId, ex.Message);
        }
    }

    /// <summary>Resolves the host's base address to localhost, which on a television means itself.</summary>
    /// <remarks>Anything already routable is left alone, so a configured address wins.</remarks>
    internal static string MakeReachableFromDevice(string url, string? lanAddress)
    {
        if (lanAddress is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.IsLoopback) return url;

        return new UriBuilder(uri) { Host = lanAddress }.Uri.ToString();
    }

    private static string? LanAddress()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            ?.ToString();

    private void OnMediaStatus(MediaStatus? status)
    {
        if (status is null) return;

        PlaybackStatusChanged?.Invoke(this, new DisplayPlaybackStatus
        {
            Position = _streamStartOffset + (TimeSpan.FromSeconds(status.CurrentTime) * _rate),
            IsPlaying = status.PlayerState == PlayerStateType.Playing,
            SampledAtUtc = DateTime.UtcNow,
        });
    }

    /// <param name="source">
    /// The client that said so, when the news came from one. A receiver picked back up is the same
    /// device on a new client, and the old one raises Disconnected as it is torn down, seconds
    /// after the replacement is already playing. Without this that farewell drops the live
    /// connection, and with nothing left to play on the song stops.
    /// </param>
    private void OnDeviceDropped(string deviceId, ChromecastClient? source = null)
    {
        if (_connectedDeviceId != deviceId) return;
        if (source is not null && !ReferenceEquals(source, _client)) return;

        _logger.LogWarning("Cast device {Name} dropped its connection", deviceId);

        var client = _client;

        StopWatching();
        Hush(client);

        _client = null;
        _connectedDeviceId = null;
        SessionId = null;

        // Letting go of the reference isn't enough: Sharpcaster's heartbeat ping is an async void, so
        // a write to a gone socket throws where nothing catches it. Disconnecting also raises Disconnected.
        if (client is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await client.DisconnectAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Untidy teardown of a dropped Cast client"); }
            });
        }

        RaiseStateChanged();
    }

    private void OnReceiverFound(object? sender, ChromecastReceiverEventArgs e)
    {
        if (Remember(e.Receiver)) RaiseStateChanged();
    }

    internal bool Remember(ChromecastReceiver receiver)
    {
        // Discovery has no stable device id, and the friendly name is what the user recognises.
        if (receiver.Name is not { Length: > 0 } id) return false;

        _lock.Wait();
        try
        {
            if (!_discovered.TryAdd(id, receiver)) return false;
        }
        finally { _lock.Release(); }

        _logger.LogInformation("Found Cast device {Name} at {Address}", receiver.Name, receiver.DeviceUri);
        return true;
    }

    private void RaiseStateChanged()
    {
        if (_broker is { } broker)
            _ = broker.PublishAsync(new DisplaysChanged());
    }

    public void Dispose()
    {
        StopWatching();

        if (_locator is not null)
        {
            _locator.ChromecastReceiverFound -= OnReceiverFound;
            try { _locator.StopContinuousDiscovery(); } catch { /* never started */ }
            _locator.Dispose();
        }

        try { _client?.DisconnectAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        _client = null;
    }
}
