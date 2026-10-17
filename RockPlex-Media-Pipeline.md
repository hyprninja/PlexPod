# RockPlex: Media Pipeline Design (Formats, Tags, Art)

*Draft 1, 18 Oct 2026. Builds on the [Research Brief](RockPlex-Research-Brief.md).*

Status tags: ✅ confirmed from a primary source · ⚠️ inferred, needs checking · 🔬 needs a test on the real iPod

---

## 1. Requirements (as given)

| # | Requirement |
|---|---|
| R1 | Target: **iPod 5.5G 80 GB** running Rockbox. 64 MB RAM, about 73 GiB usable. |
| R2 | Library is **mostly FLAC**. **Avoid transcoding wherever possible.** |
| R3 | Some albums are **24/96**. Handle them so the iPod copes. |
| R4 | **Plex is the source of truth** for metadata and album art. Plex matched these from its own database, so the files' embedded tags may be wrong or messy. |
| R5 | RockPlex must **write the correct Plex metadata and art into the files** it puts on the iPod. |
| R6 | **Format rule (decided 18 Oct 2026):** FLAC above 16-bit/44.1 kHz is automatically converted **down** to 16/44.1. **Nothing is ever upsampled.** MP3, AAC and every other format are copied with the audio untouched. |

**Decided (18 Oct 2026):** tags are rewritten on the **iPod copies only**, purely so the iPod shows exactly what Plex shows. Plex stays the single source of truth, and **source files on the server are never modified**. That also means Plex never re-analyses anything, nothing on the server can be damaged, and a bad tagging run is undone by re-syncing.

---

## 2. Why the device copies need a clean slate (Rockbox facts)

From Rockbox's tag parser (`metadata_common.c`, `parse_tag()`) ✅:

- Key matching is **case-insensitive**.
- For **text fields, the first value wins**. A later duplicate is ignored. If a messy file has `ARTIST=wrong` before `ARTIST=right`, Rockbox shows "wrong", so patching tags in place isn't safe. **Wipe everything, then write one canonical set.**
- Numeric fields (`tracknumber`, `discnumber`, `date`) go the other way: **last value wins**.
- Vorbis keys Rockbox reads:

| Rockbox field | Vorbis keys it accepts |
|---|---|
| title | `TITLE` |
| artist | `ARTIST` |
| album | `ALBUM` |
| albumartist | `ALBUMARTIST`, `ALBUM ARTIST`, `ENSEMBLE` |
| tracknum | `TRACKNUMBER` (integer parse, so `3` or `3/12`) |
| discnum | `DISCNUMBER`, `DISC` |
| year | `DATE` (integer parse, so `1997-05-12` → 1997; anything below 1900 → 0) |
| genre | `GENRE` (**one value only**; first wins) |
| composer | `COMPOSER` |
| comment | `COMMENT` |
| grouping | `GROUPING`, `CONTENT GROUP`, `CONTENTGROUP` |
| MusicBrainz track ID | `MUSICBRAINZ_TRACKID` (also passed through to the scrobbler log) |
| ReplayGain | other keys go to `parse_replaygain()`, i.e. `REPLAYGAIN_TRACK_GAIN/PEAK`, `REPLAYGAIN_ALBUM_GAIN/PEAK` ⚠️ |

- Unknown keys are ignored harmlessly, so RockPlex can store its own identity tags in the file.
- The database does **not** split multi-value genres. Writing `Rock; Progressive Rock` creates one genre called exactly that.

---

## 3. Audio format policy (R2 + R3)

### 3.1 Facts behind it
- ✅ **The hardware tops out at 48 kHz.** `ipodvideo.h` sets `HW_SAMPR_CAPS` to 8–48 kHz with no 88.2/96/192. A 96 kHz file is **resampled in software, in real time**, on an 80 MHz ARM7. That costs CPU boost time and battery, and the on-device resampler is built for speed, not quality.
- ⚠️ Rockbox's output path on this target is believed to be **16-bit**, so 24-bit sources are reduced on the device anyway. 🔬
- ✅ **Some hi-res FLACs may not decode at all.** A Sept 2025 commit raised FLAC `MAX_BLOCKSIZE` to 8 KB "for files with larger blocks, common in high-frequency recordings", but *only on more capable devices*. That came after 4.0 and may not include the iPod Video. ⚠️ A 24/96 file encoded with 8192-sample blocks could fail on the iPod.
- **Size and battery (estimates):** 24/96 FLAC runs about 3× the size of 16/44.1. On the 64 MB model, one buffer fill holds roughly **8 minutes of 16/44.1 FLAC but only about 2.5 minutes of 24/96**. That means about 3× as many hard-drive spin-ups, and spin-ups are the biggest battery drain on an HDD iPod.

### 3.2 Rules (decided; R6)

