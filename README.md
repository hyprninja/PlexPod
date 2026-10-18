# RockPlex

Syncs a Plex Media Server music library to an iPod 5.5G (Video) running Rockbox. Plex is the source of truth.
Windows desktop app: .NET 10, WPF, Fluent theme.

**Milestones 1–3 are implemented:** Plex sign-in and pickers, the media pipeline (conversion, clean tags, art) and the **sync engine**. **Sync** shows the plan first, copies and verifies every file, writes playlists that never break, and only ever touches files it created (tracked in `/.rockplex/manifest.json` on the iPod).

Requires **ffmpeg** with soxr for hi-res conversion and for verification (`winget install Gyan.FFmpeg`). RockPlex finds it on PATH or in winget's folder, or you can set the path in Settings.

Design notes are in `CLAUDE.md` and `docs/`.

## Build, test, run
```
dotnet build RockPlex.slnx -c Debug
dotnet test  RockPlex.slnx
dotnet run --project src/RockPlex.App
```

## Layout
| Project | What it does |
|---|---|
| `src/RockPlex.Core` | Models, format policy, size estimates, selection resolver, capacity check, tag mapping, sync planner, fit check, playlist writer. No packages, no I/O. |
| `src/RockPlex.Plex` | plex.tv PIN sign-in, server discovery and connection probing, library/playlist reads, track detail enrichment. |
| `src/RockPlex.Device` | Rockbox drive detection and watcher; on-device state (`/.rockplex/`), verified copies (temp → unbuffered read-back → rename), playlist line checks. |
| `src/RockPlex.Media` | Download → format decision from the real file → ffmpeg conversion (soxr, TPDF dither) → tag wipe + rewrite (TagLibSharp) → baseline JPEG art (WIC) → verification; conversion cache; Test export; **SyncEngine** and adoption. |
| `src/RockPlex.App` | WPF shell: generic host, settings, DPAPI token store, rolling log, view models. |
| `tests/RockPlex.Tests` | xUnit tests for Core, Plex (fake HTTP + JSON fixtures), Device (temp dirs as drives) and Media (tiny audio fixtures; ffmpeg tests are skipped if ffmpeg is missing). |

## App data
`%LOCALAPPDATA%\RockPlex\`: `settings.json`, `token.bin` (DPAPI, current user), `logs\rockplex-yyyyMMdd.log` (7 kept; tokens redacted), `cache\` (converted audio, capped at 20 GB by default, plus album art).

## Notes
- The JSON fixtures in `tests/RockPlex.Tests/Fixtures` are **representative**, not captured. Replace them with real responses (token redacted) when you can.
- FLAC tracks at or below 1,100 kbps are assumed to be 16/44.1 for the "≈" indicator and are not fetched in detail. Their size estimate is the source size either way, so the fit check stays conservative. M2 must still read real stream details before deciding whether to convert.
- The fit check keeps 64 MiB free (`CapacityCalculator.ReserveBytes`) for Rockbox's database and playlists.
