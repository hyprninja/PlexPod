# Milestone 1: Foundation + Pickers

**Goal:** a WPF app that signs in to Plex, finds the server and music library, loads artists, albums, tracks and playlists, detects the Rockbox iPod, and lets the user build a selection (All songs / Playlists / Artists / Albums) with a **live capacity bar**. **Read-only: nothing is written to the iPod.**

Decisions and invariants are in `../CLAUDE.md`. The selection rules come from `../RockPlex-Selection-and-Playlists.md` §2 and §5.

---

## 1. Solution layout

```
RockPlex.slnx
Directory.Build.props        # net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors, LangVersion latest
global.json                  # SDK 10.0.x, rollForward latestFeature
.editorconfig  .gitignore  README.md
src/
  RockPlex.Core/             # net10.0, NO package refs, no I/O
  RockPlex.Plex/             # net10.0, Microsoft.Extensions.Http
  RockPlex.Device/           # net10.0
  RockPlex.App/              # net10.0-windows, UseWPF, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting
tests/
  RockPlex.Tests/            # net10.0, xUnit; covers Core, Plex, Device
```
Dependencies only point inward: App → Plex, Device → Core.

---

## 2. RockPlex.Core

**Models** (records, immutable):
- `ArtistItem(RatingKey, Guid, Title, ThumbPath?)`
- `AlbumItem(RatingKey, Guid, Title, ArtistKey, ArtistTitle, Year?, TrackCount, ThumbPath?)`
- `TrackItem(RatingKey, Guid, Title, AlbumKey, ArtistKey, TrackArtist?, Index?, DiscIndex?, DurationMs, AudioFormat Format, IReadOnlyList<MediaPartRef> Parts)`
  - `AudioFormat(Container, Codec, BitrateKbps?, BitDepth?, SampleRateHz?, Channels?)`; bit depth and rate are often unknown from list responses.
  - `MediaPartRef(PartId, Key, SizeBytes, Container)`. A track with **no parts** counts as unavailable.
- `PlaylistItem(RatingKey, Guid, Title, IsSmart, LeafCount, DurationMs)` plus `PlaylistContents(PlaylistRatingKey, IReadOnlyList<PlaylistEntry>)`, where `PlaylistEntry(PlaylistItemId, TrackItem? Track, bool Unavailable)`. Order is preserved and duplicates are allowed.
- `LibrarySnapshot(SectionKey, Artists, Albums, Tracks, Playlists)` with lookups by key.
- `ItemRef(RatingKey, Guid, Title)` and `SelectionProfile(SectionKey, AllSongs, Playlists, Artists, Albums)`.

**Logic:**
- `FormatPolicy.Decide(AudioFormat) → FormatDecision { Copy | ConvertFlac(targetBits, targetRate) | UnknownFlac }`
  - Not FLAC → `Copy`, always.
  - FLAC with bits and rate known: `targetRate = min(rate, 44100)`, `targetBits = min(bits, 16)`. Equal to the source → `Copy`, otherwise `ConvertFlac`. **No path ever raises a value.**
  - FLAC with bits or rate unknown → `UnknownFlac`.
- `SizeEstimator.EstimateBytes(TrackItem)`:
  - `Copy` → part size.
  - `ConvertFlac` → `size × (targetBits/srcBits) × (targetRate/srcRate)`.
  - `UnknownFlac` → part size (**conservative**; never smaller than the source).
  - Return whether the estimate is exact or conservative, so the UI can show "≈".
- `SelectionResolver.Resolve(LibrarySnapshot, SelectionProfile, IReadOnlyDictionary<string, PlaylistContents>) → ResolvedSelection`
  - Union of tracks: AllSongs ? every track : playlists ∪ artists (by `ArtistKey`) ∪ albums (by `AlbumKey`). **Deduplicate by RatingKey.**
  - **Playlist completeness:** every available entry of a selected playlist is included. Unavailable entries are reported, never silently dropped.
  - Output: `TrackKeys`, `TotalEstimatedBytes`, `IsEstimateExact`, per-source counts (from playlists / artists / albums / all), `UnavailablePlaylistEntries`, `ConversionCount`, `UnknownFormatCount`.
  - Stale refs (an item no longer in Plex): match by RatingKey first, then by Guid; otherwise report the ref as missing.
- `CapacityCalculator(DeviceCapacity, ResolvedSelection) → { AvailableBytes, SelectedBytes, Fits, OverBy }`. In M1, available = device free space (no manifest yet; M3 adds space taken by RockPlex-managed files).

---

## 3. RockPlex.Plex

