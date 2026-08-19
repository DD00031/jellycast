# Jellyfin Chromecast Plugin

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
> rebuild against your own version.

## Installing

### Option A — add this repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → Add Repository**.
2. Repository URL:
   `https://raw.githubusercontent.com/DD00031/jellycast/main/manifest.json`
3. Go to **Catalog**, find **Chromecast**, install it, and restart Jellyfin.

This only works once a tagged release has been published (see [Releasing](#releasing) below) —
until then the catalog entry has no installable version.

### Option B — build and install manually

```bash
git clone https://github.com/DD00031/jellycast.git
cd jellycast
dotnet publish Jellyfin.Plugin.Chromecast/Jellyfin.Plugin.Chromecast.csproj -c Release -o publish
```

> Building requires **.NET SDK 9.0+** — Jellyfin server 10.10+ (including the current
> `jellyfin/jellyfin:latest` Docker image) runs on .NET 9, so this plugin targets net9.0 to match.
> Verified working end-to-end with `mcr.microsoft.com/dotnet/sdk:9.0` against a real
> `jellyfin/jellyfin:latest` (10.11.11) container.

Copy everything under `publish/` into a new folder named `Chromecast` inside your Jellyfin
`plugins` directory (e.g. `/var/lib/jellyfin/plugins/Chromecast` on Linux, or
`%ProgramData%\Jellyfin\Server\plugins\Chromecast` on Windows), then restart Jellyfin.

> The plugin as committed targets Jellyfin server **10.11.11** exactly
> (`Jellyfin.Controller`/`Jellyfin.Model` 10.11.11, `targetAbi` 10.11.0.0) — pick this as a
> reasonable default for new installs on the current latest release, but treat it as a starting
> point, not a given.
>
> Confirmed by testing against two real 10.11.x servers that this needs to match your server's
> **exact patch version**, not just the major.minor line: Jellyfin's own `MediaBrowser.Controller`
> etc. assemblies are stamped with the full release version (e.g. `10.11.6.0`, not `10.11.0.0`),
> and the plugin's compiled references request that exact version by strong name with no
> fallback/redirect. A mismatch fails outright — the server log shows
> `Could not load file or assembly 'MediaBrowser.Controller, Version=10.11.11.0, ...'. The
> system cannot find the file specified.` (if the two versions are close enough that the classes
> haven't changed shape) or a `TypeLoadException` naming a method with "does not have an
> implementation" (if an interface itself changed between versions). Either way, the fix is the
> same: check your server's exact version under **Dashboard → About**, set that same version for
> both `PackageReference`s in `Jellyfin.Plugin.Chromecast.csproj` (and `targetAbi` in
> `build.yaml`, using the `major.minor.0.0` form Jellyfin's own manifests use there), and rebuild.

## Configuration

**Dashboard → Plugins → Chromecast** lets you tune:

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
  AAC/MP3 audio direct-plays; anything else (HEVC, AV1, VP9, DTS, TrueHD, etc.) is transcoded to
  H.264/AAC MP4 by Jellyfin's normal transcoding pipeline.
- **Subtitles** are supported for text-based formats (SRT/ASS/SSA/VTT) via a WebVTT sidecar
  track. Image-based subtitles (PGS/VobSub) aren't supported, since the Default Media Receiver
  can't render them without a burned-in transcode.
- **Queues**: casting a single item, or "play all" for a list of items, both work — the plugin
  loads the first item and automatically advances to the next one in the list when the current
  one finishes. Client-driven reordering of an in-progress Chromecast queue (`PlayNext`/
  `PlayLast` while already casting) is not implemented.
- **Network**: the Chromecast must be able to reach your Jellyfin server directly over your LAN
  (same requirement as casting from Chrome/the official apps). Remote/relay access is not
  supported.

## Releasing

Pushing a tag like `v1.0.0.0` (a valid 2-4 part version number) runs
[`.github/workflows/release.yml`](.github/workflows/release.yml), which builds the plugin,
publishes a GitHub release with the packaged zip attached, and commits an updated
`manifest.json` pointing at it — so anyone with this repository added in Jellyfin picks up the
new version automatically.

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
