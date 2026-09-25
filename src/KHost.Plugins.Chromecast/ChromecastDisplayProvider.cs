using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using KHost.Abstractions.Services;
using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Interactions;

// Aliased rather than importing the namespace: Sharpcaster has its own MediaStatus.
using MediaStreamSession = KHost.Abstractions.Models.MediaStreamSession;
using KHost.Abstractions.Messaging;
using KHost.Common.Discovery;
using KHost.Abstractions.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using KHost.Common.Media;
using PlaybackProgram = KHost.Abstractions.Models.PlaybackProgram;
using RenderTarget = KHost.Abstractions.Models.RenderTarget;
using DisplayLoad = KHost.Abstractions.Models.DisplayLoad;

namespace KHost.Plugins.Chromecast;

/// <summary>Drives one Chromecast receiver at a time.</summary>
/// <remarks>Bound by the host as an <see cref="IDisplayProvider"/>: the receiver is never a screen
/// and holds no role in the sync set, so playback drives it directly rather than broadcasting.
///
/// <para><b>It deliberately draws no overlays</b> — no marquee, QR code, next-singer or break-music
/// card, and no timed words. Google's Default Media Receiver plays one media item and layers
/// nothing over it; drawing any of that needs a custom receiver app of our own. The words reach the
/// picture instead by asking for <see cref="RenderTarget.BurnLyrics"/>.</para>
///
/// <para><b>It does show pictures</b>, as image media on that same default receiver: an ad's still,
/// and the venue's card while nothing plays. Either replaces whatever the receiver holds, so a
/// picture is never loaded while a song is on. Going idle with no card takes down a picture this
/// plugin put up, and nothing else.</para></remarks>
public sealed class ChromecastDisplayProvider : IDisplayProvider, IPluginButtonHandler, IDisposable
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
    /// <summary>The Bonjour registration type; the wire name carries no trailing domain.</summary>
    private const string BonjourServiceType = "_googlecast._tcp";

    private readonly Dictionary<string, ChromecastReceiver> _discovered = [];

    /// <summary>Non-null while the macOS sweep loop is running; the locator's counterpart.</summary>
    private CancellationTokenSource? _bonjour;

    /// <summary>Set once a completed sweep found nothing, so the table can say so instead of
    /// reading "still looking" for a background poll that already answered.</summary>
    private volatile bool _lastSweepFoundNothing;
    private readonly IMessageBroker _broker;
    private readonly IInteractionDispatcher? _dispatcher;

    // IPlaybackService depends on every display provider, so it is resolved on use, never injected.
    private readonly IServiceProvider? _services;
    private readonly Func<Media, bool, Task> _castMedia;
    private readonly Func<string?> _lanAddress;
    private readonly Func<Task> _stopMedia;
    private readonly SubscriptionSet _subscriptions = new();
    private readonly SemaphoreSlim _pictureLock = new(1, 1);

    /// <summary>The program last pictured; PlaybackChanged also fires for a pause, seek or key change.</summary>
    private PlaybackProgram? _pictured;

    /// <summary>The reachable URL of the picture on the receiver, or null once a song replaced it.</summary>
    private string? _shownImageUrl;

    private Task _drawing = Task.CompletedTask;

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
        ILogger<ChromecastDisplayProvider> logger,
        IPluginContext context,
        IMessageBroker broker,
        IInteractionDispatcher dispatcher,
        IServiceProvider services)
        : this(logger, OptionsFrom(context.BindSettings<ChromecastSettings>()), broker, dispatcher, services)
    {
    }

    /// <summary>The seam the tests build against: they supply timings without a plugin context.</summary>
    internal ChromecastDisplayProvider(
        ILogger<ChromecastDisplayProvider> logger,
        ServiceOptions options,
        IMessageBroker broker,
        IInteractionDispatcher? dispatcher = null,
        IServiceProvider? services = null,
        Func<Media, bool, Task>? castMedia = null,
        Func<string?>? lanAddress = null,
        Func<Task>? stopMedia = null)
    {
        _logger = logger;
        _options = options;
        _broker = broker;
        _dispatcher = dispatcher;
        _services = services;
        _castMedia = castMedia ?? CastOnClientAsync;
        _lanAddress = lanAddress ?? LanAddress;
        _stopMedia = stopMedia ?? StopOnClientAsync;

        _subscriptions.Add(broker.Subscribe<PlaybackChanged>(_ => Redraw(venueMoved: false)));

        // The venue's card is the idle picture, and a venue edit can change or remove it.
        _subscriptions.Add(broker.Subscribe<SelectedVenueChanged>(_ => Redraw(venueMoved: true)));
    }

    /// <summary>The key the manifest declares; an unknown one is a no-op by contract.</summary>
    internal const string DevicesButtonKey = "devices";

    public Task InvokeButtonAsync(string key, CancellationToken cancellationToken = default)
        => key == DevicesButtonKey && _dispatcher is { } dispatcher
            ? dispatcher.RequestAsync(ChromecastDeviceTable.RequestFor(this), cancellationToken)
            : Task.CompletedTask;

    /// <summary>Says what the room is watching without the host opening anything.</summary>
    public PluginButtonState DescribeButton(string key)
        => key == DevicesButtonKey
            ? new PluginButtonState { Label = ChromecastDeviceTable.ButtonLabelFor(this) }
            : PluginButtonState.Default;

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

    /// <summary>A receiver plays one stream it cannot mix and draws no words over it.</summary>
    public RenderTarget DescribeTarget() => new() { MixesStems = false, BurnLyrics = true };

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

                    // A receiver plays the stream and draws nothing over it. The drawable flags
                    // stay false on purpose: the host then never sends what would not land, and
                    // the console can say so before a host picks this one.
                    SupportsAudio = true,
                    SupportsVideo = true,

                    // No mixer of ours to ride down — StopAsync cuts. Left false so the host
                    // stops instantly rather than waiting out a fade that never happens.
                    SupportsFade = false,
                })];
            }
            finally { _lock.Release(); }
        }
    }

    public bool IsDiscovering => _locator is not null || _bonjour is not null;

    /// <summary>Whether the most recently completed sweep came back empty; distinguishes "still
    /// looking" from "already looked and there is nothing" for as long as discovery stays armed.</summary>
    internal bool LastSweepFoundNothing => _lastSweepFoundNothing;

    /// <summary>Arms <see cref="IsDiscovering"/> without reaching the real daemon; the tests' stand-in
    /// for a sweep already under way.</summary>
    internal void SimulateDiscoveryArmed() => _bonjour ??= new CancellationTokenSource();

    public async Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (IsDiscovering) return;

        _logger.LogInformation("Browsing for Cast receivers");

        if (BonjourBrowser.IsSupported)
        {
            await StartBonjourDiscoveryAsync();
            return;
        }

        var locator = new ChromecastLocator();
        locator.ChromecastReceiverFound += OnReceiverFound;
        _locator = locator;

        // One sweep now so the page has something immediately, then keep listening.
        try
        {
            var found = (await locator.FindReceiversAsync(_options.DiscoveryTimeout)).ToList();
            foreach (var receiver in found)
                Remember(receiver);

            ReportSweep(found.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cast discovery sweep failed");
        }

        locator.StartContinuousDiscovery(TimeSpan.FromSeconds(30));
        RaiseStateChanged();
    }

    /// <summary>Says what a sweep actually found, because silence reads the same as every failure.</summary>
    /// <remarks>A blocked sweep and an empty room looked identical from the log, which is most of
    /// why a machine that could not multicast at all took so long to tell anyone.</remarks>
    /// <param name="background">A resweep behind an already-open dialog, not the ask that started
    /// it — an empty result there repeats every 30s all night, so it logs quieter than the first.</param>
    internal void ReportSweep(int count, bool background = false)
    {
        _lastSweepFoundNothing = count == 0;

        if (count > 0) _logger.LogInformation("Cast sweep found {Count} receiver(s)", count);
        else _logger.Log(background ? LogLevel.Debug : LogLevel.Information, "Cast sweep found no receivers");
    }

    // --- macOS: the system daemon, because a managed socket cannot multicast here ---

    /// <summary>Sweeps now, then keeps sweeping, the way continuous discovery does elsewhere.</summary>
    private async Task StartBonjourDiscoveryAsync()
    {
        var cancellation = new CancellationTokenSource();
        _bonjour = cancellation;

        await BonjourSweepAsync(cancellation.Token);

        _ = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token);
                    await BonjourSweepAsync(cancellation.Token, background: true);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogWarning(ex, "Cast discovery sweep failed"); }
            }
        }, CancellationToken.None);

        RaiseStateChanged();
    }

    private async Task BonjourSweepAsync(CancellationToken cancellationToken, bool background = false)
    {
        try
        {
            var services = await BonjourBrowser.BrowseAsync(
                BonjourServiceType, _options.DiscoveryTimeout, cancellationToken);

            ApplySweepResult(services.Count, services.Where(s => s.Address is not null).Select(ReceiverFor), background);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cast discovery sweep failed");
        }
    }

    /// <summary>The bookkeeping half of a completed sweep, kept apart from the network call so it
    /// can be driven directly by a test rather than the real daemon.</summary>
    /// <remarks>Announces every time, added or not: an open device table only re-reads on this, so
    /// an empty resweep that skipped it would sit forever on the previous wording.</remarks>
    internal void ApplySweepResult(int found, IEnumerable<ChromecastReceiver> receivers, bool background = false)
    {
        foreach (var receiver in receivers)
            Remember(receiver);

        ReportSweep(found, background);
        RaiseStateChanged();
    }

    /// <summary>Bonjour answers with a host and port; Sharpcaster wants its own receiver shape.</summary>
    private static ChromecastReceiver ReceiverFor(BonjourBrowser.Service service) => new()
    {
        Name = service.Name,
        DeviceUri = new Uri($"https://{service.Address}"),
        Port = service.Port,
    };

    public Task StopDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        var bonjour = _bonjour;
        _bonjour = null;
        _lastSweepFoundNothing = false;

        if (bonjour is not null)
        {
            bonjour.Cancel();
            bonjour.Dispose();
            _logger.LogInformation("Stopped browsing for Cast receivers");
        }

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

        StartWatching(deviceId, receiver.DeviceUri!.Host);
        Adopt(deviceId, client);
        return true;
    }

    /// <summary>Makes <paramref name="deviceId"/> the connected receiver, on a new session.</summary>
    /// <remarks>Internal as the tests' stand-in for a receiver answering a connect.</remarks>
    internal void Adopt(string deviceId, ChromecastClient? client)
    {
        _client = client;
        _connectedDeviceId = deviceId;
        SessionId = Guid.NewGuid();
        _streamStartOffset = TimeSpan.Zero;
        _rate = 1.0;

        RaiseStateChanged();

        // A fresh session holds nothing, so whatever the program pictures goes up again.
        Redraw(venueMoved: false, sessionIsNew: true);
    }

    // --- pictures ---

    /// <summary>Completes once every picture asked for so far has been drawn or declined.</summary>
    internal Task WhenPictureDrawnAsync() => _drawing;

    /// <summary>Detached, because the broker runs handlers one at a time and a receiver can be slow.</summary>
    private void Redraw(bool venueMoved, bool sessionIsNew = false)
        => _drawing = Task.Run(() => DrawPictureAsync(venueMoved, sessionIsNew));

    private async Task DrawPictureAsync(bool venueMoved, bool sessionIsNew)
    {
        if (_connectedDeviceId is null || Resolve<IPlaybackService>() is not { } playback) return;

        await _pictureLock.WaitAsync();
        try
        {
            if (sessionIsNew)
            {
                _pictured = null;
                _shownImageUrl = null;
            }

            var program = playback.CurrentProgram;

            // A venue edit re-reads the card although the program has not moved.
            if (!venueMoved && Equals(program, _pictured)) return;

            _pictured = program;

            (string Url, string? ContentType)? picture = program switch
            {
                PlaybackProgram.AdStill still => (still.ImageUrl, null),
                PlaybackProgram.Idle => await ReadIdleCardAsync(),
                _ => null,
            };

            // The song's own load replaced whatever was up, so the next picture must go up again.
            if (program is PlaybackProgram.Playing)
            {
                _shownImageUrl = null;
                return;
            }

            // Idle with no card takes down only a picture this plugin put up, so a finished ad does
            // not linger; anything else on the receiver (a song, another sender's media) is left.
            if (picture is not { } shown)
            {
                if (_shownImageUrl is null) return;
                if (_connectedDeviceId is null || !Equals(playback.CurrentProgram, program)) return;

                _shownImageUrl = null;
                _logger.LogInformation("Taking the picture down on {Name}", _connectedDeviceId);
                await GuardAsync(_stopMedia);
                return;
            }

            var reachable = MakeReachableFromDevice(shown.Url, _lanAddress());
            if (reachable == _shownImageUrl) return;

            // The card lookup awaited; a song that started meanwhile must not be replaced by it.
            if (_connectedDeviceId is null || !Equals(playback.CurrentProgram, program)) return;

            _shownImageUrl = reachable;
            _logger.LogInformation("Showing {Url} on {Name}", reachable, _connectedDeviceId);

            await GuardAsync(() => _castMedia(new Media
            {
                ContentUrl = reachable,
                ContentType = shown.ContentType ?? ImageContentTypeFor(shown.Url),
                StreamType = StreamType.None,
            }, true));
        }
        catch (Exception ex)
        {
            // Decoration: a picture that fails must not stop the queue reaching the next singer.
            _logger.LogWarning(ex, "Could not show the picture on {Name}", _connectedDeviceId);
        }
        finally
        {
            _pictureLock.Release();
        }
    }

    /// <summary>The selected venue's card, when it names a library still.</summary>
    private async Task<(string Url, string? ContentType)?> ReadIdleCardAsync()
    {
        if (Resolve<IVenuesService>() is not { } venues
            || Resolve<IMediaService>() is not { } library
            || Resolve<IMediaStreamService>() is not { } streams)
            return null;

        if ((await venues.ReadSelectedVenueAsync())?.Settings.BrandingImageMediaId is not { } cardId)
            return null;

        // A card pointing at a song would hand the receiver an image URL serving nothing.
        if (await library.ReadAsync(cardId) is not { } media
            || MediaFormats.ContentTypeFor(media.Format) is not { } contentType)
            return null;

        return (streams.BuildImageUrl(media.Id), contentType);
    }

    /// <summary>The default receiver decides image versus video on the content type's prefix.</summary>
    /// <remarks>The host's image route carries no extension, so an unknown one falls back to a
    /// generic still; the receiver sniffs the actual bytes.</remarks>
    internal static string ImageContentTypeFor(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return MediaFormats.ContentTypeFor(Path.GetExtension(path)) ?? "image/jpeg";
    }

    private async Task CastOnClientAsync(Media media, bool autoPlay)
    {
        if (_client is { } client) await client.MediaChannel.LoadAsync(media, autoPlay);
    }

    private async Task StopOnClientAsync()
    {
        if (_client is { } client) await client.MediaChannel.StopAsync();
    }

    private T? Resolve<T>() where T : class => _services?.GetService(typeof(T)) as T;

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

    // DescribeTarget asks for no stems, so StreamUrl should always be set. A null one means the host
    // has nothing but Stems, which a receiver has no mixer to play — skip the load rather than
    // casting a null content URL.
    public async Task LoadAsync(DisplayLoad load, CancellationToken cancellationToken = default)
    {
        if (_client is not { } client) return;

        if (load.StreamUrl is not { } streamUrl)
        {
            _logger.LogWarning("Nothing streamable for {Name}: this receiver cannot mix stems", _connectedDeviceId);
            return;
        }

        var reachable = MakeReachableFromDevice(streamUrl, _lanAddress());
        _streamStartOffset = load.StartOffset;
        _rate = StreamRate.FromTempo(load.Tempo);

        _logger.LogInformation("Casting {Url} to {Name}", reachable, _connectedDeviceId);

        await GuardAsync(() => client.MediaChannel.LoadAsync(
            new Media { ContentUrl = reachable, StreamType = StreamType.Buffered }, autoPlay: false));
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
        => _client is { } c ? GuardAsync(c.MediaChannel.PlayAsync) : Task.CompletedTask;

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _client is { } c ? GuardAsync(c.MediaChannel.PauseAsync) : Task.CompletedTask;

    // No fade: Cast has no volume ramp, and faking one would move the TV's own level.
    /// <summary>The fade is ignored: a receiver stops where it is, having no mixer to ride down.</summary>
    public Task StopAsync(TimeSpan? fade = null, CancellationToken cancellationToken = default)
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
        if (_broker is not { } broker) return;

        _ = broker.PublishAsync(new DisplaysChanged());

        // The same change, said to an open device table: a sweep that finds a receiver has to
        // reach the dialog, which knows nothing about this transport's own message.
        _ = broker.PublishAsync(new PluginTableChanged());
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
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