- `PlexClientOptions`: Product, Version, ClientIdentifier (stable GUID created on first run and saved in settings), DeviceName.
- `PlexHeadersHandler : DelegatingHandler` adds the `X-Plex-*` headers and `Accept: application/json`, plus `X-Plex-Token` when a token provider supplies one.
- `IPlexAuthService` / `PlexAuthService` (plex.tv):
  - `CreatePinAsync()` → `PlexPin(Id, Code, ExpiresAt)`. `POST https://plex.tv/api/v2/pins?strong=true`.
  - `BuildAuthUrl(pin)` → `https://app.plex.tv/auth#?clientID=…&code=…&context%5Bdevice%5D%5Bproduct%5D=RockPlex`.
  - `PollForTokenAsync(pin, ct)` → token. `GET https://plex.tv/api/v2/pins/{id}?code={code}` every ~1 s until `authToken` is set or the PIN expires (then throw `PinExpiredException`).
  - `ValidateTokenAsync(token)` → `Valid | Invalid | Unreachable`. `GET https://plex.tv/api/v2/user`: 200 / 401 / other.
- `IPlexServerDiscovery`:
  - `GetServersAsync()` from `GET https://plex.tv/api/v2/resources?includeHttps=1` (provides ∋ `server`; keep `accessToken`, `owned`, `connections`).
  - `SelectConnectionAsync(server)` probes candidates in this order: local https uri, local `http://address:port`, remote non-relay. Each probe is `GET {base}/identity` with a 3 s timeout. **Relay is never used.** A manual base URL override wins when it's set and reachable.
- `IPlexLibraryClient` (server base URL + server token):
  - `GetMusicSectionsAsync()` (`type == "artist"`).
  - `GetArtistsAsync / GetAlbumsAsync / GetTracksAsync(sectionKey, IProgress<int>, ct)` from `/library/sections/{id}/all?type=8|9|10`, paged 1,000 per page with container headers until `totalSize`.
  - `GetPlaylistsAsync()` from `/playlists?playlistType=audio`. Filter `playlistType == "audio"` client-side too, in case the parameter is ignored.
  - `GetPlaylistContentsAsync(playlist)` from `{key}` (`/playlists/{id}/items`), paged. Maps `playlistItemID`; an entry with no Media/Part is `Unavailable`.
  - `GetTrackDetailAsync(ratingKey)` from `/library/metadata/{rk}` to fill in `bitDepth`/`samplingRate` from the audio Stream.
- `TrackDetailEnricher`: for **selected** FLAC tracks with unknown depth or rate, fetch details with at most 6 requests at once and cache them in memory keyed by `PartId + updatedAt`. Skip tracks whose `bitrate ≤ 1100 kbps` (a strong signal of 16/44.1). The UI estimate becomes exact as results arrive.
- DTOs stay `internal`. They're mapped to Core models in one `PlexMapper`. Deserialise with `JsonNumberHandling.AllowReadingFromString`, and tolerate missing arrays (`Media`, `Part`, `Metadata`).

---

## 4. RockPlex.Device

- `RockboxDevice(DriveRoot, VolumeLabel, TotalBytes, FreeBytes, RockboxTarget?, RockboxVersion?)`.
- `IRockboxDeviceLocator.FindDevices()` scans `DriveInfo.GetDrives()` (IsReady; skip Network/CDRom) for a root containing `.rockbox`.
- `RockboxInfoParser.Parse(text)` reads lenient `key: value` lines from `/.rockbox/rockbox-info.txt` (Target, Version, Memory, …) ⚠️ check against the real file. A missing file is fine.
- `DeviceWatcher` polls every 2 s and raises `DeviceArrived` / `DeviceRemoved`. (WMI volume events are an M5 option.)
- Also show a warning when Target isn't `ipodvideo` (other Rockbox players are untested).
- **No writes in M1.**

---

## 5. RockPlex.App (WPF)

**Hosting:** `App.xaml.cs` builds a generic host, registers services and view models, and shows `MainWindow`. Unhandled exceptions are logged and shown in a dialog.

**Theme:** merge `pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml` in `App.xaml` (follows system light/dark). Avoid the experimental `ThemeMode` API unless it is no longer experimental in .NET 10.

