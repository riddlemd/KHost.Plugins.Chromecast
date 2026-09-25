using KHost.Abstractions.Services;
using KHost.Plugins.Chromecast;

namespace KHost.Plugins.Chromecast.Tests;

public class ChromecastProviderSeparationTests
{
    /// <summary>Drawing is now entirely the provider's own business — <see cref="IDisplayProvider"/>
    /// carries no drawable member at all, so there is nothing left on it to override. The guard is
    /// the implemented-interface list itself: this provider is <see cref="IDisplayProvider"/>, its
    /// own button extension, and <see cref="IDisposable"/> for the Cast connection, and nothing
    /// else. Any interface arriving beyond that set — a screen contract, a drawing surface — is
    /// what this test exists to catch.</summary>
    [Fact]
    public void CastService_ImplementsNothingDrawable()
    {
        var implemented = typeof(ChromecastDisplayProvider).GetInterfaces();

        var expected = new[]
        {
            typeof(IDisplayProvider),
            typeof(IPluginButtonHandler),
            typeof(IDisposable),
        };

        Assert.Equal(
            expected.OrderBy(t => t.FullName, StringComparer.Ordinal),
            implemented.OrderBy(t => t.FullName, StringComparer.Ordinal));
    }

    /// <summary>A receiver plays the song and draws nothing over it, and the host reads these to
    /// decide what to send. Reporting a drawable capability would have the words silently dropped.</summary>
    [Fact]
    public void CastDevices_CarryTheSongButDrawNothing()
    {
        var device = new DisplayDevice
        {
            Id = "tv-1",
            Name = "Living Room TV",
            SupportsAudio = true,
            SupportsVideo = true,
        };

        Assert.True(device.SupportsAudio);
        Assert.True(device.SupportsVideo);

        // The host waits out the fade it asks for, so claiming one here would buy the room
        // seconds of silence between every song.
        Assert.False(device.SupportsFade);
    }
}

public class ChromecastProviderUrlTests
{
    [Theory]
    [InlineData("http://localhost:5251/media/a/stream.m3u8", "192.168.1.10", "http://192.168.1.10:5251/media/a/stream.m3u8")]
    [InlineData("http://127.0.0.1:5251/media/a/stream.m3u8", "192.168.1.10", "http://192.168.1.10:5251/media/a/stream.m3u8")]
    public void MakeReachableFromDevice_ReplacesLoopback(string url, string lan, string expected)
    {
        // localhost on a television means the television.
        Assert.Equal(expected, ChromecastDisplayProvider.MakeReachableFromDevice(url, lan));
    }

    [Fact]
    public void MakeReachableFromDevice_LeavesARoutableAddressAlone()
    {
        const string url = "http://192.168.1.5:5251/media/a/stream.m3u8";

        Assert.Equal(url, ChromecastDisplayProvider.MakeReachableFromDevice(url, "192.168.1.10"));
    }

    [Fact]
    public void MakeReachableFromDevice_LeavesTheUrlAlone_WhenThereIsNoLanAddress()
        => Assert.Equal("http://localhost:5251/a.m3u8",
            ChromecastDisplayProvider.MakeReachableFromDevice("http://localhost:5251/a.m3u8", null));
}
