# Milestone 6: Stock Firmware — Two Workflows, Detected Automatically

**Status:** implemented and **verified by the owner on 18 Oct 2026**: a playlist synced to the iPod 5.5G on iPod OS 1.3 played in the car first try. Owner decisions D12–D15 in §11. Still to observe on the real device: covers, plays and ratings coming back through Play Counts, and a larger library.

**Goal:** PlexPod syncs the same Plex selection to an iPod running **either** Rockbox **or** Apple's
stock firmware, works out which one is plugged in, and runs the matching workflow. The stock
firmware is what the car needs: the head unit speaks Apple's accessory protocol, which Rockbox
doesn't implement on this iPod.

**Why it's tractable:** nothing here emulates a protocol. In disk mode the 5.5G is plain USB mass
storage, and "being iTunes" means writing the right files to FAT32: audio files, plus the
database the firmware reads. PlexPod already writes verified files to that disk; this milestone
adds the database.

Design sources: libgpod (`itdb_itunesdb.c`, `itdb_artwork.c`, `README.overview`), the wikiPodLinux
iTunesDB pages, gtkpod's notes, and the invariants in `../CLAUDE.md`. Status markers: ✅ verified
from a primary source or the real device · ⚠️ documented, not yet held against this iPod · 🔬 must
be tried on the device.

---

## 1. Phase 0: what the real device showed (18 Oct 2026) ✅

- The owner restored the iPod to **iPod OS 1.3**. It plays in the car again: **the car works with
  stock firmware**, so this milestone has a customer.
- Plugged in, Windows sees `USBSTOR\DISK&VEN_APPLE&PROD_IPOD`, model "Apple iPod USB Device",
  80 GB disk, FAT32 volume `IPOD` (74.4 GB).
- `iPod_Control/` holds `Device/` (`SysInfo` **empty**, `Preferences`, `clock`, `radio`),
  `iTunes/` (`iTunesPrefs` 36 bytes, `iTunesControl` 75 MB of zeros) and an empty `Music/`.
  **There is no `iTunesDB` at all**: the firmware runs happily without one and shows an empty
  library. So there's no Apple-written database to use as a fixture; fixtures come from libgpod's
  test data and the documented layouts, and the first synced database is checked on the iPod's
  screen before the car sees it.
- The restore **did not reformat**: RockPlex's `/.rockplex/` (manifest of ~870 tracks) and
  `/Playlists/*.m3u8` survived, but `/Music/` is gone. Those leftovers are handled in §6.
- **iTunes is installed on this PC** (the iPod Service is running). iTunes treats an iPod whose
  database it didn't write as "synced with another library" and offers to erase it. See §7.

## 2. Detecting which workflow to run

A device is classified on plug-in, read-only, from what's on the disk (as built):

- `/.rockbox/` present → **Rockbox** workflow, exactly as before M6.
- `iPod_Control/iTunes` and `iPod_Control/Device` present, no `/.rockbox/` → **Stock** workflow.

Rockbox installs alongside Apple's firmware and keeps `iPod_Control/`, so the disk alone can't tell
a Rockbox iPod from a dual-boot one running Apple's firmware; `/.rockbox/` decides, so a Rockbox iPod
is never given an Apple database by mistake. (The USB identity, `VEN_APPLE&PROD_IPOD` in Apple's disk
mode ✅, could tell which firmware is *running* on a dual-boot iPod; that choice is left for later.)
When the firmware changes, files PlexPod wrote for the other one are removed, last, like unselected
tracks.

The device panel shows the result ("iPod (Apple firmware)" / "iPod (Rockbox)"), and the sync,
plan dialog, report and Settings show only what applies to that target.

## 3. What both workflows share (unchanged)

Plex sign-in, discovery, library, pickers, selection and capacity; the media pipeline (download →
decide → convert → tag → verified copy); `DeviceFileOps` (write-through, unbuffered read-back
SHA-256, rename into place); `TagWriter`; the manifest at `/.plexpod/` with ownership, hashes,
reconciliation, adoption and recovery; the sync order (stats → tracks → playlists → verify →
removals last); the fit check; never upsample; only touch files PlexPod owns. Each target keeps
its own selection on the device, as `selection.json` does today.

## 4. The stock workflow

