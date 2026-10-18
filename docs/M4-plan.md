# Milestone 4: Stats Return (iPod → Plex)

**Status:** implemented (343 tests green); owner verification on the real iPod and Plex server pending.

**Goal:** what you play and rate on the iPod shows up in Plex. Every sync starts by reading the iPod's listening history, matching each play to its Plex track exactly (through RockPlex's manifest, not by guessing from tags), and sending the plays and rating changes to Plex. This happens *before* anything on the iPod changes, so plays of tracks about to be removed are never lost.

Design sources: research brief §2.8, selection doc §4 step 0, and the invariants in `../CLAUDE.md` (the only Plex writes ever allowed are play and rating write-back).

---

## 1. What the iPod records (verified from Rockbox 4.0 source)

### 1.1 Plays: the playback log ✅
- Written by Rockbox when **Settings → Playback Settings → Logging** is on. **It is off by default**, so nothing is recorded until you switch it on.
- File: `/.rockbox/playback.log`. Lines starting `#` are headers (`# Started Ver. 4.0 …`). Each play is one line:
  `timestamp:elapsed_ms:length_ms:/Music/Artist/Album/01 Track.flac`
  (`apps/playback.c`, `add_playbacklog`). A line is written for any track played for more than 0.5 s.
- `timestamp` comes from the iPod's clock, which has **no time zone**, so it is read as local time on the PC.
- On hard-disk iPods, lines are **buffered in RAM and written when playback stops** (or when the buffer fills). Plays from a session still running when you plug in may not be on disk yet.
- When the log passes ~512 KB, Rockbox renames it to `/.rockbox/playback_0001.log`, `_0002`, … at start-up and starts a new one. RockPlex reads all of them.

