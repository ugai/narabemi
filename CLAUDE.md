# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Narabemi is a Windows desktop application for side-by-side video comparison. There are two versions in this repository:

- **`Narabemi/`** — Active version. Avalonia 11 + libmpv, .NET 10. Dual-HWND native D3D11 playback.
- **`Narabemi.Wpf/`** — Legacy WPF + FFME, .NET 6, preserved for reference only.

## Build & Run Commands

```bash
# Build / run / test (Avalonia version)
dotnet build Narabemi/Narabemi.csproj
dotnet run   --project Narabemi/Narabemi.csproj
dotnet test  Narabemi.Tests/Narabemi.Tests.csproj

# Headless test modes (use a test video, e.g. _temp/jizou_60fps.mp4)
dotnet run --project Narabemi/Narabemi.csproj -- --bench 15 \
    --video-a <pathA> --video-b <pathB>
dotnet run --project Narabemi/Narabemi.csproj -- --snapshot --seek 5 \
    --video-a <pathA> --video-b <pathB> -o snapshot.png
dotnet run --project Narabemi/Narabemi.csproj -- --probe-native 15 \
    --video-a <pathA> [--video-b <pathB>]

# Legacy WPF version
dotnet build Narabemi.Wpf/Narabemi.csproj
dotnet test  Narabemi.Wpf.Tests/Narabemi.Tests.csproj
```

**Note:** libmpv binary (`libmpv-2.dll`) must be present in `Narabemi/lib/` for video playback.

Tests run automatically as a pre-commit hook.

## Architecture (Avalonia version)

**Playback model.** Two independent `MpvPlayer` instances each own a child HWND and render directly to it via `vo=gpu, gpu-api=d3d11, hwdec=d3d11va`. There is no shared GPU pipeline, no Compute Shader blend, no CPU readback. The split-position UI is implemented as a Grid layout, not a fragment shader: column/row star ratios react to `BlendRatio`, and a sibling Border between the two players is the drag handle.

```
MainWindow (Grid: rows = "*,Auto")
├── VideoGrid                                ← row 0, dynamic Cols/Rows from BlendMode+Ratio
│   ├── VideoPlayerControl (PlayerA)         ← NativeControlHost + MpvVideoView
│   ├── VideoSplitter (Border, 6px)          ← drag to update BlendRatio
│   └── VideoPlayerControl (PlayerB)
└── Control panel                            ← row 1: play/pause, seek, volume, split slider
```

`ApplyVideoLayout` in [MainWindow.axaml.cs](Narabemi/Views/MainWindow.axaml.cs) clears `RowDefinitions` / `ColumnDefinitions` and rebuilds them from `BlendMode` (0=Horizontal, 1=Vertical) and `BlendRatio` ∈ [0,1]. Subscribed to `MainWindowViewModel.PropertyChanged`.

**MVVM with DI.** `Microsoft.Extensions.Hosting` (Generic Host) for the DI container; `CommunityToolkit.Mvvm` for `[ObservableProperty]`, `[RelayCommand]`. Decoupled cross-component messaging via `WeakReferenceMessenger` and message types in `Messages/`.

**Key components:**
- `Mpv/MpvApi.cs` — `LibraryImport` P/Invoke layer for libmpv (lifecycle, properties, events).
- `Mpv/MpvPlayer.cs` — High-level wrapper. `InitNativeD3D11(hwnd)` is the only init path. Background event-loop thread; `Dispose` sends `quit` and joins before `terminate_destroy` to avoid an AVE race.
- `Mpv/MpvVideoView.cs` — Avalonia `NativeControlHost` that exposes the child HWND.
- `UI/Controls/VideoPlayerControl.axaml` — UserControl wrapping `MpvVideoView`; calls `vm.InitMpv(hwnd)` on `HandleReady`.
- `ViewModels/VideoPlayerViewModel.cs` — One per player; mediates `MpvPlayer` events ↔ Avalonia bindings; 250 ms poll timer keeps `Position`/`Duration`/`IsPaused` fresh.
- `ViewModels/MainWindowViewModel.cs` — `PlayerA`/`PlayerB`, `BlendMode`, `BlendRatio`, `AutoSync`, `Loop`, `MasterVolume`. `SeekBoth` / `SeekRelative` for keyboard nav.
- `Views/MainWindow.axaml.cs` — Drag-to-split splitter handlers, drop overlay, key shortcuts (Space, Esc, ←/→, Ctrl+O / Ctrl+Shift+O).
- `Services/ControlFadeManager.cs` + `UI/Controls/ControlFadeAnimator.cs` — Auto-hide control panel.
- `Settings/AppStatesService.cs` — Persists/loads `appstates.json`.
- `Settings/ColorRgba.cs` — Platform-neutral color (kept for `BlendBorderColor` backwards-compat in saved state).
- `Testing/ProbeRunner.cs` / `BenchmarkRunner.cs` / `SnapshotRunner.cs` — CLI test harnesses (see Test Modes below).

**Settings layer** (`Settings/`) has no UI framework dependency.

**Configuration:**
- `appsettings.json` — read-only (mpv directory, etc.).
- `appstates.json` — mutable runtime state (Loop, AutoSync, MainPlayerIndex, VideoPathList, BlendMode, BlendRatio, WindowWidth/Height/X/Y, IsWindowMaximized). `BlendBorderWidth` / `BlendBorderColor` remain in the schema for forward-compat but no longer drive any UI.

## Test Modes

| Mode | Flag | Purpose |
|---|---|---|
| Probe-native | `--probe-native <sec>` | Bypasses MainWindow; spins up a minimal Window with 1–2 mpv players for clean fps measurement. Used to validate architectural changes. |
| Bench | `--bench <sec>` | Runs the real MainWindow + dual-HWND pipeline; samples `estimated-vf-fps` per player every 250 ms; logs avg/min/max + `vo-drop-frame-count` delta. |
| Snapshot | `--snapshot --seek <sec>` | Loads, seeks, settles, then writes `screenshot-to-file` per player. Dual mode produces `<base>_a.<ext>` + `<base>_b.<ext>`. |

All test modes skip `appstates.json` write on exit so they don't clobber user state.

## Code Style

- Warnings treated as errors in Debug and Release.
- Code style enforced in builds (`EnforceCodeStyleInBuild`).
- See `.editorconfig`: 4-space indent for C#, Allman braces, PascalCase constants.
- `var` preferred when type is apparent.
- Comments only when WHY is non-obvious (hidden constraints, workarounds).
