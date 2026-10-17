# RockPlex: Sync Selection & Playlists Design

*Draft 1, 18 Oct 2026. Companion to the [Research Brief](RockPlex-Research-Brief.md) and the [Media Pipeline](RockPlex-Media-Pipeline.md).*

Status tags: ✅ confirmed from a primary source · ⚠️ inferred, needs checking · 🔬 needs a test on the real iPod or server

---

## 1. Requirements (as given)

| # | Requirement |
|---|---|
| S1 | **Playlists syncing to the iPod is mission-critical.** |
| S2 | Selection options: **All songs**, multi-select **Playlists**, multi-select **Artists**, multi-select **Albums**. |

---

## 2. Selection model

### 2.1 What you pick
| Picker | Contents | Shown per item |
|---|---|---|
| **All songs** (toggle) | Every track in the chosen Plex music library | Total size vs iPod capacity |
| **Playlists** (multi-select) | Plex **audio** playlists, both regular and smart | Track count, duration, size on the iPod, a "smart" badge |
| **Artists** (multi-select, searchable) | Plex artists (album-artist level) | Album count, size |
| **Albums** (multi-select, searchable, grouped by artist) | Plex albums | Track count, size, a "hi-res → converted" badge |

- A running **capacity bar** (selected vs about 73 GiB usable) updates live as you tick items.
- When **All songs** is on, the Artist and Album pickers are greyed out because they're redundant. **Playlists stay selectable.** In that mode the playlist picker decides which playlist files get written; the tracks are already covered.

### 2.2 How a selection becomes the iPod's contents
```
tracks on iPod = AllSongs ? every track in the library
               : ⋃ tracks(selected playlists)
               ∪ ⋃ tracks(selected artists)
               ∪ ⋃ tracks(selected albums)
```
- **Union, deduplicated by Plex `ratingKey`.** Each track is stored **once**, however many playlists or picks include it. Ticking an album *and* its artist costs nothing extra.
- **Selections are live rules, not snapshots.** They're saved as IDs and worked out again on every sync:
  - **Artist** = all of that artist's albums **now and in future** (new albums added in Plex flow to the iPod at the next sync).
  - **Album** = all of its tracks.
  - **Playlist** = its contents **as of this sync**. Edits in Plex and smart-playlist changes follow automatically.
- **Artist scope:** by default, albums where they're the **album artist** (Plex's artist → album hierarchy). ⚠️ An optional later setting could add "appears on" tracks (their tracks on Various Artists compilations, matched by the track-artist field `originalTitle`).

### 2.3 Fetching it efficiently (works the same on large libraries)
1. **One paginated pull of every track** in the section: `GET /library/sections/{id}/all?type=10` with `X-Plex-Container-Start/Size` paging. Slim it with `includeFields` to keys, `parentRatingKey` (album), `grandparentRatingKey` (artist), `updatedAt`, and `Media/Part` (`id`, `size`, `container`) plus the audio stream's `bitDepth`/`samplingRate`.
2. **Artist and album picks are resolved in memory** from that pull (filter on `grandparentRatingKey` or `parentRatingKey`). No extra API calls per artist or album.
3. **Playlists:** `GET /playlists` (filtered to audio ⚠️ `playlistType=audio`), then `GET {playlist.key}/items` for each selected playlist ✅. python-plexapi uses the same `items` call for regular and smart playlists. A playlist track from a *different* library section, not in the pull, is fetched individually through `/library/metadata/{ratingKey}`.
4. Full metadata (for tagging) is fetched only for tracks that are **new or changed** since the last sync.

---

## 3. Playlist guarantees (S1: mission-critical)

| # | Guarantee | How |
|---|---|---|
| **P1** | **No broken entries, ever.** Every track in a selected playlist is on the iPod. | Selecting a playlist **pulls its tracks into the sync set automatically**. A playlist is never partially copied; if it doesn't fit, the sync is blocked before anything is written (§5). |
| **P2** | **Order and duplicates kept exactly** as in Plex. | Items are written in Plex order; repeated tracks stay repeated. Plex tracks each entry separately (`playlistItemID`) ✅. |
| **P3** | **Rockbox finds and plays every line.** | UTF-8 **`.m3u8`** ✅, **absolute paths** `/Music/...` ✅ (used exactly as written by Rockbox), path strings taken **from the manifest** so upper/lower case matches the files byte-for-byte (⚠️ case-sensitive). |
| **P4** | **Playlists appear in Rockbox's Playlist Catalogue.** | Written to **`/Playlists/`**, the catalogue's default folder ✅. |
| **P5** | **A pulled cable or crash can't leave a playlist pointing at missing files.** | Order of operations (§4): copy and verify tracks → write playlists (temp file, then rename) → **only then** delete tracks no longer referenced. |
| **P6** | **Checked after writing.** | After the sync, every `.m3u8` is re-read from the iPod and **each line is resolved against the actual files**, with exact case. Any miss fails the sync loudly and names the playlist and line. |
| **P7** | **Your own iPod playlists are never touched.** | RockPlex only manages playlists listed in its manifest; each one carries a `#ROCKPLEX` header line too. If one of yours has the same name, RockPlex's copy gets a suffix. |
| **P8** | **Renames and deletions in Plex carry over.** | Playlists are tracked by Plex `ratingKey`, not name. A rename replaces the file; deleting or unticking a playlist removes the file (and any tracks nothing else needs). |
| **P9** | **Smart playlists work.** | Saved as a snapshot of the list worked out at sync time and refreshed every sync. ⚠️ Confirm the `items` call returns the evaluated list for smart playlists (expected; python-plexapi uses it unchanged). 🔬 |
| **P10** | **Playlists work immediately.** | Rockbox plays playlists straight from file paths; they don't need its track database, so they work before *Database → Update Now* finishes. |

