using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Chromecast.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;

namespace Jellyfin.Plugin.Chromecast.Session;

/// <summary>
/// Bridges a single Chromecast device into Jellyfin's session/remote-control system. Commands
/// sent through Jellyfin's normal "Play On" cast interface (Play, Playstate, GeneralCommand) are
/// translated into CastV2 protocol calls via SharpCaster, and playback status reported by the
/// Chromecast is relayed back to Jellyfin so every connected client sees accurate progress.
/// </summary>
public sealed class ChromecastSessionController : ISessionController, IAsyncDisposable
{
    /// <summary>
    /// The Google-operated "Default Media Receiver" app. It is publicly usable by any sender
    /// without registering a Cast application, and supports the MP4/WebM/HLS media the Jellyfin
    /// streaming endpoints below produce. A custom-branded Jellyfin receiver would require
    /// registering an application ID with Google, which is outside the scope of this plugin.
    /// </summary>
    private const string DefaultMediaReceiverAppId = "CC1AD845";

    private readonly SessionInfo _session;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<ChromecastSessionController> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Queue<Guid> _queue = new();

    private ChromecastReceiver _receiver;
    private readonly string _serverAddress;
    private ChromecastClient? _client;
    private BaseItem? _currentItem;
    private MediaSourceInfo? _currentMediaSource;
    private string? _currentPlaySessionId;
    private PlayMethod _currentPlayMethod;
    private int? _currentSubtitleStreamIndex;

    /// <summary>
    /// How many ticks into the original item the currently-loaded stream's local time zero
    /// corresponds to. Zero for a direct-played file (the Chromecast sees the whole file, so its
    /// own CurrentTime already is the absolute position). Set to the seek target when a transcode
    /// is reloaded starting mid-file, since the resulting stream's own timestamps restart at zero.
    /// </summary>
    private long _positionOffsetTicks;

    /// <summary>
    /// True while a LaunchApplication+Load sequence is in flight. The Default Media Receiver can
    /// broadcast an unsolicited "idle, nothing loaded" status right after the app launches and
    /// before our LOAD command takes effect; without this guard that status arrives while
    /// _currentItem is already set (for a fast, legitimate reason - see LoadMediaAsync) and gets
    /// misread as the just-started playback having immediately stopped. SharpCaster's LoadAsync
    /// already returns the load's own resulting status directly, so nothing is lost by ignoring
    /// the event-based status stream for this window.
    /// </summary>
    private volatile bool _loadInProgress;