### 4.1 Files
- **Layout:** the firmware's own `iPod_Control/Music/F00`…`F49` with short generated names
  (`ABCD.m4a`), spread across the folders as iTunes does ✅. The manifest keeps the human
  artist/album/title for every file. (Pointing the database at `/Music/Artist/Album/…` might also
  work ⚠️🔬, but the firmware was built to read `Fxx`, so that's the safe default.)
- **Formats the firmware plays:** MP3, AAC and Apple Lossless in `.m4a`, WAV, AIFF ✅. **No FLAC.**
  FLAC sources are converted (decision **D12**); the 16-bit/44.1 kHz ceiling and never-upsample
  rule are unchanged. MP3 and AAC are copied untouched, as today.

### 4.2 The database (`iPod_Control/iTunes/iTunesDB`)
- A little-endian tree: `mhbd` → `mhsd` sections → track list (`mhlt` → `mhit` per track with
  `mhod` UTF-16 strings) and playlist list (`mhlp` → `mhyp` → `mhip` per entry) ✅.
- **No signature on this model** (hash58 started with the 2007 Classic and Nano 3G) ✅⚠️.
- The **master playlist** comes first, is hidden, and lists every track ✅. libgpod also writes
  **sorted index records** (`mhod` 52/53) into it; the firmware's Artists/Albums/Songs menus, which
  the car browses through, depend on them ⚠️.
- Track paths are `mhod` 100 in colon form: `:iPod_Control:Music:F03:ABCD.m4a` ✅.
- **Gapless playback** reads the track's database fields (sample count, encoder delay and padding,
  gapless flags), not the file's `iTunSMPB` atom; PlexPod fills them from the file ⚠️.
- Written as a **full rebuild every sync** from the manifest and the Plex snapshot, never edited
  in place: built to a temp file, read back and parsed, then renamed over the old one. The firmware
  never sees a half-written database.
- **Each rebuild carries the iPod's own play counts and ratings forward** (and Plex ratings for
  tracks rated in Plex), or every sync would reset them on the iPod and in the car.
- Acceptance standard: the parser reads back exactly what the writer wrote, field for field.

### 4.3 Play counts and ratings back to Plex (replaces M4's source for this target)
- The firmware records plays and ratings in `iPod_Control/iTunes/Play Counts` (firmware ≥ 1.3) ✅.
  Each entry lines up with a track **by its position in the database on the iPod**, so it's read
  against that database before any rebuild ✅.
- Per track it holds: plays since the file was created, the **last** played time (seconds since
  1904, iPod local time), skips and the rating (stars × 20) ✅. It has **no time for each play**,
  only the last one, so M5's Last.fm forwarding can send real times only for single plays.
- The firmware keeps adding to the same file until a computer consumes it; iTunes deletes it after
  merging. PlexPod does the same once Plex has accepted everything (decision **D14**), otherwise
  the next sync would count those plays again.
- gtkpod notes the file may vanish if the iPod resets ⚠️, so it is read first thing in every sync.
- Sending reuses M4's Plex side (`ScrobbleAsync`, `RateAsync`, never retried, at-least-once
  with progress saved), its ratings setting (D10) and its PC history.

### 4.4 Artwork
The stock firmware ignores embedded art and `cover.jpg`. It reads `iPod_Control/Artwork/ArtworkDB`
plus `.ithmb` files of raw RGB565 thumbnails; for the iPod Video those are 100×100 and 200×200
cover sizes ⚠️ (libgpod's per-model table) 🔬. Same download and decode as today, new encoder.
Timing is decision **D15**.

### 4.5 Order of a stock sync
Read `Play Counts` against the current database → send plays and ratings to Plex → copy and verify
new and changed tracks → build the new database (with carried-forward stats), verify it, rename it
into place → delete `Play Counts` (D14) → remove files no longer selected (last) → artwork → commit
the manifest.

## 5. The Rockbox workflow
Unchanged from M3/M4: `/Music/Artist/Album`, FLAC kept (≤ 16/44.1), `.m3u8` playlists, Rockbox's
playback log and changelog for stats.

## 6. This iPod's Rockbox-era leftovers
`/.rockplex/manifest.json` lists ~870 tracks whose files are gone, and four `/Playlists/*.m3u8`
point at them. The existing reconciliation already drops manifest entries whose files are missing.
Decision **D13** covers whether the first stock sync also removes the dead playlists and retires
the old manifest.

## 7. iTunes on the same PC
iTunes must never sync this iPod again: it would replace PlexPod's database with its own (or an
empty one). The app warns when the iPod Service is running, and the first stock sync explains:
don't open iTunes with the iPod plugged in, or set it to *Manually manage music*. PlexPod never
touches iTunes itself.

