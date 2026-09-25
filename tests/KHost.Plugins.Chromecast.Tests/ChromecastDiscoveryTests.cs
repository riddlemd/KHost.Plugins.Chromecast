using KHost.Plugins.Chromecast;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sharpcaster.Models;

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

    [Fact]
    public void ReportSweep_ZeroReceivers_SetsLastSweepFoundNothing()
    {
        _display.ReportSweep(0);

        Assert.True(_display.LastSweepFoundNothing);
    }

    [Fact]
    public void ReportSweep_SomeReceivers_ClearsLastSweepFoundNothing()
    {
        _display.ReportSweep(0);
        _display.ReportSweep(1);

        Assert.False(_display.LastSweepFoundNothing);
    }

    [Fact]
    public async Task StopDiscovery_ClearsLastSweepFoundNothing_SoARestartDoesNotInheritIt()
    {
        _display.ReportSweep(0);

        await _display.StopDiscoveryAsync();

        Assert.False(_display.LastSweepFoundNothing);
    }

    [Fact]
    public async Task ApplySweepResult_WithNothingFound_StillAnnouncesPluginTableChanged()
    {
        // The bug: a background resweep that finds nothing used to skip the announce entirely,
        // so an open device table never learned the sweep had even run.
        _display.ApplySweepResult(0, [], background: true);

        await _broker.Received(1).PublishAsync(Arg.Any<PluginTableChanged>());
    }

    [Fact]
    public async Task ApplySweepResult_WithAReceiver_RemembersItAndAnnounces()
    {
        _display.ApplySweepResult(1, [new ChromecastReceiver { Name = "Living Room" }]);

        Assert.Contains(_display.Devices, d => d.Name == "Living Room");
        await _broker.Received(1).PublishAsync(Arg.Any<PluginTableChanged>());
    }

    [Fact]
    public void ReportSweep_ZeroReceivers_InForeground_LogsAtInformation()
    {
        var logger = new CapturingLogger();
        using var display = new ChromecastDisplayProvider(
            logger, new ChromecastDisplayProvider.ServiceOptions(), Substitute.For<IMessageBroker>());

        display.ReportSweep(0, background: false);

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
    }

    [Fact]
    public void ReportSweep_ZeroReceivers_InBackground_LogsAtDebug_NotInformation()
    {
        var logger = new CapturingLogger();
        using var display = new ChromecastDisplayProvider(
            logger, new ChromecastDisplayProvider.ServiceOptions(), Substitute.For<IMessageBroker>());

        // A resweep repeats every 30s all night; an empty one must not spam Information forever.
        display.ReportSweep(0, background: true);

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Levels));
    }

    /// <summary>A hand-written fake rather than a substitute: NSubstitute cannot assert against
    /// <see cref="ILogger"/>'s generic <c>Log&lt;TState&gt;</c> without naming its internal state type.</summary>
    private sealed class CapturingLogger : ILogger<ChromecastDisplayProvider>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    public void Dispose()
    {
        _display.Dispose();
        GC.SuppressFinalize(this);
    }
}
