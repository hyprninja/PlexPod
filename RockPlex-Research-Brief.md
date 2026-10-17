# RockPlex: Research Brief

*Plex APIs and Rockbox on the iPod 5G/5.5G (Video). Researched 18 Oct 2026.*

Status tags: ✅ confirmed from a primary source · ⚠️ inferred or secondary source, check before relying on it · 🔬 needs a test on the real iPod or server

---

## TL;DR

- **Plex side:** Plex now publishes official PMS API docs at developer.plex.tv. They cover versioned endpoints, JSON or XML responses, pagination, and the new JWT auth. Everything a sync engine needs is plain HTTP on the LAN: list tracks and playlists, download the original file, fetch artwork, and write plays and ratings back.
- **Device side:** The current stable release is Rockbox 4.0 (April 2025), and the iPod Video is a mature target. It needs a FAT32 ("Windows-formatted") iPod. Rockbox reads **embedded tags only**, builds its own database **on the device**, plays FLAC/MP3/AAC/ALAC/Ogg/Opus and more, and reads `.m3u8` playlists.
- **Stats can sync both ways.** Rockbox keeps a timestamped playback log, and its rating scale (0–10) is the same as Plex's `userRating` scale. None of the existing tools write plays or ratings back to Plex. That is RockPlex's gap to fill.
- **Biggest design risk:** what Plex *shows* can differ from what is *in the file's tags* (agent-matched metadata, edits made in Plex). Rockbox sees only the file tags. Decide early whether RockPlex rewrites tags on the device copy.

---

## 1. Plex Media Server API

### 1.1 Documentation and status
- ✅ Official public docs for the PMS API: **developer.plex.tv/pms**. They were announced at Plex Pro Week '25 ("API Unlocked") and are discussed on the Plex forum's Dev/API Corner.
- ✅ Versioning uses the `X-Plex-Pms-Api-Version` header. Documented versions run 0.0 (default) → 1.0.0 (PMS ≥ 1.41.9) → … → **1.2.2 (PMS ≥ 1.43.2)**.
- ✅ The `Accept` header picks the response format: XML (default) or `application/json`.
- ✅ Pagination: `X-Plex-Container-Start` / `X-Plex-Container-Size` headers, or `limit`. Responses can be trimmed with `includeFields` / `excludeFields` / `includeElements` / `excludeElements`, which matters when pulling tens of thousands of tracks.
- ⚠️ **Plex Pass:** the blog post says "API Unlocked is a premium feature and requires a Plex Pass subscription", but also that the docs are "a starting point… that all are welcome to use." The owner's token has always worked against the owner's own server on the LAN. The 2025 remote-streaming paywall covers **video only**; music and Plexamp are not affected. Low risk for a LAN-only engine. 🔬 Confirm with the server.

### 1.2 Authentication
Required headers on every call: `X-Plex-Client-Identifier` (a stable UUID per RockPlex install), `X-Plex-Product`, `X-Plex-Version`, and `X-Plex-Token`. These can also go in the query string.

**Option A: PIN flow (the classic "Sign in with Plex"; best for a desktop app)** ✅
1. `POST https://plex.tv/api/v2/pins?strong=true` with the headers above. Returns `id` and `code`.
2. Open `https://app.plex.tv/auth#?clientID=<id>&code=<code>&context[device][product]=RockPlex`. Everything after `#` goes in the fragment and must be URL-encoded.
3. Poll `GET https://plex.tv/api/v2/pins/{id}` about once a second until `authToken` is filled in.
4. Validate later with `GET https://plex.tv/api/v2/user`. 200 means valid. 401 means discard the token and sign in again. Any other result is a network error, not an invalid token.

**Option B: JWT (new, as of 2025)** ✅
- The device generates an **Ed25519** key pair and registers the public JWK. Strong-PIN variant: `POST https://clients.plex.tv/api/v2/pins` with the JWK and `strong:true`, then exchange with `GET …/pins/{id}?deviceJWT=<signed>`. The signed JWT carries `aud: "plex.tv"`, `iss: <clientIdentifier>`, and `kid`/`alg` in the header.
- The resulting JWT goes in the same `X-Plex-Token` header. It is **valid for 7 days** and is refreshed at any time, even after expiry, with `POST https://clients.plex.tv/api/v2/auth/token`. Other endpoints: `/api/v2/auth/jwk`, `/api/v2/auth/nonce`, `/api/v2/auth/keys`.
- ⚠️ Registering a JWK **removes the legacy token** for that client identifier. Plex hasn't given a date for retiring legacy tokens. Recommendation: build the PIN flow now and design the token store so JWT refresh can be added without changing the flow.

