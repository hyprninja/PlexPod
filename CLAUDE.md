# RockPlex: Claude Code handoff

RockPlex is a **Windows desktop app (.NET 10, WPF, Fluent theme)** that syncs a **Plex Media Server music library** to an **iPod 5.5G (Video) 80 GB running Rockbox**. Plex is the source of truth. The iPod should show exactly what Plex shows.

**Status (18 Oct 2026):** **Milestone 1 is implemented** (`docs/M1-plan.md`): it builds clean and all 88 tests pass. Not yet checked against a real Plex server: sign-in, discovery, library load, enrichment. The JSON fixtures are representative, not captured. Verified by the owner against the real server (sign-in, discovery, playlist sizing). **Next: Milestone 2**, see `docs/M2-plan.md` (owner decisions D1–D3 are in its §8).

## Read these first
| File | What's in it |
|---|---|
| `docs/M2-plan.md` | **What to build now.** Media pipeline: projects, classes, tests, acceptance criteria, owner decisions D1–D3 |
| `docs/M1-plan.md` | Milestone 1 (done): foundation + pickers |
| `RockPlex-Research-Brief.md` | Plex API + Rockbox research, prior art, sources |
| `RockPlex-Media-Pipeline.md` | Format rule, tag rewrite, album art (Milestone 2) |
| `RockPlex-Selection-and-Playlists.md` | Selection model, playlist guarantees, sync order (M1 pickers, M3 engine) |

Status markers in those docs: ✅ verified from a primary source · ⚠️ inferred, verify before relying on it · 🔬 must be tested against the real server or iPod.

## Owner decisions (don't revisit without asking)
1. **Stack:** .NET 10, **WPF**, **Fluent** theme (follows system light/dark). Windows-only app.
2. **Target device:** iPod 5.5G **80 GB** (64 MB RAM), stock storage, Rockbox, FAT32.
3. **Plex is the source of truth** for metadata and album art. The file tags in the library are messy because Plex overlays its own data.
4. **Tags are rewritten on the iPod copies only**, purely so the iPod shows what Plex shows. **Never modify source files on the server.**
5. **Format rule:** FLAC above 16-bit/44.1 kHz is automatically converted **down** to 16/44.1 (output rate = min(src, 44100), output bits = min(src, 16)). **Never upsample anything, ever.** MP3, AAC and every other format are copied with the audio untouched (their tags and art are still rewritten from Plex).
6. **Playlists syncing is mission-critical.**
7. **Selection UI:** "All songs" toggle + multi-select **Playlists**, **Artists**, **Albums**.
8. **Milestone 1 = foundation + pickers** (Plex sign-in, discovery, library fetch, iPod detection, pickers with live capacity bar). **Nothing is written to the iPod in M1.**

## Invariants (hard rules for all code)
- **Read-only against Plex libraries.** The only Plex writes ever allowed are play/rating write-back (M4).
- **Never upsample.** Non-FLAC audio is never re-encoded.
- **Playlists are never partially copied and never contain a broken entry.** Selecting a playlist pulls in all its tracks; over capacity → block the sync, never trim.
- **Sync order:** collect stats → copy/verify tracks → write playlists (temp file + rename, then re-read and resolve every line) → delete unreferenced files **last**.
- **Only touch iPod files listed in RockPlex's manifest.** The user's own files and playlists on the iPod are never modified or deleted.
- **Device tags = wipe everything, then write one clean set from Plex.** Rockbox's parser is *first value wins* for text fields, so patching in place is unsafe.
- **The fit check is conservative**: when a format is unknown, assume the larger size; never underestimate.
- **Plex tokens are stored only with DPAPI (CurrentUser)**, never in plaintext and never logged.
- `RockPlex.Core` has **no package dependencies** and no I/O.

