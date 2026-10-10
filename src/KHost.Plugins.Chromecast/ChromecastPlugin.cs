using KHost.Abstractions.Services;

namespace KHost.Plugins.Chromecast;

/// <summary>Names the settings class so the host binds it and serves it as options; the display
/// provider is discovered separately.</summary>
public sealed class ChromecastPlugin : IPlugin<ChromecastSettings>;
