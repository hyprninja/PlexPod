# RockPlex

Syncs a Plex Media Server music library to an iPod 5.5G (Video) running Rockbox. Plex is the source of truth.
Windows desktop app: .NET 10, WPF, Fluent theme.

**Milestone 1 (foundation + pickers) is implemented.** It is read-only: nothing is written to the iPod yet.
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
| `src/RockPlex.Core` | Models, format policy, size estimates, selection resolver, capacity check. No packages, no I/O. |
| `src/RockPlex.Plex` | plex.tv PIN sign-in, server discovery and connection probing, library/playlist reads, track detail enrichment. |
| `src/RockPlex.Device` | Rockbox drive detection, `rockbox-info.txt` parsing, plug/unplug watcher. |
| `src/RockPlex.App` | WPF shell: generic host, settings, DPAPI token store, rolling log, view models. |
| `tests/RockPlex.Tests` | xUnit tests for Core, Plex (fake HTTP + JSON fixtures) and Device (temp dirs as drives). |

## App data
`%LOCALAPPDATA%\RockPlex\`: `settings.json`, `token.bin` (DPAPI, current user), `logs\rockplex-yyyyMMdd.log` (7 kept; tokens redacted).

## Notes
- The JSON fixtures in `tests/RockPlex.Tests/Fixtures` are **representative**, not captured. Replace them with real responses (token redacted) when you can.
- FLAC tracks at or below 1,100 kbps are assumed to be 16/44.1 for the "≈" indicator and are not fetched in detail. Their size estimate is the source size either way, so the fit check stays conservative. M2 must still read real stream details before deciding whether to convert.
- The fit check keeps 64 MiB free (`CapacityCalculator.ReserveBytes`) for Rockbox's database and playlists.
