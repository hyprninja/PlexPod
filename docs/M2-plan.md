# Milestone 2: Media Pipeline

**Goal:** turn one selected Plex track into the exact file the iPod should hold: downloaded from Plex, converted only when the format rule says so, every old tag and picture wiped, one clean tag set written from Plex, album art embedded, and the result verified. Albums also get a `cover.jpg`.

**Still nothing is written to the iPod.** M2 output goes to a folder on the PC through a **Test export** command, so the spike tests (`../RockPlex-Media-Pipeline.md` §7) can be run by copying the result to the iPod by hand. Writing to the device, the manifest and the Sync button belong to M3.

Decisions and invariants are in `../CLAUDE.md`. The design is `../RockPlex-Media-Pipeline.md` (§3 format, §4 tags and naming, §5 art).

---

## 1. Solution changes

```
src/
  RockPlex.Core/     + TagSet, TagPolicy, TagMapper, DevicePathBuilder, FlacStreamInfo (pure; still no packages, no I/O)
  RockPlex.Plex/     + downloads, album/artist detail, art download
  RockPlex.Media/    NEW  net10.0-windows: TagLibSharp, ffmpeg runner, WIC art encoder, pipeline, cache
  RockPlex.App/      + Test export command, ffmpeg settings, export report
tests/
  RockPlex.Tests/    → net10.0-windows (Media uses WIC); + tiny FLAC/MP3/M4A fixtures
```
Dependencies: App → Media → Plex, Core. Media is Windows-only because it uses WIC (`System.Windows.Media.Imaging`) for baseline JPEGs, which adds no package. The app is Windows-only anyway (owner decision 1).

New package: **TagLibSharp** (LGPL-2.1, used as an unmodified library). **No ffmpeg binary is committed or bundled**; see §4.1.

---

## 2. RockPlex.Core (pure)

- **`TagSet`** (record): Title, Artist, AlbumArtist, Album, TrackNumber?, TrackTotal?, DiscNumber?, DiscTotal?, Date?, Genre?, Grouping?, Compilation, Label?, Composer?, MusicBrainzTrackId?, ReplayGain? (track/album gain and peak), RockPlexRatingKey, RockPlexGuid, RockPlexPartId. Plus `ContentHash()`, a stable hash of everything written. M3 uses it to detect tag-only changes.
- **`TagPolicy`** (record; D4): one switch **per tag field** saying whether it is written. Every field the iPod could show is covered, so any of them can be stripped when its data is junk:

  | Field | Source | Default |
  |---|---|---|
  | Title | Plex | Write |
  | Artist (track artist) | Plex | Write |
  | Album artist | Plex | Write |
  | Album | Plex | Write |
  | Track number | Plex | Write |
  | Track total | Plex | Write |
  | Disc number | Plex | Write |
  | Disc total | Plex | Write |
  | Date / year | Plex | Write |
  | Genre (first Plex genre only) | Plex | Write (D1) |
  | Grouping (first Plex style) | Plex | Strip |
  | Compilation flag | Plex | Write |
  | Label (studio) | Plex | Strip (Rockbox ignores it) |
  | MusicBrainz track ID | Plex | Write (helps scrobble matching later) |
  | Composer | **Kept from the original file** | Keep (D3) |
  | Comment | **Kept from the original file** | Strip |

  - Every field can be switched off, including the core ones (title, artist, album, album artist, track number). Switching off a core field shows a warning in Settings, because Rockbox's database then files those tracks under *&lt;Untagged&gt;*.
  - **Not user-facing and always written:** `ROCKPLEX_RATINGKEY`, `ROCKPLEX_GUID`, `ROCKPLEX_PARTID`. M3 rebuilds the manifest from them and M4 maps plays back to Plex through them, so stripping them would break syncing. Rockbox ignores them.
  - "Strip" means the field is absent from the file, never written as an empty value. Since every sync wipes all tags first, a field switched off later disappears from the iPod copies at the next sync.
  - The policy is part of `TagSet.ContentHash()`, so changing a switch makes M3 re-tag the affected files (tags only; no audio is copied again).