**Server discovery:** `GET https://plex.tv/api/v2/resources` lists the user's servers and their connection URIs; prefer the `local` LAN one. ⚠️ The endpoint is well known, but I didn't re-check it against the new docs.

### 1.3 Endpoints a sync engine needs

| Purpose | Endpoint | Status |
|---|---|---|
| List libraries (find the music section) | `GET /library/sections` | ✅ |
| Section details with filters and sorts in one call | `GET /library/sections/{id}?includeDetails=1` | ✅ |
| All tracks in a section | `GET /library/sections/{id}/all?type=10` (8 = artist, 9 = album, 10 = track) | ✅ path / ⚠️ type codes |
| Item metadata | `GET /library/metadata/{ratingKey}`, plus `/children` and `/grandchildren` | ✅ |
| Playlists | `GET /playlists?playlistType=audio`, `GET /playlists/{id}/items` (smart playlists return their computed items) | ⚠️ well known; not seen in the new docs excerpt |
| **Download original file** | `GET /library/parts/{partId}/{changestamp}/file.{ext}?download=1`. The part `key` comes from `Media[].Part[].key` in the metadata. This is what python-plexapi `download()` does. | ✅ mechanism / ⚠️ exact form |
| Resized artwork | `/photo/:/transcode?width=&height=&url=<thumb>` (added in API 1.1.0), or the raw `thumb` URL | ✅ |
| Music transcode | `/music/:/transcode/universal/start.m3u8` + `X-Plex-Client-Profile-Extra` | ⚠️ barely documented and fragile (the forum thread returned 400 errors until a client profile was sent). **Avoid; transcode locally with ffmpeg.** |
| Mark played | `/:/scrobble?key={ratingKey}&identifier=com.plexapp.plugins.library` (python-plexapi `markPlayed()`) | ⚠️ |
| Rate | `/:/rate?key={ratingKey}&identifier=com.plexapp.plugins.library&rating={0-10}` (python-plexapi `rate()`, 0–10 scale, 7.0 = 3.5★) | ✅ scale / ⚠️ path |
| Library scan | `GET /library/sections/{id}/refresh` | ✅ |

**Useful track fields:** `ratingKey`, `guid` (e.g. `plex://track/…`), `title`, `index` (track number), `parentIndex` (disc), `parentTitle` (album), `grandparentTitle` (album artist), `originalTitle` (track artist, when it differs ⚠️), `duration` (ms), `addedAt`, `updatedAt`, `userRating`, `viewCount`, `skipCount`, `lastViewedAt`, `Media[].Part[]` (`id`, `key`, `file`, `size`, `container`), and `thumb`/`parentThumb`.

### 1.4 Change detection
- **Webhooks:** ✅ need Plex Pass on the owner account. They send a multipart POST with a JSON payload. Events: `library.new`, `media.play/pause/resume/stop`, `media.rate`, `media.scrobble` (past the 90% mark), and others. Plex's own example payload is a music track, so playback events do fire for music.
- **For RockPlex, polling is simpler and enough:** sync runs when the iPod is plugged in. Diff on `updatedAt`/`addedAt` with slim `includeFields` queries, then fetch full metadata only for changed items.

### 1.5 Client libraries
| Library | Lang | Notes |
|---|---|---|
| **python-plexapi** | Python ≥ 3.10 | ✅ v4.18.3 (28 Sep 2026), BSD-3. The most mature option: `Track.download()`, `getStreamURL()`, `markPlayed()`, `rate()`, playlists, sonic analysis. |
| **LukeHagar.PlexAPI.SDK** | .NET 8+ | ✅ v0.22.1 (updated 17 Oct 2026). Generated from a community OpenAPI spec for PMS and plex.tv; includes OAuth PIN, transcode, webhooks. Pre-1.0, so expect churn. |
| Raw HTTP | any | The API is simple enough that a thin `HttpClient` wrapper with your own DTOs is reasonable, and it avoids depending on a pre-1.0 generated SDK. |

---

## 2. Rockbox on the iPod 5G / 5.5G (iPod Video)