    private bool _disposed;
    private volatile bool _stale;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromecastSessionController"/> class.
    /// </summary>
    public ChromecastSessionController(
        SessionInfo session,
        ChromecastReceiver receiver,
        string serverAddress,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        ILogger<ChromecastSessionController> logger)
    {
        _session = session;
        _receiver = receiver;
        _serverAddress = serverAddress;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSessionActive => !_disposed && !_stale;

    /// <inheritdoc />
    public bool SupportsMediaControl => IsSessionActive;

    /// <summary>
    /// Refreshes the known network location for this device after it responds to a fresh
    /// discovery probe (its IP address may have changed via DHCP).
    /// </summary>
    public void UpdateReceiver(ChromecastReceiver receiver)
    {
        _receiver = receiver;
        _stale = false;
    }

    /// <summary>
    /// Marks this session inactive because the device has not answered discovery recently.
    /// It will reactivate automatically if it is seen again.
    /// </summary>
    public void MarkStale() => _stale = true;

    /// <inheritdoc />
    public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return name switch
        {
            SessionMessageType.Play => SendPlayCommand(data as PlayRequest, cancellationToken),
            SessionMessageType.Playstate => SendPlaystateCommand(data as PlaystateRequest, cancellationToken),
            SessionMessageType.GeneralCommand => SendGeneralCommand(data as GeneralCommand, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task SendPlayCommand(PlayRequest? command, CancellationToken cancellationToken)
    {
        if (command?.ItemIds is null || command.ItemIds.Length == 0)
        {
            return;
        }

        _queue.Clear();
        foreach (var id in command.ItemIds)
        {
            _queue.Enqueue(id);
        }

        var user = command.ControllingUserId == Guid.Empty ? null : _userManager.GetUserById(command.ControllingUserId);
        var firstItemId = _queue.Dequeue();

        await PlayItemAsync(
            firstItemId,
            user,
            command.StartPositionTicks ?? 0,
            command.MediaSourceId,
            command.SubtitleStreamIndex,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PlayItemAsync(
        Guid itemId,
        User? user,
        long startPositionTicks,
        string? mediaSourceId,
        int? subtitleStreamIndex,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("Chromecast play requested for unknown item {ItemId}", itemId);
            return;
        }

        var mediaSources = _mediaSourceManager.GetStaticMediaSources(item, true, user);
        var mediaSource = (!string.IsNullOrEmpty(mediaSourceId)
            ? mediaSources.FirstOrDefault(m => string.Equals(m.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase))
            : null) ?? mediaSources.FirstOrDefault();

        if (mediaSource is null)
        {
            _logger.LogWarning("No playable media source found for {ItemName}", item.Name);
            return;
        }

        await LoadMediaAsync(item, mediaSource, startPositionTicks, subtitleStreamIndex, isNewItem: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads media onto the Chromecast, either as a fresh "play this item" (<paramref name="isNewItem"/>
    /// = true) or as a reload of the currently-playing item at a new position - the latter is how
    /// seeking/skipping is implemented for transcoded streams (see <see cref="SendPlaystateCommand"/>),
    /// since a live single-pass transcode has no seekable byte range for the Chromecast to jump
    /// within; the only way to "seek" it is to start a new transcode at the target offset.
    /// </summary>
    private async Task LoadMediaAsync(
        BaseItem item,
        MediaSourceInfo mediaSource,
        long startPositionTicks,
        int? subtitleStreamIndex,
        bool isNewItem,
        CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var playSessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var built = StreamUrlBuilder.Build(item, mediaSource, _serverAddress, _session.DeviceId, playSessionId, subtitleStreamIndex, config.PreferDirectPlay, startPositionTicks);

        // For a transcode, startPositionTicks is baked into the URL and the resulting stream's own
        // timestamps start at zero, so status updates need this offset added back to report an
        // absolute position. A direct-played file is the whole file regardless of seek position, so
        // the Chromecast's own CurrentTime is already absolute.
        _positionOffsetTicks = built.PlayMethod == PlayMethod.Transcode ? startPositionTicks : 0;

        var durationTicks = mediaSource.RunTimeTicks - (built.PlayMethod == PlayMethod.Transcode ? startPositionTicks : 0);

        _logger.LogInformation(
            "Casting {ItemName} to {DeviceName}: {PlayMethod}, start={StartPositionTicks} url={Url}",
            item.Name,
            _receiver.Name,
            built.PlayMethod,
            startPositionTicks,
            built.Url);

        _loadInProgress = true;
        try
        {
            await client.LaunchApplicationAsync(DefaultMediaReceiverAppId, true).ConfigureAwait(false);

            var media = new Media
            {
                ContentId = built.Url,
                ContentType = built.ContentType,
                StreamType = StreamType.Buffered,
                Duration = durationTicks.HasValue ? durationTicks.Value / 10_000_000d : null,
                Metadata = new MediaMetadata
                {
                    Title = item.Name,
                    SubTitle = item.GetParent()?.Name,
                    Images = built.ImageUrl is null ? null : new[] { new Image { Url = built.ImageUrl } }
                },
                Tracks = built.SubtitleTrack is null ? null : new[] { built.SubtitleTrack }
            };

            _currentItem = item;
            _currentMediaSource = mediaSource;
            _currentPlaySessionId = playSessionId;
            _currentPlayMethod = built.PlayMethod;
            _currentSubtitleStreamIndex = subtitleStreamIndex;

            var activeTrackIds = built.SubtitleTrack is null ? null : new[] { built.SubtitleTrack.TrackId };

            // currentTime tells the receiver what position to start playback (and its own
            // on-screen display) from. For a direct-played file this is a normal in-stream seek;
            // for a transcode reload the underlying stream only contains content from
            // startPositionTicks onward, so currentTime just needs to be relative to *that*
            // stream's own start (i.e. 0), which is already the default.
            var loadCurrentTime = built.PlayMethod == PlayMethod.DirectStream && startPositionTicks > 0
                ? startPositionTicks / 10_000_000d
                : (double?)null;

            await client.MediaChannel.LoadAsync(media, true, activeTrackIds, loadCurrentTime).ConfigureAwait(false);
        }
        finally
        {
            _loadInProgress = false;
        }

        if (isNewItem)
        {
            await _sessionManager.OnPlaybackStart(new PlaybackStartInfo
            {
                ItemId = item.Id,
                SessionId = _session.Id,
                MediaSourceId = mediaSource.Id,
                PlaySessionId = playSessionId,
                PositionTicks = startPositionTicks,
                CanSeek = true,
                PlayMethod = built.PlayMethod
            }).ConfigureAwait(false);
        }
    }

    private async Task SendPlaystateCommand(PlaystateRequest? command, CancellationToken cancellationToken)
    {
        if (command is null || _client is null)
        {
            return;
        }

        switch (command.Command)
        {
            case PlaystateCommand.Stop:
                await StopAsync().ConfigureAwait(false);
                break;
            case PlaystateCommand.Pause:
                await _client.MediaChannel.PauseAsync().ConfigureAwait(false);
                break;
            case PlaystateCommand.Unpause:
                await _client.MediaChannel.PlayAsync().ConfigureAwait(false);
                break;
            case PlaystateCommand.PlayPause:
                if (_client.MediaChannel.MediaStatus?.PlayerState == PlayerStateType.Playing)
                {
                    await _client.MediaChannel.PauseAsync().ConfigureAwait(false);
                }
                else
                {
                    await _client.MediaChannel.PlayAsync().ConfigureAwait(false);
                }

                break;
            case PlaystateCommand.Seek:
                await SeekAsync(command.SeekPositionTicks ?? 0, cancellationToken).ConfigureAwait(false);
                break;
            case PlaystateCommand.NextTrack:
                if (_queue.Count > 0)
                {
                    await PlayItemAsync(_queue.Dequeue(), null, 0, null, null, cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                _logger.LogDebug("Playstate command {Command} is not supported for Chromecast sessions", command.Command);
                break;
        }
    }

    private async Task SeekAsync(long positionTicks, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return;
        }

        if (_currentPlayMethod == PlayMethod.Transcode && _currentItem is not null && _currentMediaSource is not null)
        {
            // A live single-pass transcode has no seekable byte range for the Chromecast to jump
            // within - the only way to actually change position is to start a new transcode at
            // the target offset and load that instead. See LoadMediaAsync for the offset bookkeeping
            // this requires for position reporting to stay correct afterwards.
            await LoadMediaAsync(_currentItem, _currentMediaSource, positionTicks, _currentSubtitleStreamIndex, isNewItem: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _client.MediaChannel.SeekAsync(positionTicks / 10_000_000d).ConfigureAwait(false);
        }
    }

    private async Task StopAsync()
    {
        _queue.Clear();

        if (_client is not null)
        {
            try
            {
                await _client.MediaChannel.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error stopping Chromecast media - it may already be idle");
            }
        }

        if (_currentItem is not null && _currentMediaSource is not null)
        {
            var lastKnownPosition = _positionOffsetTicks + (long)((_client?.MediaChannel.MediaStatus?.CurrentTime ?? 0) * 10_000_000);

            await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
            {
                ItemId = _currentItem.Id,
                SessionId = _session.Id,
                MediaSourceId = _currentMediaSource.Id,
                PlaySessionId = _currentPlaySessionId,
                PositionTicks = lastKnownPosition
            }).ConfigureAwait(false);
        }

        _currentItem = null;
        _currentMediaSource = null;
        _currentPlaySessionId = null;
        _currentSubtitleStreamIndex = null;
        _positionOffsetTicks = 0;
    }

    private Task SendGeneralCommand(GeneralCommand? command, CancellationToken cancellationToken)
    {
        if (command is null || _client is null)
        {
            return Task.CompletedTask;
        }

        var currentVolume = _client.ChromecastStatus?.Volume?.Level ?? 0.5;
        var isMuted = _client.ChromecastStatus?.Volume?.Muted ?? false;

        switch (command.Name)
        {
            case GeneralCommandType.VolumeUp:
                return _client.ReceiverChannel.SetVolume(Math.Clamp(currentVolume + 0.05, 0, 1));
            case GeneralCommandType.VolumeDown:
                return _client.ReceiverChannel.SetVolume(Math.Clamp(currentVolume - 0.05, 0, 1));
            case GeneralCommandType.Mute:
                return _client.ReceiverChannel.SetMute(true);
            case GeneralCommandType.Unmute:
                return _client.ReceiverChannel.SetMute(false);
            case GeneralCommandType.ToggleMute:
                return _client.ReceiverChannel.SetMute(!isMuted);
            case GeneralCommandType.SetVolume:
                if (command.Arguments.TryGetValue("Volume", out var volStr)
                    && double.TryParse(volStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                {
                    return _client.ReceiverChannel.SetVolume(Math.Clamp(vol / 100d, 0, 1));
                }

                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private async Task<ChromecastClient?> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var client = new ChromecastClient();
            await client.ConnectChromecast(_receiver).ConfigureAwait(false);
            client.MediaChannel.StatusChanged += OnMediaStatusChanged;
            client.Disconnected += OnClientDisconnected;
            _client = client;
            return _client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Chromecast {Name}", _receiver.Name);
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async void OnMediaStatusChanged(object? sender, MediaStatus status)
    {
        if (_disposed || _loadInProgress || _currentItem is null || _currentMediaSource is null)
        {
            return;
        }

        try
        {
            var positionTicks = _positionOffsetTicks + (long)(status.CurrentTime * 10_000_000);

            if (status.PlayerState == PlayerStateType.Idle)
            {
                if (string.Equals(status.IdleReason, "FINISHED", StringComparison.OrdinalIgnoreCase) && _queue.Count > 0)
                {
                    var next = _queue.Dequeue();
                    await PlayItemAsync(next, null, 0, null, null, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                var item = _currentItem;
                var mediaSource = _currentMediaSource;
                var playSessionId = _currentPlaySessionId;

                _currentItem = null;
                _currentMediaSource = null;
                _currentPlaySessionId = null;
                _currentSubtitleStreamIndex = null;
                _positionOffsetTicks = 0;

                await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = item.Id,
                    SessionId = _session.Id,
                    MediaSourceId = mediaSource.Id,
                    PlaySessionId = playSessionId,
                    PositionTicks = positionTicks
                }).ConfigureAwait(false);
            }
            else
            {
                await _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = _currentItem.Id,
                    SessionId = _session.Id,
                    MediaSourceId = _currentMediaSource.Id,
                    PlaySessionId = _currentPlaySessionId,
                    PositionTicks = positionTicks,
                    IsPaused = status.PlayerState == PlayerStateType.Paused,
                    IsMuted = status.Volume?.Muted ?? false,
                    PlayMethod = _currentPlayMethod,
                    CanSeek = true
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Chromecast media status update");
        }
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Chromecast {Name} disconnected", _receiver.Name);
        _currentItem = null;
        _currentMediaSource = null;

        var client = _client;
        _client = null;

        if (client is not null)
        {
            client.MediaChannel.StatusChanged -= OnMediaStatusChanged;
            client.Disconnected -= OnClientDisconnected;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var client = _client;
        _client = null;

        if (client is not null)
        {
            client.MediaChannel.StatusChanged -= OnMediaStatusChanged;
            client.Disconnected -= OnClientDisconnected;

            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disconnecting Chromecast {Name} during dispose", _receiver.Name);
            }
        }

        _connectLock.Dispose();
    }
}