## 8. Build order

| Phase | What | Notes |
|---|---|---|
| 1 | Detection + target model ✅ | `IpodLocator` reports the firmware; the app shows it; Rockbox path unchanged |
| 2 | `Core/ItunesDb` writer + parser ✅ | Pure; libgpod's layout (version 0x30, 8 sections, sort indexes); reader checked against a real iTunes-written database and libgpod's parser offsets |
| 3 | Stock sync engine ✅ | `iPod_Control/Music/Fnn`, Apple Lossless verified sample-for-sample (D12), database rebuilt from the manifest and swapped in, never listing a file being removed, P1 via the previous version, leftovers removed (D13) |
| 4 | First real sync ✅ | A playlist synced and played in the car first try (18 Oct 2026) |
| 5 | Play Counts → Plex ✅ | Built with phase 3 (it has to be: Play Counts is tied to the database being replaced). Matched by position, folded into the manifest, pending plays kept, file deleted (D14) |
| 6 | Artwork ✅ | ArtworkDB + `F1028`/`F1029` RGB565 thumbnails, per track; rewritten to the other file index only when covers change |

## 9. Tests
- **ItunesDb:** write → parse gives back every field; record sizes and child counts consistent;
  every playlist member in the master playlist; colon paths both ways; UTF-16 edge cases
  (accents, CJK, emoji); a malformed database is rejected, not half-read.
- **Engine (temp-folder iPod):** first sync, re-sync with no changes (database byte-identical),
  add/remove/rename in Plex, playlists with duplicates and unavailable entries, a kill between
  the temp database and the rename leaves the old one intact, leftovers cleaned per D13, the
  user's own files untouched.
- **Detection:** stock, Rockbox, dual-boot, and an unknown USB identity.
- **Play Counts:** fixture → plays and ratings sent once; file missing → nothing to send, no
  error; counts and ratings carried into the next database.
- **Artwork:** thumbnail bytes for a known image compared with a reference checked on the device.

## 10. Acceptance criteria
1. Clean build, zero warnings, all tests green.
2. Plugging in a stock iPod picks the stock workflow, and a Rockbox iPod the Rockbox one, with no
   setting to change.
3. After a stock sync, artists, albums, songs and playlists show on the iPod and in the car and
   play, with the car's controls working.
4. Re-syncing never shows the firmware a broken database, including after an unplug mid-sync.
5. Plays and ratings made on the iPod or in the car reach Plex after the next sync, once each.
6. Only files PlexPod owns, plus its database files in `iPod_Control/iTunes/` and
   `iPod_Control/Artwork/`, are ever written or deleted.
7. The Rockbox workflow is unchanged.

## 11. Owner decisions

| # | Question | Decision |
|---|---|---|
| D12 | What FLAC becomes on the stock firmware | **Apple Lossless** (`.m4a`, `-c:a alac`): lossless, about FLAC's size. The 16-bit/44.1 kHz ceiling and never-upsample rule apply as before; MP3 and AAC are still copied untouched. |
| D13 | Rockbox-era leftovers on this iPod | **Clean them up** on the first stock sync: the dead `/Playlists/*.m3u8` listed in the old manifest are removed (PlexPod owns them), and the stock library starts from a fresh manifest. Nothing outside the old manifest is touched. |
| D14 | `Play Counts` after its plays reach Plex | **Deleted, as iTunes does**, once every play and rating in it has been accepted by Plex, and only after the counts and ratings are carried into the new database. |
| D15 | When artwork lands | **In M6, as the last phase** (after music, playlists and stats work in the car). |

## 12. Out of scope
Any accessory-protocol (iAP) work; the iPod Classic 6G/7G and nano 3G+ (signed databases); iOS
devices; video, photos, podcasts, audiobooks, contacts, calendars and notes; changing Rockbox or
the firmware.