### 2.1 Release and target status
- ✅ **Latest stable: Rockbox 4.0, released 1–2 Apr 2025.** Toolchain moved to GCC 4.9.4; release supports 83 targets. Daily builds track git.
- ✅ The iPod Video is a long-standing stable target. Hardware config (`ipodvideo.h`): PortalPlayer PP5022-class CPU (the actual chip is the PP5021C), adjustable clock, 320×240 16-bit LCD, ATA with **LBA48**, WM8758 codec, PCF50605 **RTC** (so timestamps are real), and the Rockbox USB stack enabled (`HAVE_USBSTACK`, `USE_ROCKBOX_USB`).
- ✅ **RAM differs by model:** 30 GB (5G and 5.5G) = **32 MB**; 60 GB / 80 GB = **64 MB**. One build covers both. RAM caps the audio buffer and whether the database fits in "Load to RAM" with a big library.
- ⚠️ Fork worth knowing: **rockpod** (v2.0), a Rockbox fork for the Classic and Video. It adds a Cover Flow rewrite, colours taken from album art, and a cleanup of `01 ` filename prefixes. It's a drop-in replacement.

### 2.2 Installation (Windows)
- ✅ The iPod **must be FAT32** (initialised by iTunes for Windows). Mac-formatted (HFS+) iPods are unsupported and need converting first.
- Rockbox Utility (RBUtil) installs the bootloader and the build. The manual route is `ipodpatcher` plus unzipping the build so `/.rockbox` lands at the drive root. Uninstall with `ipodpatcher` (`u`).
- Dual boot: the Rockbox bootloader can still boot the Apple firmware.
- ⚠️ An older manual page said the 80 GB model was unsupported. That is long out of date; 80 GB units are supported now. 🔬 Check on the actual unit.

### 2.3 USB behaviour
- ✅ The config enables Rockbox's own USB mass-storage stack, so when plugged in the iPod appears as a normal removable drive and a drive letter shows up on Windows.
- ⚠️ Holding a button while plugging in is believed to give charge-only mode, and the exact button varies by version. 🔬 Test. **Detection rule for RockPlex: a drive letter with a `/.rockbox` folder at its root = Rockbox device.** Existing tools (RockSync, plex-ipod-sync) use the same check.

### 2.4 Audio formats
✅ MP3, AAC-LC/HE/HEv2 (non-DRM), **ALAC**, **FLAC**, Ogg Vorbis, **Opus**, WavPack, APE, TTA, Musepack, WMA Std/Pro, WAV/AIFF and others (about 30 formats). Caveats:
- Not every format decodes in real time on every target. FLAC, MP3 and AAC are fine on the iPod Video. Opus, APE (high compression) and WMA Pro cost more CPU and battery. ⚠️
- ⚠️ Hi-res FLAC (24/96) is downsampled on the device and wastes space and battery. RockSync downsamples to 16/44.1 with ffmpeg for this reason.
- **Implication:** support an optional transcode profile (e.g. keep FLAC ≤ 16/44.1, convert hi-res to 16/44.1 FLAC, or convert everything to AAC/MP3/Opus at a set bitrate for capacity).

### 2.5 Database (tagcache)
- ✅ Built **on the device** by scanning files and parsing **embedded tags**. Options: *Initialize Now* (full rebuild; **wipes runtime data**), *Update Now* (adds new and removes deleted files; keeps runtime data), *Auto Update* (at boot), and *Load to RAM*.
- ✅ Files are `/.rockbox/database_*` (`.tcd`). Format reference: the Rockbox wiki page *TagcacheDBFormat*.
- ✅ Indexed tags: artist, album, genre, title, filename, composer, comment, **albumartist**, grouping, year, **discnumber**, tracknumber, bitrate, length. Runtime tags: **playcount, rating, playtime, lastplayed**, lastelapsed, lastoffset. Virtual tags: autoscore, entryage, and others.
- ✅ **Multi-value genres are not indexed separately.** A file tagged "Rock;Grunge" counts as one genre string. ⚠️ This comes from an old wiki page.
- ✅ *Export Modifications* writes runtime data to an ASCII changelog (`/.rockbox/database_changelog.txt`), which can be imported back and survives database format changes. This is the readable route to on-device **ratings and playcounts**.
- ✅ `lastplayed` is a monotonic **serial counter** (`lastplayed = tagcache_increase_serial()`), not a wall-clock time. Use the playback or scrobbler log for real timestamps.
- ✅ **Rating is 0–10**, set from the WPS context menu (it wraps after 10), and is stored with playcount/playtime when a track is unbuffered.
- ⚠️ A PC-side database builder exists in the source tree (`tools/database`), but it is described as a "_very_ skeleton" that must be compiled per target. **Not recommended.** Let the device run *Update Now* / *Auto Update* after a sync.
- Browse menus can be customised with `/.rockbox/tagnavi_custom.config`. Don't edit `tagnavi.config`, because installs overwrite it. Supports filters, `%sort`, `%limit`, and `%strip`. RockPlex could ship a tuned menu.

