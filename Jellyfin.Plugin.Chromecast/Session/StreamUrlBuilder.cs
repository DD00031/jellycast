using System;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Sharpcaster.Models.Media;

namespace Jellyfin.Plugin.Chromecast.Session;

/// <summary>
/// Resolves a Jellyfin library item and media source into a URL, MIME type and Google Cast
/// track list that the Chromecast default media receiver can play directly.
/// </summary>
public static class StreamUrlBuilder
{
    private static readonly string[] DirectPlayVideoContainers = { "mp4", "m4v", "mov" };
    private static readonly string[] DirectPlayAudioContainers = { "mp3", "aac", "m4a", "mp4" };
    private static readonly string[] DirectPlayVideoCodecs = { "h264" };
    private static readonly string[] DirectPlayAudioCodecs = { "aac", "mp3" };

    /// <summary>
    /// The resolved playback details for a single item.
    /// </summary>
    /// <param name="Url">The absolute URL the Chromecast should fetch the media from.</param>
    /// <param name="ContentType">The MIME type of the stream.</param>
    /// <param name="PlayMethod">Whether this is a direct stream or a transcode.</param>
    /// <param name="ImageUrl">An optional poster/primary image URL.</param>
    /// <param name="SubtitleTrack">An optional WebVTT subtitle track.</param>
    public sealed record BuiltStream(string Url, string ContentType, PlayMethod PlayMethod, string? ImageUrl, Track? SubtitleTrack);

    /// <summary>
    /// Builds the stream information for the given item/media source pair.
    /// </summary>
    public static BuiltStream Build(
        BaseItem item,
        MediaSourceInfo mediaSource,
        string serverAddress,
        string deviceId,
        string playSessionId,
        int? subtitleStreamIndex,
        bool preferDirectPlay,
        long startPositionTicks = 0)
    {
        serverAddress = serverAddress.TrimEnd('/');
        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);
        var mediaSourceId = Uri.EscapeDataString(string.IsNullOrEmpty(mediaSource.Id) ? itemId : mediaSource.Id);
        var escapedDeviceId = Uri.EscapeDataString(deviceId);
        var isAudioOnly = mediaSource.VideoStream is null;

        string url;
        string contentType;
        PlayMethod playMethod;

        if (!isAudioOnly)
        {
            if (preferDirectPlay && TryGetDirectPlayVideoContainer(mediaSource, out var container))
            {
                url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/Videos/{1}/stream.{2}?static=true&mediaSourceId={3}&deviceId={4}",
                    serverAddress,
                    itemId,
                    container,
                    mediaSourceId,
                    escapedDeviceId);
                contentType = "video/mp4";
                playMethod = PlayMethod.DirectStream;
            }
            else
            {
                // A raw progressive stream.mp4 transcode is a live, single-pass ffmpeg encode with
                // no upfront duration (the receiver has to guess from however much of the growing
                // file it has buffered, which is why it can show a wildly short "duration" early
                // on) and no real seek support. HLS instead gives the receiver a manifest that
                // declares the actual segment-based duration before playback starts, and supports
                // genuine seeking by requesting different segments - this is also what Jellyfin's
                // own web and mobile clients use for transcoded playback.
                url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/Videos/{1}/master.m3u8?videoCodec=h264&audioCodec=aac&maxAudioChannels=2&mediaSourceId={2}&deviceId={3}&playSessionId={4}",
                    serverAddress,
                    itemId,
                    mediaSourceId,
                    escapedDeviceId,
                    Uri.EscapeDataString(playSessionId));
                if (startPositionTicks > 0)
                {
                    url += "&startTimeTicks=" + startPositionTicks.ToString(CultureInfo.InvariantCulture);
                }

