# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Narabemi is a Windows desktop application for side-by-side video comparison, built with C#/.NET 6.0 and WPF. It uses FFME (FFmpeg Media Element) for video playback and custom HLSL pixel shaders for blend effects.

## Build & Run Commands

```bash
# Build
dotnet build Narabemi/Narabemi.csproj

# Run
dotnet run --project Narabemi/Narabemi.csproj

# Publish
dotnet publish Narabemi/Narabemi.csproj

# Hot reload
dotnet watch run --project Narabemi/Narabemi.csproj
```

**Note:** The PreBuild target compiles HLSL shaders via `Shaders/compile_shaders.bat` and copies them to the output directory. FFmpeg binaries must be present at the path configured in `appsettings.json` (default: `./ffmpeg/bin`). Run `download_ffmpeg.bat` to fetch them.

There are no unit tests in this project.

## Architecture

**MVVM with DI:** Uses `Microsoft.Extensions.Hosting` for dependency injection and `CommunityToolkit.Mvvm` for the MVVM pattern (`[ObservableProperty]`, `[RelayCommand]`, `WeakReferenceMessenger`).

**Entry point:** `App.xaml.cs` — builds the Generic Host, configures DI container (singletons for services and MainWindow), loads config, initializes FFME, then shows MainWindow.

**Key services (all singletons):**
- `MediaElementsManager` — synchronizes playback across two video players, handles sync strategies (simple offset vs. speed-ratio adjustment)
- `ControlFadeManager` — auto-hide/show UI controls based on mouse activity
- `AppStatesService` — persists/loads mutable runtime state (`appstates.json`)

**UI layer:** `UI/Windows/` for windows, `UI/Controls/` for reusable controls. Each has a XAML + code-behind + ViewModel. `VideoPlayer` wraps `FFME.MediaElement` with drag-and-drop, subtitle support, and per-player volume/offset.

**Messaging:** Decoupled communication via `WeakReferenceMessenger` with message types in `Messages/`.

**Configuration:**
- `appsettings.json` — read-only settings (FFmpeg path, shader path, sync timer interval)
- `appstates.json` — mutable runtime state (loop, auto-sync, video paths, blend border)

**Shaders:** HLSL pixel shaders in `Shaders/` compiled to `.fxc` with FXC compiler (PS 2.0). `BlendEffect.cs` loads these at runtime.

## Code Style

- Warnings are treated as errors in both Debug and Release
- Code style is enforced in builds (`EnforceCodeStyleInBuild`)
- See `.editorconfig` for full formatting rules: 4-space indent for C#, Allman braces, PascalCase constants
- `var` preferred when type is apparent