- **`TagMapper.Map(TrackItem, AlbumDetail, ArtistDetail?, SourceTags, TagPolicy) → TagSet`**, following the table in pipeline §4.2. Fields the policy strips are left null:
  - `ARTIST` = `originalTitle`, else the album artist. `ALBUMARTIST` = the album artist only (no variant keys).
  - `DATE` = `originallyAvailableAt` (YYYY-MM-DD), else year.
  - `GENRE` = the album's first genre (fallback: the artist's first), never joined (D1). `GROUPING` = the first style.
  - `COMPOSER` and `COMMENT` come from the original file's tags (`SourceTags`, read by Media before the wipe), only when the policy keeps them (D3, D4). The first non-empty value is used and trimmed. Nothing else from the original file survives. ReplayGain is never written (D2).
  - `COMPILATION=1` when the album artist is "Various Artists".
  - `TRACKTOTAL` = number of tracks on the same disc. `DISCTOTAL` = highest disc number on the album.
  - Whitespace is trimmed and empty values are dropped, never written as empty tags.
- **`DevicePathBuilder.Build(PathFields, extension, isMultiDisc) → string`**: `/Music/<AlbumArtist>/<Year> - <Album>/<Disc>-<Track:00> <Title>.<ext>`.
  - Paths are built from the **Plex values directly, regardless of `TagPolicy`**: stripping Date or Album artist from the tags doesn't change the folder layout. A missing value falls back to `Unknown Artist`, `Unknown Album` or no year prefix.
  - FAT32 sanitising: replace `\ / : * ? " < > |` and control characters, strip trailing dots and spaces, avoid reserved names (`CON`, `NUL`, …), cap each segment at about 120 UTF-16 units and the whole path at 240.
  - Unicode is normalised to NFC, so "Sigur Rós" is always the same bytes. 🔬 UTF-8 path behaviour on the device is still on the spike list.
  - Collision handling (`[2]`, edition labels) is part of M3, because it needs the manifest.
