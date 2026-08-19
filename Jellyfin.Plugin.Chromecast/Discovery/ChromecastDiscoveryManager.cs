using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Chromecast.Configuration;
using Jellyfin.Plugin.Chromecast.Session;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;

namespace Jellyfin.Plugin.Chromecast.Discovery;

/// <summary>
/// Discovers Chromecast devices on the local network via mDNS and exposes each one as a
/// controllable Jellyfin <see cref="SessionInfo"/>, mirroring how the built-in DLNA PlayTo
/// feature turns discovered renderers into cast targets.
/// </summary>
public sealed class ChromecastDiscoveryManager : IDisposable
{
    private readonly ILogger<ChromecastDiscoveryManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ChromecastLocator _locator;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _addDeviceLock = new(1, 1);
    private readonly Timer _staleSweepTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromecastDiscoveryManager"/> class.
    /// </summary>
    public ChromecastDiscoveryManager(
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IServerApplicationHost appHost)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChromecastDiscoveryManager>();
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _appHost = appHost;

        _locator = new ChromecastLocator(loggerFactory.CreateLogger<ChromecastLocator>());
        _locator.ChromecastReceiverFound += OnChromecastReceiverFound;

        var staleCheckInterval = TimeSpan.FromSeconds(Math.Max(15, GetConfig().DeviceStaleAfterSeconds / 2));
        _staleSweepTimer = new Timer(SweepStaleDevices, null, staleCheckInterval, staleCheckInterval);
    }

    /// <summary>
    /// Starts continuous mDNS discovery of Chromecast devices.
    /// </summary>
    public void Start()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, GetConfig().DiscoveryIntervalSeconds));
        _logger.LogInformation("Starting Chromecast mDNS discovery (interval: {Interval})", interval);
        _locator.StartContinuousDiscovery(interval);
    }

    private static PluginConfiguration GetConfig() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private async void OnChromecastReceiverFound(object? sender, ChromecastReceiverEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var receiver = e.Receiver;
        var deviceId = GetStableDeviceId(receiver);
        _lastSeen[deviceId] = DateTime.UtcNow;

        await _addDeviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var existing = _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            var controller = existing?.SessionControllers.OfType<ChromecastSessionController>().FirstOrDefault();
            if (controller is not null)
            {
                // Already tracked - just refresh the receiver info in case the IP changed.
                controller.UpdateReceiver(receiver);
                return;
            }

            await AddDeviceAsync(receiver, deviceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering Chromecast device {Name}", receiver.Name);
        }
        finally
        {
            _addDeviceLock.Release();
        }
    }

    private async Task AddDeviceAsync(ChromecastReceiver receiver, string deviceId)
    {
        var config = GetConfig();
        var deviceName = string.IsNullOrEmpty(config.DeviceNamePrefix) ? receiver.Name : config.DeviceNamePrefix + receiver.Name;

        var sessionInfo = await _sessionManager
            .LogSessionActivity("Chromecast", _appHost.ApplicationVersionString, deviceId, deviceName, receiver.DeviceUri.Host, null)
            .ConfigureAwait(false);

        var serverAddress = GetServerAddress(receiver);

        var controller = new ChromecastSessionController(
            sessionInfo,
            receiver,
            serverAddress,
            _sessionManager,
            _libraryManager,
            _userManager,
            _mediaSourceManager,
            _loggerFactory.CreateLogger<ChromecastSessionController>());

        sessionInfo.AddController(controller);

        _sessionManager.ReportCapabilities(sessionInfo.Id, new ClientCapabilities
        {
            PlayableMediaTypes = new[] { MediaType.Video, MediaType.Audio },
            SupportedCommands = new[]
            {
                GeneralCommandType.VolumeUp,
                GeneralCommandType.VolumeDown,
                GeneralCommandType.Mute,
                GeneralCommandType.Unmute,
                GeneralCommandType.ToggleMute,
                GeneralCommandType.SetVolume
            },
            SupportsMediaControl = true
        });

        _logger.LogInformation("Chromecast session created for {Name} ({Model}) at {Uri}", receiver.Name, receiver.Model, receiver.DeviceUri);
    }

    private string GetServerAddress(ChromecastReceiver receiver)
    {
        try
        {
            var ip = IPAddress.Parse(receiver.DeviceUri.Host);
            return _appHost.GetSmartApiUrl(ip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve a smart API URL for {Host}, falling back to local access URL", receiver.DeviceUri.Host);
            return _appHost.GetApiUrlForLocalAccess();
        }
    }

    private static string GetStableDeviceId(ChromecastReceiver receiver)
    {
        if (receiver.ExtraInformation is not null && receiver.ExtraInformation.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id))
        {
            return "chromecast-" + id;
        }

        // Fall back to a hash of the name + host, which is stable as long as the device isn't renamed.
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(receiver.Name + "|" + receiver.DeviceUri.Host));
        return "chromecast-" + Convert.ToHexString(bytes);
    }

    private void SweepStaleDevices(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var maxAge = TimeSpan.FromSeconds(Math.Max(30, GetConfig().DeviceStaleAfterSeconds));
            var now = DateTime.UtcNow;

            foreach (var session in _sessionManager.Sessions)
            {
                var controller = session.SessionControllers.OfType<ChromecastSessionController>().FirstOrDefault();
                if (controller is null)
                {
                    continue;
                }

                if (_lastSeen.TryGetValue(session.DeviceId, out var lastSeen) && now - lastSeen > maxAge)
                {
                    _logger.LogInformation("Chromecast {Name} has not responded to discovery in {Age}, marking session inactive", session.DeviceName, now - lastSeen);
                    controller.MarkStale();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while sweeping for stale Chromecast sessions");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _locator.ChromecastReceiverFound -= OnChromecastReceiverFound;
        _locator.Dispose();
        _staleSweepTimer.Dispose();
        _addDeviceLock.Dispose();
    }
}