Each track is checked using Plex `Media.container` + `AudioStream.bitDepth/samplingRate` **before anything is downloaded**, so the plan and the fit report already account for conversions.

**FLAC.** Target ceiling **16-bit / 44.1 kHz**. Each dimension is only ever lowered, never raised:
- output rate = `min(source rate, 44100)`
- output bit depth = `min(source bits, 16)`

| Source FLAC | Action | Audio changed? |
|---|---|---|
| 16/44.1 (most of the library) | **Bit-perfect copy**; only the tags and art blocks are rewritten | **No.** Checked by the STREAMINFO audio MD5 matching the source |
| 16/48 | Resample → 16/44.1 | Yes |
| 24/44.1 | Reduce bit depth → 16/44.1 (dithered) | Yes |
| 24/48, 24/88.2, 24/96, 24/176.4, 24/192 | Resample + reduce → 16/44.1 | Yes |
| Below 44.1 kHz and/or below 16-bit (e.g. 16/32, 16/22.05, 8-bit) | **Copy untouched.** Never upsampled | No |
| Below 44.1 kHz but 24-bit (rare, e.g. 24/32) | Reduce bit depth only → 16/32 (rate kept, never raised) | Yes (bit depth only) |
| 16/44.1 with blocksize > 4608 (rare) | Lossless FLAC → FLAC re-encode with 4096-sample blocks (safety net for the Rockbox decoder limit) | **No.** Audio is bit-identical; MD5 still matches |
| Multichannel (any rate/depth) | Rate/depth rules above apply; channels are kept, since Rockbox downmixes to stereo itself ✅. Flagged in the report | Per the rules above |

**Everything else is copied with the audio untouched.** That covers MP3, AAC/M4A, ALAC, Ogg, Opus, WavPack, WAV, AIFF and the rest, whatever their resolution. Only their tags and embedded art are rewritten from Plex (ID3v2 for MP3, MP4 atoms for M4A, and so on), and neither touches the audio frames.
- ⚠️ Edge case to be aware of: a hi-res **ALAC/WAV/AIFF** file (e.g. 24/96) is copied as-is under this rule, so the iPod resamples it live. The fit report will flag any of these.

**How conversions are done:** a high-quality resampler (soxr, very-high quality) plus TPDF dither on the final step down to 16-bit. The output is always **FLAC**, with source tags and pictures dropped (RockPlex writes fresh ones from Plex afterwards):

```
# rate > 44.1k (any depth)  → 16/44.1
ffmpeg -i in.flac -map 0:a -map_metadata -1 \
  -af aresample=44100:resampler=soxr:precision=28:dither_method=triangular \
  -sample_fmt s16 -c:a flac -compression_level 8 -frame_size 4096 out.flac

# rate ≤ 44.1k but depth > 16 → reduce bit depth only (no aresample rate given = rate unchanged)
ffmpeg -i in.flac -map 0:a -map_metadata -1 \
  -af aresample=resampler=soxr:dither_method=triangular \
  -sample_fmt s16 -c:a flac -compression_level 8 -frame_size 4096 out.flac
```
(SoX equivalent: `sox in.flac -b 16 out.flac rate -v 44100 dither`.)

✅ Tested with ffmpeg 6.1 on synthetic files: 24/96 came out as 16/44.1 and 24/32 as 16/32 (rate kept). Both outputs had 4096-sample blocks.

Converted files are cached on the PC, keyed by Plex `partId` + the part's `updatedAt` + a conversion-profile version, so a file is only converted again when the source changes. A tag-only change in Plex re-tags the cached file and skips the conversion.

### 3.3 Capacity (80 GB ≈ 73 GiB usable)

With the R6 rule every FLAC lands at 16/44.1 or below, so capacity planning is simple:

| On the iPod | Approx. per album (48 min) | Fits |
|---|---|---|
| 16/44.1 FLAC | 270–380 MB | **~210–290 albums / ~2,400–3,300 tracks** |
| (for comparison) 24/96 FLAC if it had been copied raw | ~1 GB | ~80 albums |

**Implication:** a mostly-FLAC library probably **won't fit whole** on 80 GB, so **sync scope matters**. Planning works from Plex `Part.size` (plus the estimated post-conversion size for hi-res). RockPlex should show a **fit report** before writing anything: selected size, free space, and what gets dropped. Two later options if space gets tight: a lossy "overflow" profile for low-priority albums, or a storage upgrade (iFlash + SD; Rockbox supports large disks).

---

## 4. Metadata pipeline (R4 + R5)