- **`FlacStreamInfo.Parse(ReadOnlySpan<byte>)`**: reads the `fLaC` marker and the STREAMINFO block (min/max block size, sample rate, channels, bits per sample, total samples, audio MD5) and reports where the audio frames start. M2 makes its final format decision from **the downloaded file itself**, not from Plex metadata (the M1 enricher's 16/44.1 assumption for low-bitrate FLAC is never trusted here).
- **`FinalFormatPolicy.Decide(FlacStreamInfo)`**: `FormatPolicy`'s rule (min(bits, 16) / min(rate, 44100), never raised) plus the block-size safety net: a 16/44.1 file with max block size > 4608 → `ReencodeLossless` (FLAC → FLAC, 4096-sample frames, bit-identical audio).

## 3. RockPlex.Plex

- `DownloadPartAsync(MediaPartRef, Stream destination, IProgress<long>?, ct)` → `GET {server}{Part.key}?download=1`, with the token in the header (never in the URL). Read-only. Checks the byte count against `Part.size` when Plex supplies it.
- `GetAlbumDetailAsync(albumKey)` → `/library/metadata/{albumKey}`: `Genre[]`, `Style[]`, `studio`, `originallyAvailableAt`, `year`, `leafCount`, `thumb`.
- `GetArtistDetailAsync(artistKey)` → `Genre[]` (the genre fallback).
- `GetTrackDetailAsync` gains `includeGuids=1` (the MBID from `Guid[] id="mbid://…"` ⚠️), plus the audio stream's `gain`, `albumGain`, `peak` and `albumPeak`.
- `DownloadImageAsync(thumbPath)` → full-size bytes of the album poster that is selected in Plex.
- Fixtures for album detail, artist detail and a track with guids and gain (representative, as in M1).

## 4. RockPlex.Media (new)

### 4.1 ffmpeg
- **`FfmpegLocator`** looks, in order, at: the path in settings, `PATH`, then the winget install folder. It checks `ffmpeg -version` for `--enable-libsoxr`; without soxr, conversions are refused with a clear message (copies still work).
- **`FfmpegRunner`** runs ffmpeg as an async process with stderr captured. Cancelling kills the process tree. A non-zero exit raises `FfmpegException` with the last stderr lines.
- The commands are the ones in pipeline §3.2 (soxr precision 28, TPDF dither, `-frame_size 4096`, `-map_metadata -1`), plus the lossless re-encode for the block-size safety net.

### 4.2 Tags: `TagWriter` (TagLibSharp)
Clean slate, then one canonical set:
- **FLAC:** remove every tag type (Xiph, ID3v1, ID3v2, APE) and every picture, then write Xiph comments and one PICTURE (type 3, `image/jpeg`).
- **MP3:** strip ID3v1, ID3v2 and APE; write ID3v2.4 (UTF-8) and one APIC.
- **M4A/AAC/ALAC:** clear the Apple tag; write the atoms and one `covr`.
- **Ogg Vorbis/Opus:** Xiph comments plus a `METADATA_BLOCK_PICTURE`.
- **Anything else** (WAV, AIFF, WavPack, …): audio copied, tags not rewritten, flagged in the report as "not re-tagged".
- After saving, the file is re-read and the tags compared with the `TagSet`. Any difference fails the track.

### 4.3 Art: `ArtProcessor` (WIC)
- Decode whatever Plex returns (JPEG, PNG, WebP if WIC has the codec), pad non-square art to square, and resize with the high-quality scaler.
- Encode a **baseline JPEG** at quality 85: **300×300 for embedding** and **500×500 for `cover.jpg`**.
- Cached per album in `cache\art\`, keyed by album rating key + thumb path.

### 4.4 Verification: `AudioVerifier`
- **Copies and lossless re-encodes:** the demuxed audio is hashed (`ffmpeg -i X -map 0:a -c copy -f md5 -`) for the source and the output; they must match. FLAC also checks that the STREAMINFO MD5 matches the source.
- **Conversions:** the output's STREAMINFO must say bits ≤ 16 and rate ≤ 44100, **and neither may be higher than the source** (the never-upsample invariant checked on the real output). It must also decode with no errors (`-v error -f null`), and its duration must match the source within 50 ms.
- A failed verification never produces output. The track is reported and skipped.

### 4.5 `TrackPipeline.ProcessAsync(TrackJob, ct) → ProcessedTrack`
1. Download the original into `cache\work\` (temp name, renamed when complete).
2. Read STREAMINFO (FLAC) → `FinalFormatPolicy` → copy, convert or lossless re-encode.
3. Write the tags and embed the art (on the work copy only; **the downloaded original is never what gets written to**, and the server file is never touched).
4. Verify (§4.4).
5. Return the output path, final format, `TagSet` hash, byte size and a report line. Work files are deleted afterwards.
- **Conversion cache:** converted outputs, *before* tagging, are kept in `cache\converted\` keyed by `partId + updatedAt + profileVersion`, so a tag change in Plex never re-converts. Copies are not cached (they're just re-downloaded). Default cap: **20 GB**, least-recently-used eviction (setting).
- Concurrency: 2 downloads and 1 conversion at a time (settings). Everything is cancellable and resumes cleanly; a half-done file is never left under a final name.

## 5. RockPlex.App

- **Test export…** button (enabled when something is selected): pick a folder → process the selected tracks → write `<folder>\Music\…` plus `cover.jpg` per album, a `RockPlex-export.txt` report (per track: copied / converted 24/96 → 16/44.1 / re-encoded / not re-tagged / failed + reason), and `.m3u8` copies of the selected playlists so the spike tests can check them.
- **Refuses the iPod's drive** (any drive with `.rockbox`): the manifest rule means only M3's sync engine may write there. You copy the test files across by hand.
- Progress: overall bar, current track, and a Cancel button. Errors are listed, never shown one dialog per track.
- Settings: ffmpeg path (with a "Detect" button and a status line such as "ffmpeg 9.0.2 with soxr ✓") and the conversion-cache cap.
- **Tags written to the iPod** (D4): a checklist with one row per `TagPolicy` field, in two groups ("From Plex" and "Kept from the original file"), plus **Restore defaults**. Switching off a core field shows the *&lt;Untagged&gt;* warning inline. A note explains that RockPlex's own ID tags are always written. Saved in `settings.json`; the Test export report lists the policy that was used.
- The capacity summary shows **real** sizes for anything already in the conversion cache.

## 6. Tests

- **Core:** TagMapper (artist fallback, compilation, date fallback, totals per disc, empty values dropped, every TagPolicy switch on and off (a stripped field is absent, not empty), genre never joined, composer/comment carry-over only when kept, identity tags written even with every switch off, policy change → different ContentHash, no ReplayGain keys); DevicePathBuilder (illegal characters, reserved names, trailing dots, length caps, NFC, multi-disc prefix); FlacStreamInfo (fixture headers, a truncated header, not-FLAC); FinalFormatPolicy (the full M1 matrix plus the block-size rule; property check that nothing is ever raised).
- **Plex:** download writes the exact bytes and sends the token in the header only; size mismatch throws; album/artist detail mapping; guids and gain parsing.
- **Media (unit):** TagWriter on tiny committed fixtures (FLAC, MP3, M4A) that start out *messy* (duplicate ARTIST, ID3 glued onto FLAC, three pictures) → exactly one clean set and one picture afterwards, and with every policy switch off only the ROCKPLEX_* keys remain; ArtProcessor produces a baseline JPEG (no SOF2 marker) of the right size and pads non-square art.
- **Media (integration, `[FfmpegFact]`, skipped with a reason when ffmpeg isn't found):** generated 24/96 → 16/44.1; 24/32 → 16/32 (rate kept); 16/44.1 → bit-perfect copy (audio MD5 equal); 16/22.05 → copied, never upsampled; block size 8192 → lossless re-encode with an identical MD5; cancellation kills ffmpeg and leaves no output file.
- **Pipeline:** a fake Plex download plus real TagWriter/ArtProcessor → final file, report line, cache hit on the second run.

## 7. Acceptance criteria

1. Clean build, zero warnings; all tests green (ffmpeg tests run on this PC).
2. Test export of a 16/44.1 FLAC album: every file's audio is bit-identical to the source; tags are exactly the Plex values; one 300×300 baseline picture each; `cover.jpg` present.
3. Test export of a 24/96 album: 16/44.1 FLAC output, verified, roughly the estimated size; a second export re-uses the cache (no ffmpeg run).
4. A messy source (duplicate tags, glued ID3, huge embedded art) comes out clean.
5. MP3 and M4A tracks: audio frames unchanged, tags and art rewritten.
6. Exporting to the iPod drive is refused. **No file on the Plex server is ever modified** (downloads only).
7. Spike tests 1–3 from pipeline §7 can be run with the exported files.

## 8. Owner decisions (18 Oct 2026)

| # | Question | Decision |
|---|---|---|
| D1 | **Genre** | **First Plex genre only**, never joined. Can be stripped entirely through D4 (the owner doesn't use genres). |
| D2 | **ReplayGain** | **Skipped in M2.** Revisit after spike test 4 has checked Plex's units. |
| D3 | **Carry-over allow-list** | **`COMPOSER`** kept from the source file by default; **`COMMENT`** available but off by default. Everything else from the source is always dropped. |
| D4 | **Per-tag stripping** | **Every tag field has its own write/strip switch** (§2 `TagPolicy`, §5 settings), because source data for many fields is junk. Only RockPlex's internal ID tags are exempt. |

## 9. Out of scope for M2
Writing to the iPod, the manifest, collision renaming, the real `.m3u8` writer with its guarantees (M3); stats (M4); lyrics `.lrc` sidecars; computing ReplayGain locally with `rsgain`.
