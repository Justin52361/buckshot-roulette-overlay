# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A Windows-only C# WinForms tool (single project, `src/BuckshotOverlay.App`, targeting `net10.0-windows10.0.19041.0`, x64) that shows a topmost HUD over the Godot 4 game **Buckshot Roulette**. The HUD shows how many live and blank shells are left. It tracks these automatically by reading the game's process memory externally, with screen OCR and muzzle-flash detection as fallbacks.

## Commands

```powershell
.\build.ps1                      # dotnet build (Release) + copy output into dist/
.\build.ps1 -Configuration Debug
dotnet build src\BuckshotOverlay.App\BuckshotOverlay.App.csproj -c Release
.\run.bat                        # builds if dist\BuckshotOverlay.exe is missing, then launches it
.\dist\BuckshotOverlay.exe --render-test   # headless HUD render -> dist\rendered_hud_default.png, rendered_hud.png (2L/2B)
.\dist\BuckshotOverlay.exe --inject        # CLI injection of BuckshotHook.dll into the running game
```

- There are no tests and no linter. The closest thing to a test is `--render-test`: it renders `OverlayForm` to PNGs so you can check HUD drawing changes without the game running.
- `Directory.Build.props` sends `bin/` and `obj/` to `%LOCALAPPDATA%\BuckshotBuild\` instead of the repo. `build.ps1` copies from that location into `dist/`.
- The app writes diagnostics to `app_debug.log` next to the exe (for example `dist\app_debug.log`).

## Architecture

**Entry and UI.** `Program.cs` handles the CLI modes and then runs `MainWindow`, the control center. `MainWindow` does the following:
- Polls for `Buckshot Roulette.exe` every second.
- Creates and owns the `OverlayForm`.
- Starts and stops `AutoTrackerEngine`.
- Routes global hotkeys from `GlobalKeyboardHook`: F4 toggles click-through (`WS_EX_TRANSPARENT`), F8 resets the round, F9 hides or shows the overlay.

`OverlayForm` holds the shell counts (`LiveCount`/`BlankCount`, `SetRound`, `RegisterLiveFired`/`RegisterBlankFired`, `UpdateCurrentCounts`), custom-paints the HUD, and anchors itself to the game window.

**Tracking.** `AutoTrackerEngine` runs a background loop with a 50 ms tick. It changes overlay state only through `_overlay.BeginInvoke(...)`. Each tick it uses three signals, in priority order:
1. **Memory (every ~100 ms, authoritative).** `MemoryScanner` scans committed RW heap regions with `ReadProcessMemory`, looking for Godot 4 `CowData` arrays (refcount and size header, then `Variant` elements) whose elements are the strings `live`/`blank`. It tries several element layouts (`s_configurations`, stride and pointer offset).
   - The heap holds look-alike arrays: freed buffers, the unmodified `tempSequence`, and arrays from earlier rounds. So the tracker does **not** trust scan results directly.
   - Instead it finds pointer *holders* (`FindPointerHolders`) and re-reads them each tick (`ReadViaHolder`).
   - It accepts only legal transitions:
     - remove-front (a shot fired or racked)
     - front-shell flip seen on 2 consecutive reads (the inverter)
     - same-multiset reorder (a shuffle)
   - The first holder to make a legal deduction becomes `_confirmedHolder`, and the other holders are dropped.
   - Arrays seen before are kept as `_staleSignatures`, so a new round is detected only from a freshly allocated array, and only after it shows up on two consecutive scans.
2. **OCR (every ~300 ms, fallback).** Windows.Media.Ocr reads the dealer subtitle strip ("X LIVE ROUNDS. Y BLANK."). It may start a round only while the gun is empty or `_awaitingNewRound` is set.
3. **Muzzle flash (every 50 ms).** Checks the brightness of a box in the center of the screen. It runs only when no memory holder is being tracked.

Keep this priority order when changing tracking logic. Memory results override the counts from the other signals (`UpdateCurrentCounts`).

**Injection.** `InjectorEngine` finds the game process and window and performs classic `CreateRemoteThread` + `LoadLibraryW` injection. `DllGenerator`, and the same logic duplicated in `build.ps1`, hand-writes a minimal x64 PE file for `BuckshotHook.dll`. Its `DllMain` just returns TRUE: the DLL contains no hook logic, and all tracking runs in the overlay process. If you change the generated PE bytes, keep `DllGenerator.cs` and `build.ps1` consistent.

## Gotchas

- `NoWarn` suppresses `CS8625` and the WinForms analyzer warnings `WFO1000` and `WFO0003`.
- `dist/` is gitignored build output; `run.bat` rebuilds it on a fresh clone.
