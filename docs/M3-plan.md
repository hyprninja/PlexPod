# Milestone 3: Sync Engine

**Goal:** the **Sync** button makes the iPod's RockPlex-managed content exactly match the selection: new tracks added, changed ones re-tagged or replaced, unselected ones removed, and every selected playlist written as a `.m3u8` that is **never partial and never broken**. It's safe to unplug at any moment, and it never touches a file RockPlex didn't create.

Design sources: `../RockPlex-Selection-and-Playlists.md` (§3 playlist guarantees P1–P10, §4 sync order, §5 fit check, §6 saving the selection) and the invariants in `../CLAUDE.md`. The media pipeline from M2 produces every file; M3 decides *what* to produce and puts it on the device safely.

---

## 1. Solution changes

```
src/
  RockPlex.Core/     + Manifest model, SyncPlanner (pure diff), FitCheck, PlaylistWriter (m3u8 text), PathAllocator
  RockPlex.Device/   + DeviceStore (manifest/device/selection files, atomic writes), DeviceFileOps (temp+rename, verify), PlaylistVerifier
  RockPlex.Media/    + SyncEngine (orchestrates pipeline → device), Adoption (recognise RockPlex files already on the iPod)
  RockPlex.App/      Sync button, plan/fit dialog, progress + safe stop, sync report
tests/               + planner/fit/playlist/path tests (pure), device tests on temp dirs, engine tests with fake Plex + temp "iPod"
```
Core stays pure (no I/O). All device I/O goes through `RockPlex.Device`.

## 2. On-device state: `/.rockplex/`

| File | Contents |
|---|---|
| `device.json` | A stable device ID (GUID, created on first sync), plus the label and Rockbox target seen. Selections are stored per device ID on the PC. |
| `manifest.json` | **Every file and folder RockPlex owns**, and nothing else. Tracks: device path (exact case), Plex `ratingKey` + `guid` + `partId` + `updatedAt`, size, SHA-256, tag hash, format action, state (`Committed` / `Pending`). Playlists: Plex `ratingKey`, device path, title, content hash. Folders RockPlex created. |
| `manifest.prev.json` | The previous manifest, kept for recovery. |
| `selection.json` | Mirror of the selection (§6 of the selection doc), so the iPod carries its profile to any PC. |
| `last-sync.txt` | Human-readable report of the last sync. |

- Every write is **temp file + rename**, and the previous manifest is rotated to `manifest.prev.json` first. A corrupt manifest falls back to `.prev`, then to **rebuild from the device** (§7).
- The manifest is **journaled during a sync**: a track is recorded as `Pending` before its copy starts and flipped to `Committed` after verification. The manifest is flushed every few files and at every phase boundary, so an interrupted sync resumes with nothing lost or orphaned.

## 3. Planning (pure, `SyncPlanner`)

Input: the resolved selection (M1), the current manifest, Plex metadata for the selected tracks, the `TagPolicy` and the device paths. Output: a `SyncPlan` with:

| Action | When | Work |
|---|---|---|
| **Add** | Track selected, not in the manifest | Pipeline → copy |
| **Replace** | Same `ratingKey`, but `partId` or the part's `updatedAt` changed (a new file in Plex) | Pipeline → copy over (temp + rename) |
| **Re-tag** | Same audio, but the tag hash changed (Plex edit or policy change) | Pipeline (re-download; the conversion cache covers hi-res) → copy over |
| **Move** | Its Plex-derived path changed (album renamed, artist re-matched) | Rename on the device (no re-copy), then re-tag if needed |
| **Keep** | Nothing changed | Nothing |
| **Remove** | In the manifest, no longer referenced by anything selected | Delete **last** |
| **Playlist write** | Every selected playlist, every sync (smart playlists refresh, P9) | Write if the content hash changed |
| **Playlist remove** | A RockPlex playlist no longer selected, or deleted in Plex | Delete with the removals |

- **Paths (`PathAllocator`):** a track keeps its manifest path for life unless its Plex-derived path changes. New paths come from M2's `DevicePathBuilder`. Collisions are checked case-insensitively (FAT32), against both manifest paths and *any existing file on the device*: a user's own file is never overwritten, and the new file gets ` (2)`.
- **Playlist file names:** the FAT32-safe Plex title + `.m3u8`. A name already taken by a playlist that isn't RockPlex's gets ` (RockPlex)` (P7). Playlists are tracked by Plex `ratingKey`, so a rename in Plex renames the file (P8).

## 4. Fit check (pure, `FitCheck`) — the gate before anything is written

- Bytes needed = Σ(estimated size of Add/Replace/Re-tag outputs) − Σ(current size of files they replace). Bytes freed = Σ(Remove sizes).
- Estimates use M2's real cached sizes where known, else the conservative estimate (never under).
- **Over capacity → blocked before any write**, with the overage and the biggest contributors, so you can untick something. Playlists are never trimmed (P1).
- Because removals happen **last**, the adds must also fit in the free space *before* the removals run. When they only fit after removals, see decision **D5**.

## 5. Executing a sync (`SyncEngine`), in the mandated order

