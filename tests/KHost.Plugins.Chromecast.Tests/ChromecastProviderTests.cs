using System.Reflection;
using KHost.Abstractions.Services;
using KHost.Abstractions.Services.IPC;
using KHost.Plugins.Chromecast;

namespace KHost.Plugins.Chromecast.Tests;

public class ChromecastProviderSeparationTests
{
    [Fact]
    public void CastService_IsNotAScreen()
    {
        var implemented = typeof(ChromecastDisplayProvider).GetInterfaces();

        // If it implements a screen interface again it is back in the role system by accident.
        Assert.DoesNotContain(typeof(IScreenServer), implemented);
        Assert.DoesNotContain(typeof(IScreenConnection), implemented);
        Assert.DoesNotContain(typeof(IScreenProvider), implemented);
    }

    /// <summary>The interface now carries the whole drawable surface, every member of it with a
    /// default body. A receiver draws none of it, and the way it says so is by implementing none
    /// of it — an override here would be a screen feature arriving inside the Cast plugin.</summary>
    [Fact]
    public void CastService_ImplementsNothingDrawable()
    {
        var drawable = typeof(IDisplayProvider).GetMethods()
            .Where(m => m.GetParameters().Any(p => typeof(IScreenCommand).IsAssignableFrom(p.ParameterType)))
            .ToList();

        Assert.NotEmpty(drawable);

        var map = typeof(ChromecastDisplayProvider).GetInterfaceMap(typeof(IDisplayProvider));

        var overridden = drawable
            .Where(method =>
            {
                var index = Array.IndexOf(map.InterfaceMethods, method);

                // Declared on the interface itself is the default body; declared on the provider
                // is an override, which is what this test exists to catch.
                return index >= 0 && map.TargetMethods[index].DeclaringType == typeof(ChromecastDisplayProvider);
            })
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            overridden.Count == 0,
            $"The Cast provider draws things it cannot draw: {string.Join(", ", overridden)}");
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
        Assert.False(device.SupportsLyrics);
        Assert.False(device.SupportsMarquee);
        Assert.False(device.SupportsQrCodes);
        Assert.False(device.SupportsImage);
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