**Edge cases:**
- A Plex playlist entry whose file is **unavailable on the server** (deleted or offline) is skipped and listed in the sync report. There's nothing to copy.
- **Non-music items** in a playlist are ignored.
- A **hi-res FLAC** in a playlist is converted as usual (Media Pipeline §3.2); the playlist points at the converted file.
- Playlists are **one-way (Plex → iPod)**. Edits made to a RockPlex playlist on the iPod get overwritten at the next sync. (Two-way playlist sync is possible later if wanted.)
- ⚠️ Rockbox has a **Max Playlist Size** setting (System → Limits). The default is believed to be far above what 80 GB of FLAC holds (about 3,000 tracks), but RockPlex checks the largest playlist against it and warns. 🔬

**File format written** (Rockbox skips `#` lines ✅; other players use the `#EXTINF` info):
```
#EXTM3U
#ROCKPLEX ratingKey=48211 synced=2026-10-18T14:02:11+11:00
#EXTINF:412,Artist One - Track Title
/Music/Artist One/2002 - Album Name/03 Track Title.flac
#EXTINF:356,Artist Two - Another Track
/Music/Artist Two/2013 - Other Album/1-03 Another Track.flac
```
Filename: the Plex playlist title made FAT32-safe, plus `.m3u8`. Duplicate titles get ` (2)`.

---

## 4. Sync order (mirror semantics, safe if interrupted)

The iPod's RockPlex-managed content = **exactly** the current selection. A track is removed only when **nothing** references it any more (All songs, any playlist, artist or album).

0. **Collect stats first:** read the playback/scrobbler logs and the exported database changelog, and send plays and ratings to Plex (Brief §2.8). This happens *before* any deletion, so plays of tracks about to be removed aren't lost.
1. **Plan:** work out the selection → target track set; compare it against the manifest → add / re-tag / remove lists; run the fit check (§5).
2. **Add:** download → convert if needed → clean and tag → embed art → write to a temp name → verify → rename. Also write `cover.jpg` files.
3. **Re-tag:** tracks whose Plex metadata changed are rewritten in place (no audio re-copy).
4. **Write playlists** (P5): temp file, then rename; then run the line-by-line check (P6).
5. **Remove** unreferenced tracks and stale playlists, then empty album and artist folders.
6. **Commit the manifest** (`/.rockplex/manifest`) and the selection (§6).
7. Remind you to run *Database → Update Now*, or rely on *Auto Update*.

Files that aren't in the manifest (anything you put on the iPod yourself) are **never** modified or deleted.

---

## 5. Fit check (no lossy transcoding, so the selection has to fit)

- Sizes come from Plex `Part.size`. Hi-res FLAC uses an **estimate after conversion**: ≈ `size × (16/bits) × (44100/rate)`, corrected from real results once files have been converted.
- The sync report shows: **total selected · already on the iPod · to add · to remove · free space after sync**, plus the biggest items.
- **Over capacity = the sync is blocked before anything is written.** The report shows how much is over and the largest contributors so you can untick something. Playlists are never trimmed to fit (P1).
- Expect **All songs** on a mostly-FLAC library to go over 80 GB (about 210–290 albums of 16/44.1 FLAC fit). The capacity bar shows this the moment it's ticked.
- *Possible later:* priority auto-fill (playlists → albums → artists).

---

## 6. Saving the selection

- Selections are stored as Plex **`ratingKey` + `guid` + title**. The `guid` lets a pick survive Plex reassigning `ratingKey`s (after a re-match or library rebuild); the title is for display and debugging.
- They're saved per device on the PC **and** mirrored to **`/.rockplex/selection.json` on the iPod**, so the iPod carries its own profile to any PC running RockPlex.
- The device gets an ID in `/.rockplex/device.json` (useful if you ever have more than one iPod).

```json
{
  "library": { "sectionId": 3, "uuid": "…" },
  "allSongs": false,
  "playlists": [ { "ratingKey": 48211, "guid": "com.plexapp.agents.none://…", "title": "Road Trip" } ],
  "artists":   [ { "ratingKey": 1021,  "guid": "plex://artist/…", "title": "Artist One" } ],
  "albums":    [ { "ratingKey": 5530,  "guid": "plex://album/…",  "title": "Album Name" } ]
}
```

---

## 7. Tests to run early 🔬
1. A smart playlist: confirm the `items` call returns the evaluated tracks in the expected order.
2. A playlist with the same track twice: confirm both entries survive to the `.m3u8` and play on the iPod.
3. Pull the USB cable mid-sync: confirm every playlist on the iPod still resolves (P5/P6).
4. A 1,000+ entry playlist: confirm it loads in Rockbox within the Max Playlist Size setting.
5. A non-ASCII playlist name and paths (e.g. "Sigur Rós", "Mötley Crüe"): confirm the UTF-8 `.m3u8` paths resolve on FAT32.

---

### Sources
- [python-plexapi Playlist docs](https://python-plexapi.readthedocs.io/en/latest/modules/playlist.html) · [python-plexapi playlist source (`items`, `playlistItemID`)](https://python-plexapi.readthedocs.io/en/latest/_modules/plexapi/playlist.html)
- [Rockbox manual: Working with playlists (catalogue at `/Playlists`)](https://git.lenord.me/devs/foxbox/src/commit/2d8e0f7c907ee0d50e6e55fc9ed5f94a43ab5d67/manual/working_with_playlists/main.tex) · [Rockbox playlist.c (.m3u8, path handling, `#` lines)](https://git.lenord.me/devs/foxbox/src/branch/master/apps/playlist.c)
- [plex-ipod-sync (case-sensitive playlist paths note)](https://github.com/undergroundpost/plex-ipod-sync)
