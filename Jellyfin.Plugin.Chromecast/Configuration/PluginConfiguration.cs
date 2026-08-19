using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chromecast.Configuration;

/// <summary>
/// Configuration for the Chromecast plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        DiscoveryIntervalSeconds = 30;
        DeviceStaleAfterSeconds = 120;
        DeviceNamePrefix = string.Empty;
        PreferDirectPlay = true;
        EnableDebugLogging = false;
    }

    /// <summary>
    /// Gets or sets how often (in seconds) the plugin scans the local network for Chromecast devices.
    /// </summary>
    public int DiscoveryIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets how long (in seconds) a Chromecast device may go unseen before its session is
    /// marked inactive and removed from the "Play On" device list.
    /// </summary>
    public int DeviceStaleAfterSeconds { get; set; }

    /// <summary>
    /// Gets or sets an optional prefix added to the device name shown in the Jellyfin cast menu,
    /// e.g. "Chromecast - " so it is easy to tell apart from other session types.
    /// </summary>
    public string DeviceNamePrefix { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to direct-play/direct-stream sources that are already
    /// compatible with the Chromecast default media receiver (H.264/AAC/MP3 in MP4/WebM) instead of
    /// always transcoding.
    /// </summary>
    public bool PreferDirectPlay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether verbose Chromecast/CastV2 protocol logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; }
}
