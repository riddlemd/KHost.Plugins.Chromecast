using System.Text.Json;
using KHost.Abstractions.Interactions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Services;
using KHost.Common.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpcaster;
using Sharpcaster.Models;

namespace KHost.Plugins.Chromecast.Tests;

/// <summary>A settings save reaches the next sweep or connect: nothing is copied at construction.</summary>
public class ChromecastLiveSettingsTests
{
    private readonly IOptionsMonitor<ChromecastSettings> _settings = Substitute.For<IOptionsMonitor<ChromecastSettings>>();

    private void Save(int discoverySeconds, int connectTimeoutSeconds)
        => _settings.CurrentValue.Returns(new ChromecastSettings
        {
            DiscoverySeconds = discoverySeconds,
            ConnectTimeoutSeconds = connectTimeoutSeconds,
        });

    private ChromecastDisplayProvider Build(
        Func<ChromecastLocator, TimeSpan, Task<IEnumerable<ChromecastReceiver>>>? zeroconfSweep = null,
        Func<string, TimeSpan, CancellationToken, Task<IReadOnlyList<BonjourBrowser.Service>>>? bonjourBrowse = null,
        Func<ChromecastClient, ChromecastReceiver, Task>? open = null)
        => new(
            NullLogger<ChromecastDisplayProvider>.Instance,
            new ChromecastDisplayProvider.ServiceOptions(),
            Substitute.For<IMessageBroker>(),
            optionsSource: ChromecastDisplayProvider.OptionsSourceFor(_settings),
            zeroconfSweep: zeroconfSweep,
            bonjourBrowse: bonjourBrowse,
            open: open,
            startContinuous: (_, _) => { });

    [Fact]
    public async Task ZeroconfSweep_UsesTheDiscoverySecondsSavedAfterConstruction()
    {
        Save(5, 10);
        TimeSpan? seen = null;
        using var display = Build(zeroconfSweep: (_, timeout) =>
        {
            seen = timeout;
            return Task.FromResult<IEnumerable<ChromecastReceiver>>([]);
        });

        Save(17, 10);
        await display.StartZeroconfDiscoveryAsync();

        Assert.Equal(TimeSpan.FromSeconds(17), seen);
    }

    [Fact]
    public async Task BonjourSweep_UsesTheDiscoverySecondsSavedAfterConstruction()
    {
        Save(5, 10);
        TimeSpan? seen = null;
        using var display = Build(bonjourBrowse: (_, timeout, _) =>
        {
            seen = timeout;
            return Task.FromResult<IReadOnlyList<BonjourBrowser.Service>>([]);
        });

        Save(23, 10);
        await display.StartBonjourDiscoveryAsync();

        Assert.Equal(TimeSpan.FromSeconds(23), seen);
    }

    [Fact]
    public async Task Connect_GivesUpAfterTheConnectTimeoutSavedAfterConstruction()
    {
        Save(5, 600);
        var never = new TaskCompletionSource();
        using var display = Build(open: (_, _) => never.Task);
        display.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });

        Save(5, 1);

        // Without the live read this waits the original 600 s (or the 10 s default); the guard fails the test instead.
        Assert.False(await display.ConnectAsync("Den").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Connect_KeepsWaiting_WhenTheTimeoutSavedAfterConstructionIsLonger()
    {
        Save(5, 1);
        var gate = new TaskCompletionSource();
        using var display = Build(open: (_, _) => gate.Task);
        display.Remember(new ChromecastReceiver { Name = "Den", DeviceUri = new Uri("https://127.0.0.1/") });

        Save(5, 600);
        var connecting = display.ConnectAsync("Den");

        await Task.Delay(2500);
        Assert.False(connecting.IsCompleted);
        gate.TrySetException(new InvalidOperationException("stop"));
        await connecting;
    }

    [Fact]
    public void Manifest_DeclaresTheCurrentApiVersion_AndNoLiveFlag()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));

        Assert.Equal(6, doc.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("settingsApplyLive", out _));
    }

    [Fact]
    public void EntryPoint_NamesTheSettingsClass()
        => Assert.IsAssignableFrom<IPlugin<ChromecastSettings>>(new ChromecastPlugin());

    [Fact]
    public void ZeroSeconds_FallBackToTheDefaults()
    {
        Save(0, 0);
        var options = ChromecastDisplayProvider.OptionsSourceFor(_settings)();

        Assert.Equal(TimeSpan.FromSeconds(5), options.DiscoveryTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectTimeout);
    }
}
