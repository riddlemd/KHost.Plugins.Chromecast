namespace KHost.Plugins.Chromecast;

/// <summary>Bound from the manifest's declared settings; the keys match by name.</summary>
/// <remarks>Seconds rather than TimeSpan: a manifest setting is an int, and a host reading the
/// Plugins page should not have to write a duration format.</remarks>
public sealed class ChromecastSettings
{
    /// <summary>How long a browse sweeps before reporting what it found.</summary>
    public int DiscoverySeconds { get; set; } = 5;

    /// <summary>mDNS can advertise an address nothing here can reach (a VPN interface does it), so
    /// a receiver that never answers has to give up rather than hang the dialog.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;
}
