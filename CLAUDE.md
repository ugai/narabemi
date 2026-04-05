# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Narabemi is a Windows desktop application for side-by-side video comparison. There are two versions in this repository:

- **`Narabemi/`** — New Avalonia + libmpv version (C#/.NET 10, active development)
- **`Narabemi.Wpf/`** — Legacy WPF + FFME version (C#/.NET 6, preserved for reference)

## Build & Run Commands

```bash
# Build (Avalonia version)
dotnet build Narabemi/Narabemi.csproj

# Run
dotnet run --project Narabemi/Narabemi.csproj

# Test (Avalonia version)
dotnet test Narabemi.Tests/Narabemi.Tests.csproj
```

**Note:** libmpv binary (`libmpv-2.dll`) must be present in `Narabemi/lib/` for video playback.

```bash
# Build (Legacy WPF version)
dotnet build Narabemi.Wpf/Narabemi.csproj

# Test (Legacy WPF version)
dotnet test Narabemi.Wpf.Tests/Narabemi.Tests.csproj
```

Tests run automatically as a pre-commit hook.

## Architecture (Avalonia version)

**MVVM with DI:** Uses `Microsoft.Extensions.Hosting` for dependency injection and `CommunityToolkit.Mvvm` for the MVVM pattern (`[ObservableProperty]`, `[RelayCommand]`, `WeakReferenceMessenger`).

**Entry point:** `App.axaml.cs` — builds the Generic Host, configures DI container, loads config, then shows MainWindow.

**Key components:**
- `Mpv/MpvApi.cs` — Thin P/Invoke layer for libmpv C API
- `Mpv/MpvPlayer.cs` — High-level playback wrapper (play/pause/seek/volume, background event loop)
- `Mpv/MpvVideoView.cs` — Avalonia `NativeControlHost` that provides a window handle for mpv rendering
- `Services/ControlFadeManager.cs` — auto-hide/show UI controls based on mouse activity
- `UI/Controls/ControlFadeAnimator.cs` — Avalonia animation driver for control fade
- `Settings/AppStatesService.cs` — persists/loads mutable runtime state (`appstates.json`)

**Settings layer** (`Settings/`) has no UI framework dependency. `ColorRgba` is a platform-neutral color type.

**Messaging:** Decoupled communication via `WeakReferenceMessenger` with message types in `Messages/`.

**Configuration:**
- `appsettings.json` — read-only settings (mpv directory, sync timer interval)
- `appstates.json` — mutable runtime state (loop, auto-sync, video paths, blend border)

## Code Style

- Warnings are treated as errors in both Debug and Release
- Code style is enforced in builds (`EnforceCodeStyleInBuild`)
- See `.editorconfig` for full formatting rules: 4-space indent for C#, Allman braces, PascalCase constants
- `var` preferred when type is apparent