### 2.6 Playlists
✅ From `playlist.c`:
- `.m3u8` = UTF-8 (the default for any extension other than `.m3u`). `.m3u` is read with the codepage decoder. **Always write `.m3u8` in UTF-8.**
- Lines starting with `#` are skipped, so `#EXTM3U`/`#EXTINF` are harmless but unused.
- Backslashes become `/`. `C:/…` style paths have the drive letter replaced by the playlist's volume. `/Music/…` absolute paths are used as-is. Relative paths resolve against the playlist's folder.
- Limited by the *Max Playlist Size* setting. Playlist files max out at 256 MB.
- ⚠️ plex-ipod-sync notes that Rockbox **playlist paths are case-sensitive**, so generate paths exactly as written to disk.
- **Recommendation:** write `/Playlists/<name>.m3u8` with absolute `/Music/...` paths. Materialise Plex smart playlists at sync time.

### 2.7 Album art
✅ From the manual's album-art appendix:
- **Baseline JPEG or BMP only.** Progressive or multi-scan JPEG is **not supported**, and neither is RLE BMP. PNG is not mentioned.
- ~~Embedded art works only for ID3v2 and MP4.~~ **Correction:** that appendix is out of date. Current source supports embedded art in **FLAC** (first PICTURE block) and **Ogg/Opus/Vorbis**, as well as ID3v2, MP4, APE and ASF ✅. See the [Media Pipeline doc](RockPlex-Media-Pipeline.md) §5. PictureFlow still can't use embedded art.
- Search order: embedded → `./<filename>.jpg|bmp` → `./<albumtitle>.jpg` → `./cover.jpg|bmp` → `./folder.jpg` → `/.rockbox/albumart/<albumartist>-<albumtitle>.jpg` → `../<albumtitle>.jpg` → `../cover.jpg`. In names, `\ / : < > ? * |` become `_`.
- **Implication:** embed a small **baseline** JPEG *and* write a baseline `cover.jpg` per album folder, re-encoded locally (Plex art is often progressive). Details are in the Media Pipeline doc.

