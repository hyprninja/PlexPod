# Milestone 7: PlexPod on macOS

**Status:** implemented on branch `mac-port` (14 Nov 2026), waiting for the owner's checks (§8).
Builds clean on macOS, with the WPF app compiling there too. All tests pass on macOS (ffmpeg tests
skipped until ffmpeg is installed), including FAT32 disk-image tests that fail without the §3
fixes. The Keychain round trip was checked by hand, the packaged `PlexPod.app` launches, and the
windows were checked in a headless render. Owner decisions D16–D18 in §9.

**Goal:** the owner can sync the iPod from a Mac, with the same guarantees as on Windows: the same
selection, the same files and tags on the iPod, the same playlists, the same stats return. **The
Windows app is unchanged** for the user: same WPF window, same behaviour, same installer.

**Why it's tractable:** about 85% of the code is plain .NET that already runs on macOS:
`PlexPod.Core` (planner, iTunesDB, Play Counts, tags, stats), `PlexPod.Plex` and most of
`PlexPod.Device`. The whole Windows solution already compiles on a Mac with
`-p:EnableWindowsTargeting=true` (checked on 14 Nov 2026: 0 warnings), so every change can be
checked against the Windows build from the Mac. What is Windows-only is a short list (§2).

Status markers as in the other plans: ✅ verified · ⚠️ documented, not yet held against this Mac or
iPod · 🔬 must be tried on the device.

---

## 1. Shape of the solution

| Project | Today | After M7 |
|---|---|---|
| `PlexPod.Core` | `net10.0` | unchanged |
| `PlexPod.Plex` | `net10.0` | unchanged |
| `PlexPod.Device` | `net10.0`, Windows calls inside | `net10.0`, Windows and macOS code paths chosen at run time (§3) |
| `PlexPod.Media` | `net10.0-windows` (WIC for art) | multi-targets `net10.0-windows` (WIC, unchanged) and `net10.0` (macOS ImageIO) |
| `PlexPod.App.Shared` | — | **new**, `net10.0`: the view models and services moved out of `PlexPod.App`, with small seams for the UI thread, picker filtering, token storage and the shell |
| `PlexPod.App` | WPF, view models inside | WPF only: windows, converters and the Windows implementations of the seams |
| `PlexPod.Mac` | — | **new**, Avalonia 11 (Fluent theme, follows light/dark) for macOS, Apple Silicon |
| `PlexPod.Tests` | `net10.0-windows` | `net10.0-windows` on Windows, `net10.0` elsewhere; Windows-only tests (DPAPI, registry, WIC) compile only on Windows |

Namespaces stay as they are (`PlexPod.App.ViewModels`, `PlexPod.App.Services`) so the move doesn't
churn every file. Nothing about the sync pipeline, its order or its invariants changes.

## 2. Windows-only pieces and their macOS counterparts

