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

    [Fact]
    public void CastService_ExposesNoScreenCommandSurface()
    {
        var takesCommands = typeof(IDisplayProvider).GetMethods()
            .Where(m => m.GetParameters().Any(p => typeof(IScreenCommand).IsAssignableFrom(p.ParameterType)))
            .Select(m => m.Name);

        // Accepting IScreenCommand is the doorway every future screen feature leaks through.
        Assert.True(!takesCommands.Any(), $"IDisplayProvider takes screen commands: {string.Join(", ", takesCommands)}");
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
