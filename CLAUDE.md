# PlexPod: Claude Code handoff

PlexPod is a **Windows desktop app (.NET 10, WPF, Fluent theme)** that syncs a **Plex Media Server music library** to an **iPod 5.5G (Video) 80 GB running Rockbox**. Plex is the source of truth. The iPod should show exactly what Plex shows.

**Renamed (18 Oct 2026):** the app was called **RockPlex** until M4. Code, docs, the exe and the GitHub repo (`hyprninja/PlexPod`) use the new name; the local checkout folder may still be called `RockPlex`.

**Status (18 Oct 2026):** M1, M2 and **M3 are done** and verified by the owner on the real library and iPod: a full sync of several playlists (~870 tracks, hi-res conversions, M4A, adopted test-export files), followed by a full bug/security review and fixes (path confinement, manifest reconciliation, token scoping, art and download robustness). **M4 (stats return) is implemented** per `docs/M4-plan.md` (owner decisions D8–D11 in its §9): builds clean, 350 tests pass (after a second full code review on 18 Oct), including end-to-end syncs against a temp-folder iPod and a fake Plex (plays sent before removals, Plex outage resumes, rotated logs, ratings modes, Send plays now). **Not yet checked by the owner:** plays and ratings reaching the real Plex server from the real iPod (acceptance criteria 2–6). The Plex JSON fixtures are still representative, not captured. Next: verify M4, then M5.

## Read these first
| File | What's in it |
|---|---|
| `docs/M4-plan.md` | Milestone 4 (implemented, owner verification pending): stats return, Rockbox playback log + database changelog → Plex plays and ratings, owner decisions D8–D11 |
| `docs/M3-plan.md` | Milestone 3 (done): sync engine, manifest, playlist guarantees, owner decisions D5–D7 |
| `docs/M2-plan.md` | Milestone 2 (done): media pipeline, owner decisions D1–D4 |
| `docs/M1-plan.md` | Milestone 1 (done): foundation + pickers |
| `PlexPod-Research-Brief.md` | Plex API + Rockbox research, prior art, sources |
| `PlexPod-Media-Pipeline.md` | Format rule, tag rewrite, album art (Milestone 2) |
| `PlexPod-Selection-and-Playlists.md` | Selection model, playlist guarantees, sync order (M1 pickers, M3 engine) |

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
- **Read-only against Plex libraries.** The only Plex writes ever allowed are play/rating write-back (M4): `ScrobbleAsync` and `RateAsync`, which are never retried (a retried scrobble would count a play twice).
- **Rockbox's own files are only read** (D9): the playback logs and changelog are never modified, moved or deleted. PlexPod's progress through them lives in `/.plexpod/stats.json`.
- **Never upsample.** Non-FLAC audio is never re-encoded.
- **Playlists are never partially copied and never contain a broken entry.** Selecting a playlist pulls in all its tracks; over capacity → block the sync, never trim.
- **Sync order:** collect stats → copy/verify tracks → write playlists (temp file + rename, then re-read and resolve every line) → delete unreferenced files **last**.
- **Only touch iPod files listed in PlexPod's manifest.** The user's own files and playlists on the iPod are never modified or deleted.
- **Device tags = wipe everything, then write one clean set from Plex.** Rockbox's parser is *first value wins* for text fields, so patching in place is unsafe.
- **The fit check is conservative**: when a format is unknown, assume the larger size; never underestimate.
- **Pre-rename names are read, never written.** iPods and PCs set up as RockPlex hold `/.rockplex` (moved to `/.plexpod` before any device write; reads never move it), `ROCKPLEX_*` ID tags (adopted, and a manifest tag hash made with them counts as current, so tracks get `PLEXPOD_*` tags only when their tags really change), `#ROCKPLEX ratingKey=` playlist headers, `*.rockplex-tmp` leftovers, `%LOCALAPPDATA%\RockPlex\` (moved on first start) and the DPAPI entropy `RockPlex.PlexToken.v1` (re-saved under the new one).
- **Plex tokens are stored only with DPAPI (CurrentUser)**, never in plaintext and never logged. The account token is attached only to `https://*.plex.tv`; servers get only their own token; connection probes are unauthenticated.
- **Everything read from the iPod is untrusted** (manifest, playlists, selection): device paths go through `DevicePaths` (no drive letters, backslashes, `.`/`..`, must resolve inside the drive), and PlexPod only mutates `/Music` and `/Playlists`. The planner reconciles the manifest with the files actually present before acting.
- `PlexPod.Core` has **no package dependencies** and no I/O.
- **Device tags follow `TagPolicy`** (D4): every field can be stripped; only the `PLEXPOD_*` ID tags are always written. Folder names come from Plex values, never from the (possibly stripped) tags.
- **Device writes go through `DeviceFileOps` only**: temp file → flush → unbuffered read-back SHA-256 → rename. Deletes only for paths in the manifest; stale playlists are removed before tracks, and a track any PlexPod playlist on the iPod still names is never deleted.
- **Nothing reaches the UI thread that touches drives or the network synchronously** (a disconnected mapped drive or a waking iPod disk can block for ~20 s).

