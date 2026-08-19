using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Chromecast.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Chromecast;

/// <summary>
/// Adds native Google Cast (Chromecast) support to Jellyfin's own "Play On" / remote-control
/// cast interface, so devices discovered on the local network can be selected from the existing
/// cast button in jellyfin-web and controlled the same way any other Jellyfin client session is.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Chromecast";

    /// <inheritdoc />
    public override string Description => "Cast to Google Cast devices from Jellyfin's built-in cast interface.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ec2dcff4-56f0-4a88-b0a8-f3910a7cd823");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
