# Jellycast

A Jellyfin server plugin that adds native Google Cast (Chromecast) support to Jellyfin's
**existing** cast interface — no separate app, button or UI to learn.

Chromecast devices on your local network are discovered automatically and appear as regular
targets in jellyfin-web's cast menu (the cast icon next to your avatar, and on every item's
play screen), alongside any other Jellyfin client sessions. Once you start casting, playback is
controlled the same way you already control any other Jellyfin session — play/pause/seek/stop,
volume, and the "Now Playing" remote control bar all just work.

## How it works

Jellyfin already has a generic mechanism for turning a non-Jellyfin device into a controllable
session: this is exactly how the built-in DLNA "Play To" feature works. This plugin follows the
same pattern for Chromecast instead of DLNA:

1. A background service scans the local network for Chromecast devices via mDNS
   (`_googlecast._tcp`), using [SharpCaster](https://github.com/Tapanila/SharpCaster) for
   discovery and the CastV2 protocol.
2. Each discovered device is registered as a Jellyfin `SessionInfo`, which is what makes it show
   up in the cast menu and the session/"Play On" list.
3. When you cast to it, Jellyfin sends the same Play/Playstate/GeneralCommand messages it would
   send to any other client session. This plugin's session controller translates those into
   CastV2 calls (`LOAD`, `PLAY`, `PAUSE`, `SEEK`, `STOP`, volume) against the device.
4. The Chromecast plays the media using Google's public "Default Media Receiver" app and reports
   status back; the plugin relays that back into Jellyfin's normal playback-progress reporting,
   which is what powers the remote-control UI and continues-watching tracking.

No changes to jellyfin-web are needed — everything happens server-side through interfaces the
web UI already knows how to talk to.

> [!IMPORTANT]
> **This build must match your Jellyfin server's exact version (down to the patch number)**,
> not just the major.minor line. Confirmed by testing against two real servers on the same
> 10.11 line (10.11.6 and 10.11.11): a mismatch doesn't degrade gracefully, it fails to load
> with `Could not load file or assembly 'MediaBrowser.Controller, Version=X.X.X.0'... The system
> cannot find the file specified.` in the server log. See [Installing](#installing) for how to
> rebuild against your own version with a single build flag.

## Installing

### Option A — add this repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → Add Repository**.
2. Repository URL:
   `https://raw.githubusercontent.com/DD00031/jellycast/main/manifest.json`
3. Go to **Catalog**, find **Jellycast**, install it, and restart Jellyfin.

This only works once a tagged release has been published (see [Releasing](#releasing) below) —
until then the catalog entry has no installable version. It also only works if a release happens
to have been built for your server's exact version — see the note below.

### Option B — build and install manually

```bash
git clone https://github.com/DD00031/jellycast.git
cd jellycast
dotnet publish Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj -c Release -o publish
```

> Building requires **.NET SDK 9.0+** — Jellyfin server 10.10+ (including the current
> `jellyfin/jellyfin:latest` Docker image) runs on .NET 9, so this plugin targets net9.0 to match.
> Verified working end-to-end with `mcr.microsoft.com/dotnet/sdk:9.0` against real Jellyfin
> servers (both Docker and a native install).

Copy everything under `publish/` into a new folder named `Jellycast` inside your Jellyfin
`plugins` directory (e.g. `/var/lib/jellyfin/plugins/Jellycast` on Linux, or
`%ProgramData%\Jellyfin\Server\plugins\Jellycast` on Windows), then restart Jellyfin.

> **Building for your own server version.** This repository targets Jellyfin **10.11.11** by
> default, but that has to match your server's exact patch version, not just the major.minor
> line — confirmed by testing against two real 10.11.x servers side by side. Jellyfin's own
> `MediaBrowser.Controller`/`Model` assemblies get a new, incompatible identity on every patch
> release, and the plugin's compiled references bake in the exact version they were built
> against with no fallback. A mismatch fails outright, visible in the server log as either
> `Could not load file or assembly 'MediaBrowser.Controller, Version=X.X.X.0, ...'. The system
> cannot find the file specified.` or a `TypeLoadException` naming a method as "does not have an
> implementation". There's no way to make a single compiled build tolerant of this - the version
> is baked into the DLL's own metadata at compile time. Check your server's exact version under
> **Dashboard → About**, then rebuild against it directly, no file editing needed:
> ```bash
> dotnet build -p:JellyfinServerVersion=10.11.6 Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj
> ```
> (swap in your own version, and use `dotnet publish` the same way for the actual install
> artifact). `build.yaml`'s `targetAbi` is left as documentation and isn't read by this repo's
> own release process, so there's no need to touch it for a one-off local build.

## Configuration

**Dashboard → Plugins → Jellycast** lets you tune:

- **Discovery interval** — how often the local network is scanned for devices.
- **Device timeout** — how long a device may go unseen before it's dropped from the cast menu.
- **Device name prefix** — an optional label prefix so Chromecasts are easy to spot in the menu.
- **Prefer direct play** — stream already-compatible sources (H.264/AAC in MP4) as-is instead of
  always transcoding.
- **Verbose logging** — for troubleshooting.

## Known limitations

- **Playback UI on the TV** is Google's generic "Default Media Receiver" skin, not a
  Jellyfin-branded one. A custom-branded receiver requires registering a Cast Application ID
  with Google and publishing a hosted receiver app, which is outside the scope of what a
  self-hosted plugin can do.
- **Codec support** follows what the Default Media Receiver plays natively: H.264 video with
  AAC/MP3 audio direct-plays (verified end-to-end, including seeking via normal HTTP range
  requests); anything else (HEVC, AV1, VP9, DTS, TrueHD, etc.) is transcoded to H.264/AAC via
  Jellyfin's HLS endpoint, which gives an accurate duration up front and supports real seeking
  by requesting different segments (also verified end-to-end).
- **On-device seek display for transcoded content**: seeking on a source that needs transcoding
  restarts the transcode at the new offset (a live single-pass encode has no other way to seek).
  Jellyfin's own UI shows the correct absolute position immediately, but the Chromecast's own
  on-screen overlay briefly shows the position of the new segment rather than the original
  absolute time. Direct-played sources don't have this limitation.
- **Subtitles** are supported for text-based formats (SRT/ASS/SSA/VTT) via a WebVTT sidecar
  track. Image-based subtitles (PGS/VobSub) aren't supported, since the Default Media Receiver
  can't render them without a burned-in transcode.
- **Queues**: casting a single item, or "play all" for a list of items, both work — the plugin
  loads the first item and automatically advances to the next one in the list when the current
  one finishes. Client-driven reordering of an in-progress Chromecast queue (`PlayNext`/
  `PlayLast` while already casting) is not implemented.
- **Network**: the Chromecast must be able to reach your Jellyfin server directly over your LAN
  (same requirement as casting from Chrome/the official apps). Remote/relay access is not
  supported. A full-tunnel VPN on the server (e.g. one that captures the mDNS multicast route)
  will also prevent discovery from finding devices - this isn't fixable from the plugin side.

## Releasing

Pushing a tag like `v0.1.0.0` (a valid 2-4 part version number) runs
[`.github/workflows/release.yml`](.github/workflows/release.yml), which builds the plugin,
publishes a GitHub release with the packaged zip attached, and commits an updated
`manifest.json` pointing at it — so anyone with this repository added in Jellyfin picks up the
new version, *provided their server matches the exact Jellyfin version this release was built
against* (currently 10.11.11 - see the note under Installing). This repo only publishes one
build per release right now; it doesn't do a build matrix across multiple Jellyfin versions.

## Project layout

- [`Jellyfin.Plugin.Chromecast/`](Jellyfin.Plugin.Chromecast) — the plugin itself.
  - `Plugin.cs`, `PluginServiceRegistrator.cs` — plugin entry points.
  - `Discovery/` — mDNS discovery and session registration (the Chromecast equivalent of
    Jellyfin's DLNA `PlayToManager`).
  - `Session/` — `ChromecastSessionController` (the `ISessionController` implementation that
    receives Play/Playstate/GeneralCommand messages) and the stream URL builder.
  - `Configuration/` — plugin settings and dashboard config page.
- [`manifest.json`](manifest.json) — the installable-repository manifest for
  Dashboard → Plugins → Repositories.
- [`SharpCaster/`](SharpCaster) — vendored copy of the SharpCaster CastV2 client library the
  plugin depends on.