## Conventions
- Classes are `sealed` by default; records for immutable models; nullable enabled; warnings as errors.
- `async` all the way with `CancellationToken` on every I/O method; no `.Result`/`.Wait()`.
- MVVM via **CommunityToolkit.Mvvm**; DI via **Microsoft.Extensions.Hosting** (generic host in `App.xaml.cs`); one `ServiceCollectionExtensions` per project (`AddRockPlexPlex()`, `AddRockPlexDevice()`, …).
- HTTP via `IHttpClientFactory`; JSON via `System.Text.Json` (Plex sends `ratingKey` as a **string**; allow numbers-as-strings).
- Tests: xUnit; Plex tests use a fake `HttpMessageHandler` + JSON fixtures; device tests use temp directories as fake drives.
- App data: `%LOCALAPPDATA%\RockPlex\` (`settings.json`, `token.bin`, `logs\`, later `cache\`).

## Commands
```
dotnet build RockPlex.slnx -c Debug
dotnet test  RockPlex.slnx
dotnet run --project src/RockPlex.App
```

## Milestones
| # | Scope |
|---|---|
| M1 | Foundation + pickers (read-only). ✅ done |
| **M2** | **← next.** Media pipeline: download original part, FLAC conversion (ffmpeg/soxr), tag wipe and rewrite (TagLibSharp), album art (baseline JPEG, embedded + `cover.jpg`) |
| M3 | Sync engine: device manifest, add/re-tag/remove, `.m3u8` playlists with guarantees, interrupt safety, fit-check gate, `/.rockplex/selection.json` on the iPod |
| M4 | Stats return: Rockbox playback/scrobbler log + database changelog → Plex scrobble/rate |
| M5 | Polish: tray, auto-sync on plug-in, Last.fm forwarding, optional "appears on" artist scope |

## Technical cheat-sheet

### Plex
- Headers on every call: `X-Plex-Product=RockPlex`, `X-Plex-Version`, `X-Plex-Client-Identifier` (stable GUID per install), `X-Plex-Platform=Windows`, `X-Plex-Device-Name`, `X-Plex-Token`, `Accept: application/json` (the default is XML).
- **PIN sign-in ✅:** `POST https://plex.tv/api/v2/pins?strong=true` → `id`, `code`. Open `https://app.plex.tv/auth#?clientID={cid}&code={code}&context%5Bdevice%5D%5Bproduct%5D=RockPlex`. Poll `GET https://plex.tv/api/v2/pins/{id}` about once a second until `authToken` is set or the PIN expires. Validate with `GET https://plex.tv/api/v2/user`: 200 = OK, 401 = discard the token; anything else is a network error, not an invalid token.
- JWT auth (Ed25519 JWK, 7-day tokens, refresh `POST https://clients.plex.tv/api/v2/auth/token`) is **not in M1**. Note: registering a JWK removes the legacy token for that client ID.
- **Servers ⚠️:** `GET https://plex.tv/api/v2/resources?includeHttps=1` → entries whose `provides` contains `server`, with `accessToken` and `connections[] {uri, address, port, protocol, local, relay}`. Probe local connections first (`GET {uri}/identity`, short timeout), then also try `http://{address}:{port}` (plex.direct DNS can be blocked by routers); **skip relay** (bandwidth-limited); support a manual server URL override.
- Music sections: `GET /library/sections` → `type="artist"`.
- Lists: `GET /library/sections/{id}/all?type=8|9|10` (artist/album/track ⚠️ type codes). Page with the `X-Plex-Container-Start` / `X-Plex-Container-Size` headers; use `MediaContainer.totalSize`.
- Track JSON: `ratingKey` (string), `guid`, `title`, `parentRatingKey` (album), `grandparentRatingKey` (album artist), `originalTitle` (track artist ✅), `index`, `parentIndex`, `duration` ms, `updatedAt`, `Media[] {bitrate, audioCodec, container, Part[] {id, key, file, size, container}}`.
- **Stream details** (`bitDepth`, `samplingRate`, `gain`, `albumGain`, `peak`) are in `GET /library/metadata/{ratingKey}` and probably **not** in list responses ⚠️. Estimate conservatively and fetch details on demand.
- Playlists: `GET /playlists?playlistType=audio` ⚠️ → `ratingKey`, `key`, `title`, `smart`, `leafCount`, `duration`. Items: `GET {key}` (`/playlists/{id}/items`) ✅, paged. The same call is used for smart playlists ⚠️🔬. Each entry has a `playlistItemID`; **duplicates are legal; keep them and their order**.
- Download original (M2): `{server}{Part.key}?download=1`. Art: the album `thumb`, or `/photo/:/transcode?width=&height=&url=`.
- Write-back (M4) ⚠️: `/:/scrobble?key={rk}&identifier=com.plexapp.plugins.library`, `/:/rate?key={rk}&identifier=com.plexapp.plugins.library&rating={0-10}`.

### Rockbox on the iPod Video
- Detection: a ready drive whose root contains **`/.rockbox`**. `/.rockbox/rockbox-info.txt` holds `key: value` lines (Target, Version, …) ⚠️; parse leniently.
- Hardware: PP5021C, 64 MB RAM (80 GB model), **output sample rates 8–48 kHz only** ✅ (no 88.2/96), LBA48, real-time clock present.
- Playlists ✅: `/Playlists/*.m3u8`, UTF-8, absolute `/Music/...` paths; `#` lines ignored; backslashes converted. Treat paths as **case-sensitive** ⚠️.
- Tag parser ✅: keys are case-insensitive; **text fields first-wins, numeric fields last-wins**; reads `TITLE, ARTIST, ALBUM, ALBUMARTIST|ALBUM ARTIST|ENSEMBLE, TRACKNUMBER, DISCNUMBER|DISC, DATE, GENRE (single), COMPOSER, COMMENT, GROUPING, MUSICBRAINZ_TRACKID, REPLAYGAIN_*`.
- Art ✅: **baseline JPEG** or BMP only (no progressive). Embedded art works for FLAC (first PICTURE block; keep it small), ID3 APIC, MP4 `covr` and Ogg. PictureFlow needs `cover.jpg` in the folder.
- Database: built on the device. *Update Now* keeps play stats; *Initialize* wipes them.
- Stats ✅: rating is 0–10 (same scale as Plex `userRating`); `lastplayed` is a serial counter, not a timestamp. Real timestamps come from the playback log / `/.scrobbler.log`.

### Capacity
About **73 GiB usable**. 16/44.1 FLAC ≈ 270–380 MB per album, so roughly **210–290 albums**. Estimated size after converting a hi-res FLAC ≈ `size × (16/bits) × (44100/rate)`.

## Open / unverified (check when you reach them)
- Plex: resources/playlist query params, list-vs-detail stream fields, whether smart-playlist items come back evaluated, scrobble/rate paths.
- Rockbox 🔬: Max Playlist Size default, the `database_changelog.txt` line format, the charge-only USB button, the embedded-art size limit, real UTF-8 path behaviour.
- The test lists at the end of the pipeline and selection docs.