| Area | Windows (unchanged) | macOS |
|---|---|---|
| UI | WPF, `Application.Current.Dispatcher` | Avalonia, `Dispatcher.UIThread` behind `IUiThread` |
| Plex token | DPAPI → `token.bin` | **Keychain** (generic password, service `PlexPod`), via Security.framework; never on disk in plaintext, never logged |
| Album art | WIC decode + baseline JPEG encode | **ImageIO** (CGImageSource / CGImageDestination, baseline JPEG, quality 0.85); scaling, padding and RGB565 stay in shared code |
| Drives | `DriveInfo` with drive letters | `DriveInfo` mounts under `/Volumes/`, network and read-only mounts skipped |
| Unbuffered read-back (D6) | `FILE_FLAG_NO_BUFFERING` | `F_NOCACHE` on both the write and the read-back handle, so the hash is computed from the disk ⚠️ |
| USB product id (signed-database check) | registry | none for now: no USB evidence, the file evidence still applies (the iPod Video has none either way) |
| ffmpeg | settings → `PATH` → winget | settings → `PATH` → `/opt/homebrew/bin`, `/usr/local/bin` (apps opened from Finder don't get the shell's `PATH`). ✅ Homebrew's own ffmpeg (9.0.2) has **no soxr** (nor Vorbis), so PlexPod refuses hi-res FLAC with it; the `homebrew-ffmpeg/ffmpeg` tap with `--with-libsoxr` has both (CI uses it) |
| App data | `%LOCALAPPDATA%\PlexPod\` | `~/Library/Application Support/PlexPod/` (what .NET returns for LocalApplicationData on macOS) |
| Reveal folder / open browser | `explorer.exe` / shell execute | `open` |
| Package | WiX MSI | `PlexPod.app` (self-contained, osx-arm64, ad-hoc signed) in a `.dmg`; not notarised (§10) |

## 3. macOS hazards for the iPod (new invariants)

1. **AppleDouble files.** On FAT32, macOS stores extended attributes in `._name` files. A Rockbox
   database scan would list `._track.flac` as a broken track (and `._Mix.m3u8` as a broken playlist).
   ✅ Measured on a FAT32 image: macOS gives **every file and folder a process creates** a
   `com.apple.provenance` attribute, which `xattr -c` can't remove, so a `._` twin appears next to
   each one, folders included. Deleting the twin itself works and leaves the file clean. So after
   every write, rename and folder creation PlexPod deletes the twin (it's part of a file PlexPod
   owns, so the manifest rule holds). `._*`, `.DS_Store`, `.Spotlight-V100`, `.fseventsd` and
   `.Trashes` are never treated as tracks, playlists or the user's files. 🔬 Spotlight's own folders
   at the iPod root are macOS's; whether Rockbox's database scan minds them is untested.
2. **Unicode normalisation.** macOS can return file names decomposed (NFD) where Plex and the
   manifest use composed (NFC). ✅ Measured on a FAT32 image: listings come back NFD even for names
   written NFC; lookups match either form. Without the fix every playlist line with an accent fails
   verification and blocks the sync. Every name read from a directory listing on the iPod is compared
   in NFC; device paths PlexPod writes stay NFC, as on Windows, so a Mac-synced and a PC-synced iPod
   hold the same bytes. The playlist verifier's exact-name check (case-sensitive per Rockbox) compares
   NFC to NFC. 🔬 Tested on a real FAT32 disk image (`hdiutil create -fs MS-DOS`) in CI and locally.
3. **Finder and the stock iPod.** Finder shows a stock iPod and can sync it. Syncing there would
   replace PlexPod's iTunesDB. The README tells the user not to sync in Finder and to turn off
   *Automatically sync when this iPod is connected*. 🔬 whether a Windows-formatted stock iPod mounts
   as a volume without *Enable disk use*.
4. **Ejecting.** Mac users must eject before unplugging. The Mac app has an **Eject** button
   (`diskutil eject`), enabled only when no sync is running.

## 4. The same iPod from both computers

Everything the sync relies on is on the iPod (`/.plexpod/manifest.json`, `stats.json`,
`selection.json`), so a Windows-synced iPod can be synced from the Mac and back. Per-computer state
(settings, token, conversion cache, the play history in `stats/<device>/`) is not shared; plays
already sent to Plex are recorded on the iPod, so switching computers never sends a play twice ⚠️.
Album art encoded by ImageIO differs byte-for-byte from WIC's; the change stamp doesn't include art
bytes, so switching computers doesn't re-copy the library.

## 5. Build order

1. Plan (this file), git checkout on `mac-port`, .NET 10 SDK on the Mac.
2. Cross-platform libraries: Device (drives, `F_NOCACHE`, AppleDouble, NFC), Media (art codec seam,
   ffmpeg locations), tests by host OS. Windows build still compiles ✅ and all tests run on the Mac.
3. `PlexPod.App.Shared`: move view models and services, add the seams, WPF implements them.
   WPF XAML only gains `;assembly=PlexPod.App.Shared` on its `vm:` namespace.
4. `PlexPod.Mac`: Avalonia windows mirroring the WPF ones (main, settings, sync confirm), Keychain,
   Eject, `open`.
5. CI: GitHub Actions builds and tests on `windows-latest` and `macos-latest` for every push and PR.
6. Packaging script `installer/build-mac.sh` → `PlexPod.app` + `.dmg`.
7. Owner checks: Windows build on the PC (D18), then the Mac on the real iPod (§8).

## 6. Tests

- All existing tests run on macOS except the Windows-only ones (DPAPI, registry), which stay on Windows.
- Art tests use the codec seam to make their input images, so they run on both.
- New: Mac drive source filtering; `F_NOCACHE` read-back detects a corrupted copy; AppleDouble files
  ignored and cleaned; NFD listings match NFC manifest paths; Keychain round trip (Mac only);
  ffmpeg candidate order on macOS; an end-to-end sync to a mounted FAT32 image (Mac only).

## 7. What doesn't change

Read-only against Plex libraries; never upsample; playlists never partial or broken; sync order;
manifest-only deletes; device writes through `DeviceFileOps`; untrusted iPod data through
`DevicePaths`; tokens scoped as today; nothing slow on the UI thread.

## 8. Acceptance criteria (owner)

1. Windows: the PR's Windows build installs and syncs as v1.0.2 did (D18).
2. Mac: sign in to Plex, the server is found, the library and pickers load, the capacity bar works.
3. Mac, stock iPod: a playlist syncs and plays in the car; covers show; a play and a rating on the
   iPod reach Plex on the next sync.
4. Mac: no `._` files or Spotlight folders inside `/Music`, `/Playlists` or `/iPod_Control/Music` after a sync.
5. A track with accents in its name syncs from the Mac, and a following sync from Windows (or the
   reverse) copies nothing.

## 9. Owner decisions

- **D16 (14 Nov 2026):** a separate Mac app (Avalonia) that shares the view models with the Windows
  app; the Windows app keeps WPF and looks the same.
- **D17 (14 Nov 2026):** the .NET 10 SDK is installed in `~/.dotnet` on the Mac; the owner installs
  Homebrew and ffmpeg.
- **D18 (14 Nov 2026):** CI on Windows and macOS for every push, plus a hands-on check of the
  Windows build by the owner before merging.

## 10. Open / out of scope

- Signing with an Apple Developer ID and notarisation (the app is ad-hoc signed; first launch needs
  right-click → Open, or *Open Anyway* in System Settings).
- Intel Macs (osx-x64 builds are possible but untested).
- `.metadata_never_index` at the iPod root to stop Spotlight: it's a write outside the folders
  PlexPod manages, so it needs an owner decision.
- USB product id on macOS (IOKit), for the signed-database check on other iPod models.