### 1.2 Ratings: the database changelog ✅
- Ratings live in Rockbox's database and only leave it when you choose **Settings → General Settings → Database → Export Modifications**. Rockbox never exports automatically.
- File: `/.rockbox/database_changelog.txt`. First line `## Changelog version 1`, then one line per **modified** track: `tag="value"` pairs (`filename`, `rating`, `playcount`, `lastplayed`, …), with `"` and `\` escaped and newlines as `\n` (`apps/tagcache.c`, `tagcache_create_changelog`).
- It is a **snapshot** (current values), not a list of changes, so RockPlex compares it with what it sent last time.
- Rockbox rating 0–10 = Plex rating 0–10 (Plex shows it as stars ÷ 2).

### 1.3 Plex endpoints ✅ (python-plexapi `markPlayed` / `rate`)
- Play: `GET {server}/:/scrobble?key={ratingKey}&identifier=com.plexapp.plugins.library`. Adds one play and sets *last played* to **now**: Plex has no way to back-date a play, so the original time is kept by RockPlex (§3) for Last.fm later (M5).
- Rating: `PUT {server}/:/rate?key={ratingKey}&identifier=com.plexapp.plugins.library&rating={0–10}` (`-1` clears).

## 2. Matching (exact, never fuzzy)
- A log line's path is the device path RockPlex wrote, so it matches a manifest entry's `DevicePath` directly (case-insensitive, as FAT32 is). That gives the Plex `ratingKey`.
- Plays of files RockPlex doesn't own (your own music on the iPod) are counted and ignored.
- A play only counts if at least **90% of the track** was played (decision **D8**). Skips and partial listens are ignored.
- The same play line is never sent twice: see the bookmark (§3).

## 3. State and history
- **On the iPod**, `/.rockplex/stats.json` (RockPlex's own file): a bookmark per log file (name, bytes already processed, and a SHA-256 of exactly those bytes), plus the last rating sent per track. Logs only grow and a rotated log keeps its content, so a file whose first *n* bytes hash the same is the same log under any name; anything else (a replaced log, even with an identical header) is read from the start. Rockbox's own log files are left in place (**D9**).
- **On the PC**, `%LOCALAPPDATA%\RockPlex\stats\<device id>\`: a copy of every log processed, plus `plays.jsonl` with every play sent (track, real time, elapsed). That's a backup, and the source of real timestamps for Last.fm in M5.
- **Delivery:** plays are sent in order and the bookmark is saved every few plays and after the last one. If Plex is unreachable, sending stops, the bookmark stays before the first unsent play, and the next sync continues from there. A crash between sending a play and saving the bookmark can at worst send that one play twice.

## 4. Ratings
- From the changelog, for each RockPlex track whose rating differs from **what RockPlex last sent** for it, send the new rating, subject to the setting (decision **D10**):
  - *iPod changes win* (default): send it.
  - *Only fill gaps*: send it only if the track has no rating in Plex (Plex's `userRating`, which the track list RockPlex already loads includes, so no extra requests).
- Rockbox stores "never rated" as 0; a 0 that was never anything else is not sent, so unrated tracks don't wipe Plex ratings.
- If the changelog hasn't changed since the last sync, nothing is sent: every rating equals what was last sent.

## 5. Where it runs
- **Step 0 of every sync** (decision **D11**), after preflight and before any change to the iPod. The plan dialog shows *"Will send N plays and M ratings to Plex"*, and the sync report lists what was sent.
- A **Send plays now** action in the app does the same without syncing.
- If Plex is unreachable, the sync still runs; the plays wait for the next one.
- **Hints:** if the iPod has no playback log at all, the app explains how to turn on Logging. If the changelog is older than the newest log, it suggests *Export Modifications*.

## 6. Code
- **Core (pure), `RockPlex.Core/Stats`:** `PlaybackLogParser` (lines, headers, malformed lines skipped, an unfinished last line left for later), `ChangelogParser` (quoted `tag="value"` with escapes), `PlayRule` (≥ 90%, integer arithmetic), `StatsPlanner` (logs + bookmarks + manifest → play steps in order; changelog + last-sent ratings → ratings to send), `LogBookmark` (prefix hash).
- **Device:** `StatsReader` (lists and reads `/.rockbox/playback*.log`, rotated logs in number order then the current one, and the changelog; read-only, shared with Rockbox, size-capped) and `DeviceStore.Load/SaveStatsAsync` (`/.rockplex/stats.json`, atomic writes, sanitised on load).
- **Plex:** `ScrobbleAsync(ratingKey)` and `RateAsync(ratingKey, rating)` on the library client, the only write calls RockPlex makes to Plex, never retried. `TrackItem.UserRating` comes from the track list (for *Only fill gaps*).
- **Media:** `StatsService` (preview → send in order, bookmark saved every 10 plays and in a `finally` → PC archive). `SyncEngine` runs it as step 0, right after preflight, re-reading the iPod at that point so plays sent in between are never sent twice. `SendNowAsync` does the same without a sync.
- **App:** plan dialog line (*First: send N plays and M ratings*), hints, sync report and status line, *Send plays now* button, Settings → *Plays and ratings* (on/off, *Ratings from the iPod*).

## 7. Tests
- **Parsers:** real-format log lines (headers, a path with `:` characters in it, CRLF, truncated last line, junk); changelog escapes and quoted values; rotated logs read in order.
- **Planner:** threshold edges; plays of non-RockPlex files ignored; bookmark resumes mid-file; a rotated or replaced log is detected; the same input processed twice sends nothing the second time; ratings only when changed; unrated 0 never sent.
- **Plex:** scrobble and rate send exactly the documented requests with the server token; nothing else is ever written.
- **End-to-end (temp-folder iPod + fake Plex):** sync → play log appears → next sync sends the plays *before* removals, including plays of a track being removed in the same sync; Plex down → bookmark not advanced → next sync sends them; Rockbox's own files are left untouched (per D9).

## 8. Acceptance criteria
1. Clean build, zero warnings; all tests green.
2. With Logging on, tracks played on the iPod show their plays in Plex after the next sync, and each play is counted once.
3. A rating set on the iPod and exported shows in Plex after the next sync.
4. Plays of a track removed in the same sync still reach Plex.
5. Unplugging or Plex being unreachable loses no plays, and they're sent next time.
6. Your own files and Rockbox's settings are untouched.

## 9. Owner decisions

| # | Question | Decision |
|---|---|---|
| D8 | What counts as a play | **At least 90% of the track played** (`elapsed ≥ 0.9 × length`). Near-complete listens only. |
| D9 | Rockbox's own log files after they're processed | **Left untouched.** RockPlex never modifies Rockbox's files; it keeps a bookmark of how far it has read each log. |
| D10 | Rating changed on the iPod vs in Plex | **A setting, "Ratings from the iPod":** *iPod changes win* (default: any rating changed on the iPod since the last sync replaces Plex's) or *Only fill gaps* (sent only if the track has no rating in Plex; Plex ratings are never overwritten). |
| D11 | When plays are sent | **At every sync**, as step 0, plus a *Send plays now* button. Stats return can be switched off in Settings. |

## 10. Out of scope
Ratings from Plex back to the iPod (would need Rockbox's *Import Modifications*). Last.fm scrobbling with the real play times (M5, using `plays.jsonl`). Skip counts. Reading Rockbox's binary database directly.