0. **Pre-flight:** device present, its ID matches, the manifest loads (or recovers), free space re-read, fit check passes. *(Stats collection, step 0 of the design, arrives in M4; the hook sits here, before anything is changed.)*
1. **Tracks — add, replace, re-tag, move.** For each item: the pipeline produces the file on the PC (M2) → copy to `<path>.rockplex-tmp` on the iPod → flush → **verify** (decision D6) → rename into place → manifest entry `Committed`. Downloads and conversions run ahead in parallel; device writes are one at a time (spinning HDD).
2. **Playlists.** Built from the manifest's exact paths, in Plex order, duplicates kept (P2/P3), unavailable Plex entries skipped and reported (never written as broken lines). Each is written as temp + rename into `/Playlists/` (P4/P5).
3. **Playlist check (P6).** Every RockPlex `.m3u8` is re-read from the iPod and **every line resolved against the real files with exact case**. Any miss fails the sync loudly, names the playlist and line, and **stops before removals**.
4. **Removals, last (P5).** Delete unreferenced tracks and stale RockPlex playlists, then RockPlex-created folders that are now empty. Only paths in the manifest are ever deleted.
5. **Commit.** Write the manifest, `selection.json` and `last-sync.txt`, and remind you to run *Database → Update Now* (or rely on Auto Update).

### 5a. Make-room mode (D5)
Used **only** when the fit check says the adds fit after removals but not before. The order still guarantees no broken playlist line at any instant:
1. Delete every RockPlex playlist that will change or go away. Every RockPlex playlist left on the iPod then references only tracks that are being **kept**, because a selected playlist pulls in all its tracks.
2. Delete the tracks being removed (nothing left on the device references them).
3. Copy and verify tracks (step 1 above), write playlists (step 2), run the line check (step 3).

The summary dialog says when make-room mode will be used and which playlists are briefly absent. An interruption between 1 and 3 leaves those playlists absent until the next sync, never pointing at missing files.

- **Stop / unplug safety:** *Stop* finishes the current file and stops cleanly. An unplug or crash leaves at most one `.rockplex-tmp` file, recorded as `Pending`, which the next sync deletes. No playlist can point at a missing file, because playlists are written only after their tracks are verified and removals only run after the playlist check.
- **Re-plug detection:** if the device disappears, the sync aborts and the next sync resumes from the journal.

## 6. App

- **Sync** is enabled when an iPod with Rockbox is connected, a library is loaded and the selection fits.
- Clicking it builds the plan and shows a **summary dialog**: *to add N (x GB) · re-tag N · move N · remove N (y GB) · playlists N · free after: z GB*, the biggest items, and any unavailable playlist entries. Then **Start sync** or Cancel.
- During the sync: an overall bar, the current phase and file, MB/s, and **Stop**. Afterwards: the result plus a link to the report.
- Selections are kept **per device** (by device ID) on the PC and mirrored to `/.rockplex/selection.json`. Plugging in an iPod with a selection the PC doesn't know offers to load it.

## 7. Recovery and adoption

- **Rebuild manifest from device:** scan `/Music` for files carrying `ROCKPLEX_RATINGKEY` / `ROCKPLEX_PARTID` tags (written by M2 on every file) and `/Playlists` for `#ROCKPLEX ratingKey=` headers, then rebuild the manifest. Used when the manifest is missing or corrupt, and for adopting the files from the **manual test export** already on your iPod (decision D7).
- Adoption only claims files RockPlex demonstrably made (its ID tags or header line). Anything else stays the user's.
- An adopted track whose `ROCKPLEX_PARTID` matches the current Plex part is kept if its tags hash to what the current `TagPolicy` would write; otherwise it is re-tagged. One whose part changed is replaced. Unselected adopted tracks are removed like any other RockPlex file. Adopted test playlists are rewritten with the real header and guarantees.
- Adopted files are checked to parse (tags and audio header) and recorded with their SHA-256. The full read-back (D6) applies to every file RockPlex copies from then on.

## 8. Tests

- **Pure:** the planner (every action, including move vs re-tag vs replace and duplicate tracks shared by playlists); path allocation (case-insensitive collisions, user files never claimed, stable paths); fit check (before/after removals, conservative unknowns); the playlist writer (order, duplicates, UTF-8, `#ROCKPLEX` header, skipped entries, never a broken line).
- **Device (temp-dir "iPods"):** atomic manifest writes and recovery from `.prev`; temp + rename copies; the P6 line check catching a wrong-case path and a missing file; removals touching only manifest paths, with a planted user file and user playlist surviving every scenario.
- **Engine (fake Plex + real pipeline + temp iPod):** first sync; incremental (re-tag only, move only, replace); deselect → removal; smart-playlist refresh; **interrupt at every phase** (simulated unplug after N bytes) → the next sync completes and every playlist resolves; over capacity → nothing written; adoption of a test export.

## 9. Acceptance criteria

1. Clean build, zero warnings; all tests green.
2. First sync of a real selection: every selected track and playlist is present, every playlist line resolves on the iPod, the tags and art are as in M2, and the database updates cleanly on the device.
3. A second sync with no changes does nothing (no copies) and finishes in seconds.
4. Edit a title in Plex → only that file is re-tagged; untick an album → only its files are removed, after the playlists are written.
5. Pulling the cable mid-sync, then re-syncing → completes, no orphans, no broken playlist lines.
6. Your own files and playlists on the iPod are untouched in every scenario.
7. Over capacity → blocked with a clear report; nothing is written.

## 10. Owner decisions

| # | Question | Decision |
|---|---|---|
| D5 | Adds don't fit until removals run | **Make room first, only when needed** (§5a). Affected playlists are briefly *absent*, never broken. |
| D6 | Verifying each file after copying to the iPod | **Full read-back**: every file is re-read from the iPod and its SHA-256 compared with the PC copy before it is renamed into place. |
| D7 | The test-export files already on the iPod | **Adopt** files and playlists that carry RockPlex's ID tags or header (§7); keep the ones still selected, and treat the rest as RockPlex-owned removals. |

## 11. Out of scope for M3
Stats return (M4: playback log / scrobbler / changelog → Plex). Auto-sync on plug-in and the tray (M5). Writing Rockbox settings (`config.cfg` is the user's). Lossy overflow profiles.