### 4.1 Steps for each track
1. **Plan:** compare the selected Plex tracks against the device manifest. New, changed, and deleted tracks are detected from `ratingKey`, `Part.id`, `Part.updatedAt`, and a hash of the metadata RockPlex would write (so editing a track in Plex causes a **tag-only** re-write without copying the audio again).
2. **Fetch:** download the original part (`/library/parts/{id}/{ts}/file.flac?download=1`) into a local cache.
3. **Convert:** only if a §3.2 rule applies.
4. **Clean slate:** remove all Vorbis comments, **all embedded pictures**, any ID3v2/ID3v1/APE tags glued onto the FLAC (common in messy files), and any SEEKTABLE/APPLICATION junk if necessary. Keep STREAMINFO.
5. **Tag:** write the canonical set from §4.2.
6. **Art:** embed the cover per §5.
7. **Verify:**
   - Audio MD5 is unchanged (copied tracks).
   - Re-read the tags and confirm they match what was intended. Ideally also run the file through **Rockbox's own parser** via [`rockbox-metadata`](https://docs.rs/rockbox-metadata), a Rust crate that compiles Rockbox's C tag parsers for use on a PC ⚠️ (young, v0.1.1). That shows exactly what the iPod will display.
8. **Write to the device:** copy to a temporary name, then rename, so pulling the cable mid-copy leaves no half-written track. Then update the manifest.

### 4.2 Plex → tag mapping (FLAC Vorbis comments; MP3/M4A use the matching ID3/MP4 frames)

| Tag | From Plex | Notes |
|---|---|---|
| `TITLE` | track `title` | |
| `ARTIST` | track `originalTitle` if present, else `grandparentTitle` | ✅ `originalTitle` = "the artist for the track" (featured artists, compilations) |
| `ALBUMARTIST` | track `grandparentTitle` (= album artist) | Write only `ALBUMARTIST`, not the variants |
| `ALBUM` | track `parentTitle` | |
| `TRACKNUMBER` / `TRACKTOTAL` | `index` / album `leafCount` (per disc, if available) | |
| `DISCNUMBER` / `DISCTOTAL` | `parentIndex` / max disc on the album | |
| `DATE` | album `originallyAvailableAt` (YYYY-MM-DD), else album `year` | Rockbox reads the year from the front of the string |
| `GENRE` | album `genres[0]` (fallback: artist `genres[0]`) | **Single value.** Setting: first only (default) or a joined string |
| `GROUPING` | album `styles[0]` (e.g. a sub-genre or style) | Optional. Gives a second, finer "Style" browse menu through `tagnavi_custom.config` |
| `COMPILATION` | `1` when album artist = "Various Artists" | Rockbox ignores it; good practice for other players |
| `LABEL` / `ORGANIZATION` | album `studio` | Rockbox ignores it; kept for completeness |
| `MUSICBRAINZ_TRACKID` | track `guids` entry `mbid://…` if present | ⚠️ check which MBID Plex stores (recording vs track). Improves Last.fm matching from the scrobbler log |
| `REPLAYGAIN_TRACK_GAIN/PEAK`, `REPLAYGAIN_ALBUM_GAIN/PEAK` | `AudioStream.gain`, `.peak`, `.albumGain`, `.albumPeak` ✅ | ⚠️ Units and reference level need checking against a file with known RG values. Fallback: compute locally with `rsgain`. Optional setting |
| `ROCKPLEX_RATINGKEY`, `ROCKPLEX_GUID`, `ROCKPLEX_PARTID` | Plex IDs | Rockbox ignores them. Embedding identity **in the file** means the manifest can be rebuilt from the iPod alone, and logged plays map back to Plex exactly |
| `COMPOSER`, others Plex doesn't hold | carried over from the original file **only if on an allow-list** (setting) | Otherwise dropped; this is how the mess gets cleaned out |