### 2.8 Play statistics: data available for syncing back
- ✅ **Playback logging** (a setting) writes a log in `/.rockbox/` that rotates into `playback_0001.log` and onward at 512 KB.
- ✅ The **lastfm_scrobbler plugin** turns the log into `/.scrobbler.log` (Audioscrobbler portable-player format, tab-separated: artist, album, title, track#, length, rating L/S, **unix timestamp**, MusicBrainz ID). It has listen thresholds and de-duplication. It runs on demand and is not resident.
- Database runtime data (playcount, rating) can be read via *Export Modifications*.
- **Implication for two-way sync:** RockPlex reads the playback log (or `.scrobbler.log`), matches each play to a Plex `ratingKey` through its own manifest (by device path, so no fuzzy tag matching), and calls scrobble on Plex. Ratings map 1:1 (Rockbox 0–10 ↔ Plex 0–10). ⚠️ Plex scrobble sets `lastViewedAt` to *now*, not the original play time, and `viewCount` goes up one per call. The engine could also forward plays to Last.fm.

### 2.9 Storage mods
- ✅ The iPod Video uses a 1.8" ATA (4200 rpm) drive. iFlash-style adapters (SD/microSD) are common. Rockbox has LBA48, so very large cards work. The iFlash vendor tests boards to **1 TB**, and suggests formatting FAT32 and Class 10 / UHS-I cards (SanDisk Ultra or better), verified with H2testw.
- ✅ Apple firmware struggles past about 40k tracks; Rockbox handled about 65k tracks in user reports on the Classic. On the Video, the **32 MB vs 64 MB RAM** difference matters for *Load to RAM* with large libraries. 🔬

---

## 3. Prior art

| Project | Stack | What it does | Gap vs RockPlex |
|---|---|---|---|
| **undergroundpost/plex-ipod-sync** | Python + python-plexapi + Rich | Plex → Rockbox iPod: full library or selected playlists, completes partial albums, mirror mode (adds and removes), `.m3u` playlists with `/Music/` paths. Copies files via **path mapping / SMB mount** (no API download). Per-device state file on the iPod. | macOS-only (diskutil, Keychain); no transcode; no stats sync back; 3★, created Oct 2025 |
| **FugginBeenus/grapefruit** | Tauri (Rust) + React + Python sidecar | Treats a local folder library as the source of truth; device sync (selective, mirror, delta), M3U8 import, pushes playlists to Plex, pulls Plex metadata into files. | Plex is a secondary source, not the master; no Rockbox stats or database handling documented |
| **MariKiwi/RockSync** | Python + PySide6 | A replacement for iTunes on Rockbox devices: one-way mirror, resizes covers to 100×100, downsamples FLAC, manages `/.rockbox` configs and themes. | No Plex at all |
| **beets-alternatives** | Python (beets plugin) | Keeps a transcoded or filtered copy of a library in sync with a target folder. | Requires beets as the source of truth |
| **Ardakilic/rocklist** | Go | Parses the Rockbox tagcache and builds smart playlists from Last.fm, Spotify and MusicBrainz. | Reads the database; a useful example of parsing `.tcd` files |

**None of them sync plays or ratings from the iPod back to Plex.** That is RockPlex's distinct feature.

---

## 4. Design implications (first pass)

1. **Getting files (pick one or both):**
   - *HTTP download* via `/library/parts/...?download=1`. Works from any machine on the LAN with only the token; no share mounting. Recommended default.
   - *Direct file copy* via path mapping, when the engine runs on the box holding the music. Faster for the first full sync.
   - *Transcode locally* with ffmpeg; skip Plex's transcoder.
2. **Manifest on the device:** `/.rockplex/manifest.json` (or SQLite) mapping `ratingKey` + `guid` + `partId` ↔ device path, size, source `updatedAt`, and a content hash. That makes syncs incremental, lets RockPlex move the iPod between machines, and maps logged plays back to Plex exactly.
3. **Tags:** decide whether to (a) copy files as-is or (b) **rewrite tags on the device copy from Plex metadata** (album artist, disc/track, genre, year, maybe ratings), using mutagen or TagLib#. Option (b) makes Rockbox's browse menus match Plex.
4. **File layout:** `/Music/<AlbumArtist>/<Year> - <Album>/<Disc>-<Track> <Title>.<ext>`. Strip characters FAT32 rejects (`\ / : * ? " < > |`, trailing dots and spaces), keep paths well short of 260 characters, and treat paths as case-sensitive in playlists.
5. **Album art:** a baseline `cover.jpg` per album folder, re-encoded locally.
6. **Playlists:** UTF-8 `.m3u8` files in `/Playlists`, with absolute paths.
7. **After a sync:** leave *Auto Update* on, or ask the user to run *Database → Update Now*. **Never** trigger *Initialize* if runtime data matters (or export the changelog first).
8. **Return path (on each connect, before pushing changes):** read the playback log / `.scrobbler.log` and the exported changelog → map plays to `ratingKey` → scrobble and rate on Plex → archive the logs on the PC.
9. **Auth:** PIN flow + token store; add JWT refresh later.
10. **Language:** python-plexapi is the most proven route. C#/.NET with a thin HttpClient wrapper fits a Windows desktop and tray app and your existing stack (Scanny-style service + UI split). Both work; the Plex API is simple HTTP either way.

## 5. Open questions (decisions needed)
- ~~Which iPod?~~ **Answered:** 5.5G **80 GB** (64 MB RAM).
- ~~Formats?~~ **Answered:** mostly FLAC, some 24/96; keep transcoding to a minimum; Plex metadata and art must be written into the device files. See the Media Pipeline doc.
- Where does the engine run: on the Windows desktop the iPod plugs into, as a service on the home server, or both?
- ~~Sync scope?~~ **Answered:** All songs, plus multi-select playlists, artists and albums. Playlists are mission-critical. See [Selection & Playlists](RockPlex-Selection-and-Playlists.md).
- ~~Rewrite tags from Plex metadata?~~ **Answered:** yes, on the iPod copies only, so the iPod mirrors Plex. Source files are never touched.
- Two-way stats: plays only, or ratings as well? Push to Last.fm as well?

## 6. Not yet verified (robots-blocked or undocumented)
- The exact current Rockbox manual wording on USB/charge-only buttons, and the changelog line format. Several rockbox.org manual and forum pages block automated fetching. 🔬 Check with a test export on the device.
- Exact paths for Plex playlist, scrobble and rate in the new official docs (they come from python-plexapi behaviour).
- Whether the Plex Pass statement applies to API use on your own server over the LAN.

---

### Sources
- Plex: [Pro Week '25: API Unlocked](https://www.plex.tv/blog/plex-pro-week-25-api-unlocked/) · [PMS API docs](https://developer.plex.tv/pms/) · [Forum: Public API docs](https://forums.plex.tv/t/public-api-docs-for-pms-are-here/930957) · [Forum: JWT Authentication](https://forums.plex.tv/t/jwt-authentication/931646) · [Forum: Authenticating with Plex](https://forums.plex.tv/t/authenticating-with-plex/609370) · [Forum: Music transcoding API](https://forums.plex.tv/t/music-transcoding-api/696895) · [Webhooks](https://support.plex.tv/articles/115002267687-webhooks/) · [PMS URL commands](https://support.plex.tv/articles/201638786-plex-media-server-url-commands/) · [Important 2025 Plex updates](https://www.plex.tv/blog/important-2025-plex-updates/) · [Forum: plexapi download filename](https://forums.plex.tv/t/plexapi-download-filename/774478)
- Libraries: [PlexAPI on PyPI](https://pypi.org/project/PlexAPI/) · [python-plexapi audio](https://python-plexapi.readthedocs.io/en/latest/modules/audio.html) · [base](https://python-plexapi.readthedocs.io/en/latest/modules/base.html) · [mixins](https://python-plexapi.readthedocs.io/en/latest/modules/mixins.html) · [LukeHagar.PlexAPI.SDK (NuGet)](https://www-1.nuget.org/packages/LukeHagar.PlexAPI.SDK)
- Rockbox: [Wikipedia: Rockbox](https://en.wikipedia.org/wiki/Rockbox) · [LWN: Rockbox 4.0](https://lwn.net/Articles/1016207/) · [Wiki: DataBase](https://www.rockbox.org/wiki/DataBase.html) · [Manual: album art appendix](https://git.lenord.me/len0rd/rockbox/src/branch/master/manual/appendix/album_art_info.tex) · [Manual: lastfm_scrobbler](https://git.lenord.me/devs/foxbox/src/branch/master/manual/plugins/lastfm_scrobbler.tex) · [Source: tagcache.h](https://git.lenord.me/devs/foxbox/src/branch/master/apps/tagcache.h) · [Commit: runtime stats and rating](https://git.lenord.me/devs/foxbox/commit/226cb7b0b82fe3d3736b8206d8a94c67c359ab64) · [Source: playlist.c](https://git.lenord.me/devs/foxbox/src/branch/master/apps/playlist.c) · [Source: ipodvideo.h](https://git.lenord.me/devs/foxbox/src/commit/703db94de695c5efc047bbfe8e709550888b9136/firmware/export/config/ipodvideo.h) · [Source: tools/database](https://git.lenord.me/len0rd/rockbox/src/commit/653bca7ff4b8b9492562ed421b531996cf26ecfd/tools/database/database.c) · [Apple Wiki: iPod with video](https://theapplewiki.com/wiki/IPod_with_video_(5th_generation)) · [iFlash: SDHC/SDXC](https://www.iflash.xyz/ipod-and-sdhc-sdxc-cards/comment-page-11/) · [rockpod fork](https://github.com/nuxcodes/rockpod)
- Prior art: [plex-ipod-sync](https://github.com/undergroundpost/plex-ipod-sync) · [grapefruit](https://github.com/FugginBeenus/grapefruit) · [RockSync](https://github.com/MariKiwi/RockSync) · [rocklist](https://pkg.go.dev/github.com/Ardakilic/rocklist) · [beets-alternatives](https://www.pypi.org/project/beets-alternatives/)
