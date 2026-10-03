using KHost.Abstractions.Interactions;
using KHost.Abstractions.Interactions.Requests;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Common.Discovery;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>A receiver or the network failing is told to the host in one line and never stops the show.</summary>
public class ChromecastOfflineTests
{
    private readonly IFlashService _flash = Substitute.For<IFlashService>();
    private readonly CapturingLogger _logger = new();

    private ChromecastDisplayProvider Build(
        Func<ChromecastLocator, TimeSpan, Task<IEnumerable<ChromecastReceiver>>>? zeroconfSweep = null,
        Func<string, TimeSpan, CancellationToken, Task<IReadOnlyList<BonjourBrowser.Service>>>? bonjourBrowse = null,
        Func<ChromecastClient, ChromecastReceiver, Task>? open = null,
        Action<ChromecastLocator, TimeSpan>? startContinuous = null,
        Func<ChromecastClient, Task>? relaunch = null,
        IInteractionDispatcher? dispatcher = null,
        TimeSpan? connectTimeout = null)
        => new(
            _logger,
            new ChromecastDisplayProvider.ServiceOptions { ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10) },
            Substitute.For<IMessageBroker>(),
            dispatcher,
            zeroconfSweep: zeroconfSweep,
            bonjourBrowse: bonjourBrowse,
            flash: _flash,
            open: open,
            startContinuous: startContinuous ?? ((_, _) => { }),
            relaunch: relaunch ?? (_ => throw new InvalidOperationException("receiver gone")));

    private static Task Refused() => throw new InvalidOperationException("refused");

    private void ShownOnce(string text)
        => _flash.Received(1).Show(text, FlashType.Warning);

    // --- connect ---

    [Fact]
    public async Task ConnectAsync_WhenItFails_DoesNotFlash_BecauseTheCallerOwnsThatLine()
    {
        using var cast = Build(open: (_, _) => throw new InvalidOperationException("refused"));
        cast.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });

        Assert.False(await cast.ConnectAsync("Den"));

        _flash.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    [Fact]
    public async Task ConnectAsync_AbandonedAfterTheTimeout_ObservesALaterFault()
    {
        var gate = new TaskCompletionSource();
        using var cast = Build(open: (_, _) => gate.Task, connectTimeout: TimeSpan.FromMilliseconds(50));
        cast.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });

        Assert.False(await cast.ConnectAsync("Den"));

        gate.SetException(new InvalidOperationException("late fault"));

        await _logger.WaitForAsync(e => e.Level == LogLevel.Debug && e.Message.Contains("Abandoned connect"));
    }

    // --- dropped mid-show ---

    [Fact]
    public void OnDeviceDropped_PromisesAReconnect_OnlyWhenOneFollows()
    {
        using var cast = Build();
        cast.Adopt("Den", null);

        cast.OnDeviceDropped("Den", willRetry: true);

        ShownOnce("Chromecast: lost Den; trying to reconnect.");
    }

    [Fact]
    public void OnDeviceDropped_WithNoPickUp_JustSaysItWasLost()
    {
        using var cast = Build();
        cast.Adopt("Den", null);

        cast.OnDeviceDropped("Den");

        ShownOnce("Chromecast: lost Den.");
    }

    [Fact]
    public void OnDeviceDropped_ForADeviceThatIsNotConnected_SaysNothing()
    {
        using var cast = Build();
        cast.Adopt("Den", null);

        cast.OnDeviceDropped("Kitchen", willRetry: true);

        _flash.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    [Fact]
    public async Task ARefusedCommand_FlashesTheDropThenThatThePickUpFailed_AndDoesNotThrow()
    {
        using var cast = Build();
        cast.Adopt("Den", new ChromecastClient());

        await cast.GuardAsync(Refused);

        Received.InOrder(() =>
        {
            _flash.Show("Chromecast: lost Den; trying to reconnect.", FlashType.Warning);
            _flash.Show("Chromecast: Den did not come back.", FlashType.Warning);
        });
        _flash.ReceivedWithAnyArgs(2).Show(default!, default);
    }

    [Fact]
    public async Task APickUpThatSucceeds_DoesNotFlashTheFailure()
    {
        using var cast = Build(open: (_, _) => Task.CompletedTask);
        cast.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });
        cast.Adopt("Den", new ChromecastClient());

        await cast.GuardAsync(Refused);

        _flash.DidNotReceive().Show("Chromecast: Den did not come back.", Arg.Any<FlashType>());
        Assert.Equal("Den", cast.ConnectedDeviceId);
    }

    [Fact]
    public async Task ARelaunchThatSucceeds_KeepsTheSession_AndSaysNothing()
    {
        var opened = 0;
        using var cast = Build(open: (_, _) => { opened++; return Task.CompletedTask; }, relaunch: _ => Task.CompletedTask);
        cast.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });
        cast.Adopt("Den", new ChromecastClient());

        await cast.GuardAsync(Refused);

        _flash.DidNotReceiveWithAnyArgs().Show(default!, default);
        Assert.Equal(0, opened);
        Assert.Equal("Den", cast.ConnectedDeviceId);
    }

    [Fact]
    public async Task AFlashThatThrows_DoesNotStopTheShow()
    {
        _flash.When(f => f.Show(Arg.Any<string>(), Arg.Any<FlashType>())).Throw(new InvalidOperationException("no UI"));
        using var cast = Build();
        cast.Adopt("Den", new ChromecastClient());

        await cast.GuardAsync(Refused);

        Assert.Null(cast.ConnectedDeviceId);
    }

    // --- sweeps ---

    [Fact]
    public async Task AFailedZeroconfSweep_IsRecorded_ShownInTheTable_AndFlashedOnce()
    {
        using var cast = Build(zeroconfSweep: (_, _) => throw new InvalidOperationException("no multicast"));

        await cast.StartZeroconfDiscoveryAsync();

        Assert.True(cast.LastSweepFailed);
        Assert.True(cast.IsDiscovering);
        var content = await ChromecastDeviceTable.RequestFor(cast).LoadAsync(CancellationToken.None);
        Assert.Equal("Could not search the network.", content.EmptyMessage);
        ShownOnce("Chromecast: could not search the network.");
    }

    [Fact]
    public async Task AFailedBonjourSweep_IsRecorded_ShownInTheTable_AndFlashedOnce()
    {
        using var cast = Build(bonjourBrowse: (_, _, _) => throw new InvalidOperationException("no daemon"));

        await cast.StartBonjourDiscoveryAsync();

        Assert.True(cast.LastSweepFailed);
        var content = await ChromecastDeviceTable.RequestFor(cast).LoadAsync(CancellationToken.None);
        Assert.Equal("Could not search the network.", content.EmptyMessage);
        ShownOnce("Chromecast: could not search the network.");
    }

    [Fact]
    public async Task ASweepThatSucceedsAfterAFailure_ClearsIt()
    {
        using var cast = Build(zeroconfSweep: (_, _) => throw new InvalidOperationException("no multicast"));
        await cast.StartZeroconfDiscoveryAsync();

        cast.ReportSweep(0);

        Assert.False(cast.LastSweepFailed);
    }

    [Fact]
    public async Task StopDiscovery_ClearsAFailedSweep_SoARestartDoesNotInheritIt()
    {
        using var cast = Build(zeroconfSweep: (_, _) => throw new InvalidOperationException("no multicast"));
        await cast.StartZeroconfDiscoveryAsync();

        await cast.StopDiscoveryAsync();

        Assert.False(cast.LastSweepFailed);
    }

    [Fact]
    public async Task StartingAgainAfterADisarmingFailure_FlashesAgain_BecauseTheHostAskedAgain()
    {
        using var cast = Build(
            zeroconfSweep: (_, _) => throw new InvalidOperationException("socket"),
            startContinuous: (_, _) => throw new InvalidOperationException("socket"));
        await cast.StartZeroconfDiscoveryAsync();

        await cast.StartZeroconfDiscoveryAsync();

        _flash.Received(2).Show("Chromecast: could not search the network.", FlashType.Warning);
    }

    [Fact]
    public async Task ASweepFailure_RepeatingWithTheSameReason_WarnsAndFlashesOnce_ThenLogsDebug()
    {
        var calls = 0;
        using var cast = Build(bonjourBrowse: (_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("no daemon");
        });

        await cast.StartBonjourDiscoveryAsync();
        // The loop's own resweeps arrive through the same path; two more of the same reason.
        await cast.SweepBonjourAsync(background: true);
        await cast.SweepBonjourAsync(background: true);

        Assert.Equal(3, calls);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("sweep failed")));
        ShownOnce("Chromecast: could not search the network.");
    }

    [Fact]
    public async Task ASweepFailure_WithANewReason_WarnsAndFlashesAgain()
    {
        var reason = "no daemon";
        using var cast = Build(bonjourBrowse: (_, _, _) => throw new InvalidOperationException(reason));
        await cast.StartBonjourDiscoveryAsync();

        reason = "daemon crashed";
        await cast.SweepBonjourAsync(background: true);

        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Warning));
        _flash.Received(2).Show("Chromecast: could not search the network.", FlashType.Warning);
    }

    [Fact]
    public async Task AFailedContinuousStart_DisarmsDiscovery_AndReportsIt()
    {
        using var cast = Build(
            zeroconfSweep: (_, _) => Task.FromResult<IEnumerable<ChromecastReceiver>>([]),
            startContinuous: (_, _) => throw new InvalidOperationException("socket"));

        await cast.StartZeroconfDiscoveryAsync();

        Assert.False(cast.IsDiscovering);
        Assert.True(cast.LastSweepFailed);
        ShownOnce("Chromecast: could not search the network.");
    }

    [Fact]
    public async Task AHealthyZeroconfStart_StaysArmed_AndSaysNothing()
    {
        using var cast = Build(zeroconfSweep: (_, _) => Task.FromResult<IEnumerable<ChromecastReceiver>>([]));

        await cast.StartZeroconfDiscoveryAsync();

        Assert.True(cast.IsDiscovering);
        Assert.False(cast.LastSweepFailed);
        _flash.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    [Fact]
    public async Task TheDeviceTableTheHostOpens_FlashesAFailedShowHere()
    {
        ShowPluginTableRequest? shown = null;
        var dispatcher = Substitute.For<IInteractionDispatcher>();
        dispatcher.RequestAsync(Arg.Do<IInteractionRequest>(r => shown = r as ShowPluginTableRequest), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        using var cast = Build(
            open: (_, _) => throw new InvalidOperationException("refused"),
            dispatcher: dispatcher);
        cast.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });

        await cast.InvokeButtonAsync(ChromecastDisplayProvider.DevicesButtonKey);
        var content = await shown!.LoadAsync(CancellationToken.None);
        await Assert.Single(Assert.Single(content.Rows).Actions).PerformAsync(CancellationToken.None);

        ShownOnce("Chromecast: could not connect to Den. It did not answer on the network.");
    }

    private sealed class CapturingLogger : ILogger<ChromecastDisplayProvider>
    {
        internal sealed record Entry(LogLevel Level, string Message);

        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add(new Entry(logLevel, formatter(state, exception)));
        }

        public async Task WaitForAsync(Func<Entry, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (Entries.Any(match)) return;
                await Task.Delay(10);
            }

            Assert.Fail("Expected log entry never arrived.");
        }
    }
}