**Not written:** sort tags. Rockbox's database doesn't appear to use them ⚠️ (there's an open feature request, FS#7287), and it has its own "ignore leading *The*" handling. Track ratings and play counts are not written as tags either; they go back to Plex through the stats path in the brief (§2.8).

**Optional later:** lyrics. Plex holds LyricFind lyrics (`LyricStream`, often timed `.lrc`). RockPlex could write a `.lrc` sidecar next to each track for Rockbox's lyrics plugin.

### 4.3 File and folder naming on the iPod (built from Plex data, not source filenames)
`/Music/<AlbumArtist>/<Year> - <Album>/<Disc>-<Track:00> <Title>.flac`
- The disc prefix appears only on multi-disc albums. There are no per-disc subfolders, so one `cover.jpg` serves the whole album.
- FAT32 sanitising: replace `\ / : * ? " < > |`, strip trailing dots and spaces, limit each name's length, and keep full paths well under 260 characters.
- Duplicate-album collisions (deluxe vs standard editions) get ` [2]` or a Plex edition label appended.
- Rockbox playlist paths are **case-sensitive** ⚠️, so playlists use the exact paths the manifest records.

---

## 5. Album art pipeline (R4)

**Facts:**
- ✅ Embedded art now works for **FLAC** (first PICTURE block, JPEG/PNG MIME), **Ogg/Opus/Vorbis**, MP3 (ID3v2), M4A (`covr`), APE and ASF.
- ✅ JPEGs must be **baseline**; progressive JPEGs don't show.
- ✅ PictureFlow can't read embedded art; it uses folder images.
- ⚠️ The FLAC change only reads the picture if it fits inside the metadata buffer, so **keep embedded art small**. 🔬 Also check that FLAC embedded art is in the Rockbox build you're running; it is present in current source.

**Plan:**
1. Download the **album's** Plex poster (album `thumb`, i.e. whichever poster is selected in Plex) at full size, one request per album, and cache it.
2. Re-encode locally to a **baseline JPEG, 300×300, quality about 85** (about 20–40 KB). Pillow and ImageSharp both write baseline by default. Square-crop or pad non-square art.
3. **Embed** it as the only picture: FLAC PICTURE type 3 "Cover (front)", MP3 APIC, or M4A `covr`.
4. Also write **`cover.jpg`** (same image, or 500×500 for nicer PictureFlow slides) into each album folder. That covers PictureFlow and older builds, and costs about one file per album.
5. Remove the source's original embedded pictures (step 4.1.4). Messy files often carry 1–5 MB of art per *track*, which wastes space and slows tag parsing on the iPod.
6. 🔬 Tune the size after checking the theme's art viewport on the 320×240 screen.

---

## 6. Tooling options

| Job | Python | .NET |
|---|---|---|
| Plex API | python-plexapi 4.18.x | HttpClient wrapper, or LukeHagar.PlexAPI.SDK (pre-1.0) |
| Tag read/write (FLAC, ID3, MP4, pictures) | **mutagen** (the reference implementation) | **TagLibSharp** |
| Hi-res conversion / FLAC re-encode | ffmpeg (soxr) or SoX; `flac` CLI | the same CLIs, run as processes |
| Art | Pillow | ImageSharp |
| Check tags as Rockbox sees them | `rockbox-metadata` (Rust) via a small CLI wrapper | same |

All of these run on Windows.

---

## 7. Things to test on the real iPod early (spike list)
1. Convert one 24/96 album with the commands in §3.2: check it plays gaplessly, sounds right, and that the tags and art show.
2. FLAC with embedded 300×300 baseline art: does it show on the WPS screen in your Rockbox build? Try a 1000×1000 one to find the size limit.
3. Duplicate `ARTIST` tags: confirm first-wins in the database view (a sanity check of the parser reading).
4. ReplayGain: compare Plex `gain` values against `rsgain` output for the same album.
5. Export Modifications: capture the real `database_changelog.txt` line format for the stats return path.

## 8. Open decisions
- Genre: first Plex genre only, or all genres joined?
- Use Plex's ReplayGain values, compute locally, or skip?
- ~~Sync scope rules~~ **Decided:** All songs / playlists / artists / albums multi-select. See [Selection & Playlists](RockPlex-Selection-and-Playlists.md).

---

### Sources
- Rockbox: [parse_tag() in metadata_common.c](https://git.lenord.me/devs/foxbox/src/commit/d835b7b51f361ef0c0b8f54cc4a7b9ddfc2edf72/apps/metadata/metadata_common.c) · [FLAC embedded art commit](https://git.lenord.me/devs/foxbox/commit/f633d5ed482848826b6b081078be0414a5aaa78f) · [Ogg/Opus embedded art commit](https://git.lenord.me/devs/foxbox/commit/0847bcc110eaa8f2c8a70c0b85f98788969e1e14?files=apps) · [file_formats.tex history (FLAC MAX_BLOCKSIZE, Sept 2025)](https://git.lenord.me/devs/foxbox/commits/branch/master/manual/appendix/file_formats.tex) · [ipodvideo.h (HW_SAMPR_CAPS)](https://git.lenord.me/devs/foxbox/src/commit/703db94de695c5efc047bbfe8e709550888b9136/firmware/export/config/ipodvideo.h) · [pcm_sampr.h](https://git.lenord.me/devs/foxbox/src/branch/master/firmware/export/pcm_sampr.h) · [Album art appendix](https://git.lenord.me/len0rd/rockbox/src/branch/master/manual/appendix/album_art_info.tex) · [rockbox-metadata crate](https://docs.rs/rockbox-metadata)
- Plex: [python-plexapi media (AudioStream gain/peak, LyricStream, MediaPart)](https://python-plexapi.readthedocs.io/en/latest/modules/media.html) · [python-plexapi audio (Album/Track fields)](https://python-plexapi.readthedocs.io/en/stable/modules/audio.html) · [Forum: Gain and Album Gain](https://forums.plex.tv/t/gain-and-album-gain-always-the-same/537331) · [Symfonium: Plex loudness normalisation](https://support.symfonium.app/t/loudness-normalisation-for-plex/9016)