**Services:**
- `SettingsStore`: JSON at `%LOCALAPPDATA%\RockPlex\settings.json` (ClientIdentifier, ServerMachineId, ServerUrlOverride, SectionKey, SelectionProfile).
- `TokenStore`: DPAPI `ProtectedData.Protect` (CurrentUser) → `token.bin`.
- `BrowserLauncher`: `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`.
- Simple rolling file log in `logs\` (never logs tokens; redact `X-Plex-Token` in URLs).

**MainWindow layout:**
```
┌ Top bar ───────────────────────────────────────────────────────────────┐
│ [Sign in with Plex] / user · Server [▼] · Library [▼] · [⟳ Refresh]      │
│ iPod: E:\ "IPOD" · Rockbox ipodvideo 4.0 · 61.2 GB free of 74.4 GB       │
├ Selection ─────────────────────────────────────────────────────────────┤
│ [x] All songs                                                           │
│ ┌ Playlists | Artists | Albums ───────────────────────────────────────┐ │
│ │ 🔍 filter…                                     [Select all][Clear]   │ │
│ │ [x] Road Trip        smart   142 tracks   4.1 GB                     │ │
│ │ [ ] …                                                                │ │
│ └──────────────────────────────────────────────────────────────────────┘ │
├ Summary ───────────────────────────────────────────────────────────────┤
│ ███████████████████░░░░░  41.2 GB ≈ of 61.2 GB  ·  1,234 tracks          │
│ playlists 410 · artists 600 · albums 224 · 37 to convert · 3 unavailable │
│                                              [Sync] (disabled, M3)       │
└────────────────────────────────────────────────────────────────────────┘
```
- The lists are virtualised `ListView`s with a CheckBox per row and a filter box. Albums show "Artist — Album (Year)". All rows show track count and estimated size.
- **All songs** greys out the Artists and Albums tabs. Playlists stay enabled.
- Ticking anything **recalculates on a background task, debounced ~150 ms**, and updates the bar. Over capacity turns the bar red and shows the overage.
- `≈` appears while any estimate is conservative; the enricher refines it.
- The selection is saved to settings on change and restored on launch (refs re-matched by RatingKey, then Guid; missing refs are listed).
- **Sign-in flow:** button → create PIN → open browser → "Waiting for Plex…" with Cancel → token saved → load servers → auto-pick the saved server or the only one → probe connection → load sections → load library with progress.

**ViewModels** (CommunityToolkit.Mvvm): `MainViewModel`, `SignInViewModel`, `ServerViewModel`, `DeviceViewModel`, `SelectionViewModel` (holding `PickerItemViewModel` collections), `SummaryViewModel`.

---

## 6. Tests (xUnit, `tests/RockPlex.Tests`)
- **FormatPolicy:** 16/44.1 → Copy; 16/48 → 16/44.1; 24/44.1 → 16/44.1; 24/96 → 16/44.1; 16/32 → Copy; 24/32 → 16/32; 8/22.05 → Copy; MP3/AAC/ALAC hi-res → Copy; FLAC unknown → UnknownFlac. Property check: output rate/bits never exceed the input.
- **SizeEstimator:** ratios for each case; unknown → part size; exact flag.
- **SelectionResolver:** union and dedupe (album + its artist counted once); All songs; playlist with duplicates; unavailable entries reported; stale ref matched by Guid; empty selection.
- **CapacityCalculator:** fits / over by N.
- **Plex:** PIN create → poll (null, null, token) → token; expired PIN throws; validate 200/401/500; resources parsing and connection ordering (relay excluded, override wins); paging over 3 pages joins correctly; track mapping (string ratingKey, missing Media → unavailable); playlist items keep order and duplicates; the token is never present in logged URLs.
- **Device:** a temp dir with `.rockbox` is detected, a dir without it isn't; rockbox-info parsing (normal, missing, junk lines).

JSON fixtures live in `tests/RockPlex.Tests/Fixtures/`. Mark them as **representative**, and replace them with captured real responses (token redacted) once a real server is available.

---

## 7. Acceptance criteria
1. Clean build, zero warnings; all tests green.
2. First run: Sign in with Plex in the browser → signed in; after relaunch, still signed in (token from DPAPI); a revoked token → back to signed out cleanly.
3. The server is found and a **local** connection chosen automatically; a manual URL override works.
4. The music library loads (artists, albums, tracks, playlists) with progress, including libraries over 1,000 items (paging).
5. A Rockbox iPod is detected within ~3 s of plug-in and shows drive, label, Rockbox target/version and free/total space; unplugging is detected.
6. Pickers work as specified; the capacity bar updates live; over capacity shows red; hi-res FLAC is estimated as converted; unknown formats are estimated conservatively and refined.
7. The selection persists across restarts.
8. **No files are created or changed on the iPod.**

## 8. Out of scope for M1
Downloading, conversion, tagging, art, playlists on the device, manifest, the sync button's action, stats write-back, tray/auto-sync.