                contentType = "application/x-mpegURL";
                playMethod = PlayMethod.Transcode;
            }
        }
        else
        {
            if (preferDirectPlay && TryGetDirectPlayAudioContainer(mediaSource, out var container))
            {
                url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/Audio/{1}/stream.{2}?static=true&mediaSourceId={3}&deviceId={4}",
                    serverAddress,
                    itemId,
                    container,
                    mediaSourceId,
                    escapedDeviceId);
                contentType = "audio/mp4";
                playMethod = PlayMethod.DirectStream;
            }
            else
            {
                url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/Audio/{1}/stream.mp3?audioCodec=mp3&mediaSourceId={2}&deviceId={3}&playSessionId={4}",
                    serverAddress,
                    itemId,
                    mediaSourceId,
                    escapedDeviceId,
                    Uri.EscapeDataString(playSessionId));
                if (startPositionTicks > 0)
                {
                    url += "&startTimeTicks=" + startPositionTicks.ToString(CultureInfo.InvariantCulture);
                }

                contentType = "audio/mpeg";
                playMethod = PlayMethod.Transcode;
            }
        }

        var imageUrl = BuildImageUrl(item, serverAddress);
        var subtitleTrack = BuildSubtitleTrack(item, mediaSource, serverAddress, subtitleStreamIndex);

        return new BuiltStream(url, contentType, playMethod, imageUrl, subtitleTrack);
    }

    private static bool TryGetDirectPlayVideoContainer(MediaSourceInfo mediaSource, out string container)
    {
        container = string.Empty;

        if (!mediaSource.SupportsDirectPlay && !mediaSource.SupportsDirectStream)
        {
            return false;
        }

        var matchedContainer = GetMatchingContainerToken(mediaSource.Container, DirectPlayVideoContainers);
        if (matchedContainer is null)
        {
            return false;
        }

        var video = mediaSource.VideoStream;
        if (video is null || string.IsNullOrEmpty(video.Codec) || !DirectPlayVideoCodecs.Contains(video.Codec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var audio = mediaSource.GetDefaultAudioStream(mediaSource.DefaultAudioStreamIndex);
        if (audio is not null && !string.IsNullOrEmpty(audio.Codec) && !DirectPlayAudioCodecs.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        container = matchedContainer;
        return true;
    }

    private static bool TryGetDirectPlayAudioContainer(MediaSourceInfo mediaSource, out string container)
    {
        container = string.Empty;

        if (!mediaSource.SupportsDirectPlay && !mediaSource.SupportsDirectStream)
        {
            return false;
        }

        var audio = mediaSource.GetDefaultAudioStream(mediaSource.DefaultAudioStreamIndex);
        if (audio is null || string.IsNullOrEmpty(audio.Codec) || !DirectPlayAudioCodecs.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var matchedContainer = GetMatchingContainerToken(mediaSource.Container, DirectPlayAudioContainers);
        if (matchedContainer is null)
        {
            return false;
        }

        container = matchedContainer;
        return true;
    }

    /// <summary>
    /// Jellyfin often reports <see cref="MediaSourceInfo.Container"/> as ffprobe's raw
    /// comma-separated format name (e.g. "mov,mp4,m4a,3gp,3g2,mj2" for a great many real MP4
    /// files), not a single clean extension. Matching it against an accepted-extensions list
    /// requires checking each comma-separated token rather than the whole string, and the
    /// matched token is also what has to be used for the stream URL's file extension - the raw
    /// multi-value string is not a valid path segment.
    /// </summary>
    private static string? GetMatchingContainerToken(string? rawContainer, string[] acceptedContainers)
    {
        if (string.IsNullOrEmpty(rawContainer))
        {
            return null;
        }

        foreach (var token in rawContainer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = acceptedContainers.FirstOrDefault(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? BuildImageUrl(BaseItem item, string serverAddress)
    {
        if (!item.HasImage(ImageType.Primary, 0))
        {
            return null;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}/Items/{1:N}/Images/Primary", serverAddress, item.Id);
    }

    private static Track? BuildSubtitleTrack(BaseItem item, MediaSourceInfo mediaSource, string serverAddress, int? subtitleStreamIndex)
    {
        if (subtitleStreamIndex is null)
        {
            return null;
        }

        var stream = mediaSource.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == subtitleStreamIndex.Value);
        if (stream is null || !stream.IsTextSubtitleStream)
        {
            // Image-based subtitles (PGS/VobSub) can't be handed to the Chromecast default
            // receiver as a WebVTT sidecar track - they would require burning in via transcode.
            return null;
        }

        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);
        var mediaSourceId = Uri.EscapeDataString(string.IsNullOrEmpty(mediaSource.Id) ? itemId : mediaSource.Id);
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/Videos/{1}/{2}/Subtitles/{3}/Stream.vtt",
            serverAddress,
            itemId,
            mediaSourceId,
            stream.Index);

        return new Track
        {
            TrackId = 1,
            Type = TrackType.TEXT,
            Subtype = TextTrackType.SUBTITLES,
            TrackContentId = url,
            TrackContentType = "text/vtt",
            Language = string.IsNullOrEmpty(stream.Language) ? "und" : stream.Language,
            Name = string.IsNullOrEmpty(stream.DisplayTitle) ? "Subtitle" : stream.DisplayTitle
        };
    }
}