## Conventions
- Classes are `sealed` by default; records for immutable models; nullable enabled; warnings as errors.
- `async` all the way with `CancellationToken` on every I/O method; no `.Result`/`.Wait()`.
- MVVM via **CommunityToolkit.Mvvm**; DI via **Microsoft.Extensions.Hosting** (generic host in `App.xaml.cs`); one `ServiceCollectionExtensions` per project (`AddPlexPodPlex()`, `AddPlexPodDevice()`, …).
- HTTP via `IHttpClientFactory`; JSON via `System.Text.Json` (Plex sends `ratingKey` as a **string**; allow numbers-as-strings).
- Tests: xUnit; Plex tests use a fake `HttpMessageHandler` + JSON fixtures; device tests use temp directories as fake drives.
- App data: `%LOCALAPPDATA%\PlexPod\` (`settings.json`, `token.bin`, `logs\`, `cache\` = `work\`, `converted\`, `art\`).
- `PlexPod.Media` is `net10.0-windows` (WIC for baseline JPEG art); so is the test project. ffmpeg is found, never bundled (settings path → PATH → winget).
- Edit source files with tools that keep UTF-8 intact. Windows PowerShell 5.1's `Get-Content`/`Set-Content` without `-Encoding` re-encodes non-ASCII text (… — → ✓) and corrupts it.

## Commands
```
dotnet build PlexPod.slnx -c Debug
dotnet test  PlexPod.slnx
dotnet run --project src/PlexPod.App
```

## Milestones
| # | Scope |
|---|---|
| M1 | Foundation + pickers (read-only). ✅ done |
| M2 | ✅ done. Media pipeline: download original part, FLAC conversion (ffmpeg/soxr), tag wipe and rewrite (TagLibSharp), album art (baseline JPEG, embedded + `cover.jpg`) |
| M3 | ✅ done. Sync engine: device manifest, add/re-tag/remove, `.m3u8` playlists with guarantees, interrupt safety, fit-check gate, `/.plexpod/selection.json` on the iPod |
| M4 | ✅ implemented, owner verification pending. Stats return: Rockbox playback log + database changelog → Plex scrobble/rate, step 0 of every sync, Send plays now |
| **M5** | **← next.** Polish (below) |
| M5 | Polish: tray, auto-sync on plug-in, Last.fm forwarding (from `stats\<device>\plays.jsonl`), optional "appears on" artist scope |

## Technical cheat-sheet

### Plex
- Headers on every call: `X-Plex-Product=PlexPod`, `X-Plex-Version`, `X-Plex-Client-Identifier` (stable GUID per install), `X-Plex-Platform=Windows`, `X-Plex-Device-Name`, `X-Plex-Token`, `Accept: application/json` (the default is XML).
- **PIN sign-in ✅:** `POST https://plex.tv/api/v2/pins?strong=true` → `id`, `code`. Open `https://app.plex.tv/auth#?clientID={cid}&code={code}&context%5Bdevice%5D%5Bproduct%5D=PlexPod`. Poll `GET https://plex.tv/api/v2/pins/{id}` about once a second until `authToken` is set or the PIN expires. Validate with `GET https://plex.tv/api/v2/user`: 200 = OK, 401 = discard the token; anything else is a network error, not an invalid token.
- JWT auth (Ed25519 JWK, 7-day tokens, refresh `POST https://clients.plex.tv/api/v2/auth/token`) is **not in M1**. Note: registering a JWK removes the legacy token for that client ID.
- **Servers ⚠️:** `GET https://plex.tv/api/v2/resources?includeHttps=1` → entries whose `provides` contains `server`, with `accessToken` and `connections[] {uri, address, port, protocol, local, relay}`. Probe local connections first (`GET {uri}/identity`, short timeout), then also try `http://{address}:{port}` (plex.direct DNS can be blocked by routers); **skip relay** (bandwidth-limited); support a manual server URL override.
- Music sections: `GET /library/sections` → `type="artist"`.
- Lists: `GET /library/sections/{id}/all?type=8|9|10` (artist/album/track ⚠️ type codes). Page with the `X-Plex-Container-Start` / `X-Plex-Container-Size` headers; use `MediaContainer.totalSize`.
- Track JSON: `ratingKey` (string), `guid`, `title`, `parentRatingKey` (album), `grandparentRatingKey` (album artist), `originalTitle` (track artist ✅), `index`, `parentIndex`, `duration` ms, `updatedAt`, `Media[] {bitrate, audioCodec, container, Part[] {id, key, file, size, container}}`.
- **Stream details** (`bitDepth`, `samplingRate`, `gain`, `albumGain`, `peak`) are in `GET /library/metadata/{ratingKey}` and probably **not** in list responses ⚠️. Estimate conservatively and fetch details on demand.
- Playlists: `GET /playlists?playlistType=audio` ⚠️ → `ratingKey`, `key`, `title`, `smart`, `leafCount`, `duration`. Items: `GET {key}` (`/playlists/{id}/items`) ✅, paged. The same call is used for smart playlists ⚠️🔬. Each entry has a `playlistItemID`; **duplicates are legal; keep them and their order**.
- Download original (M2): `{server}{Part.key}?download=1`. Art: the album `thumb`, or `/photo/:/transcode?width=&height=&url=`.
- Write-back (M4) ✅ (python-plexapi): `GET /:/scrobble?key={rk}&identifier=com.plexapp.plugins.library`, `PUT /:/rate?key={rk}&identifier=com.plexapp.plugins.library&rating={0-10}` (`-1` clears). Plex can't back-date a play. 🔬 not yet tried against the real server.

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
