# Narabemi

Quick side-by-side video comparison tool.

## Screenshots

![screenshot main2](screenshots/ss2.gif?raw=true)
![screenshot main1](screenshots/ss.jpg?raw=true)

*© Blender Foundation | [cloud.blender.org/spring](http://cloud.blender.org/spring), [studio.blender.org/films/big-buck-bunny](https://studio.blender.org/films/big-buck-bunny)*

### Comparing Subtitles

![screenshot subtitles](screenshots/ss_subs.jpg?raw=true)

## Versions

This repository contains two versions:

- **`Narabemi/`** — Active version. Avalonia 11 + libmpv on .NET 10. Native dual-HWND D3D11 playback (each video rendered directly by mpv into its own child window).
- **`Narabemi.Wpf/`** — Legacy WPF + FFME on .NET 6. Preserved for reference; supported a custom HLSL blend shader (see git history).

The instructions below are for the active **Avalonia** version. For the legacy version, see [`Narabemi.Wpf/`](Narabemi.Wpf/).

## Installation

### Download

- [Releases](https://github.com/ugai/narabemi/releases)

### libmpv

`libmpv-2.dll` must be present in `Narabemi/lib/`. Download a Windows build from [mpv.io/installation](https://mpv.io/installation/) (the `mpv-dev` archive) and copy `libmpv-2.dll` into `Narabemi/lib/`.

## Building from source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- `libmpv-2.dll` placed at `Narabemi/lib/libmpv-2.dll`

### Steps

```bash
dotnet build Narabemi/Narabemi.csproj
dotnet run   --project Narabemi/Narabemi.csproj
```

To run the test suite:

```bash
dotnet test Narabemi.Tests/Narabemi.Tests.csproj
```

## Usage

- **Open files**: drag-and-drop two video files onto the window, or use `Ctrl+O` / `Ctrl+Shift+O` to open files for player A / B individually.
- **Drag the splitter**: the thin line between the two video panes is draggable — grab it to change the split ratio in real time.
- **Keyboard shortcuts**: `Space` play/pause, `Esc` stop, `←` / `→` seek ±5 s (`Shift+` for ±30 s).
- **Split direction**: toggle between Horizontal (side-by-side) and Vertical (stacked) via the `Horizontal/Vertical` button in the control panel.
- **Sync** seeks both players together when enabled. **Loop** loops the current videos.

## Test / benchmark modes

```bash
# Headless fps benchmark of the real pipeline (15 s, dual player)
dotnet run --project Narabemi/Narabemi.csproj -- --bench 15 \
    --video-a A.mp4 --video-b B.mp4

# Snapshot at 5 s into each video (raw mpv frame, no OSD)
dotnet run --project Narabemi/Narabemi.csproj -- --snapshot --seek 5 \
    --video-a A.mp4 --video-b B.mp4 -o snapshot.png

# Architectural probe (skips the main UI, useful for raw rendering experiments)
dotnet run --project Narabemi/Narabemi.csproj -- --probe-native 15 \
    --video-a A.mp4 [--video-b B.mp4]
```

## Limitations

- Two players are not frame-locked; each runs at its own present cadence.
- Video playback synchronization is not frame-accurate.

## Alternatives

- [Image Comparison & Analysis Tool (ICAT)](https://www.nvidia.com/en-us/geforce/technologies/icat/)
- [video-compare](https://github.com/pixop/video-compare)
- [GridPlayer](https://github.com/vzhd1701/gridplayer)
- [Syncplay](https://github.com/Syncplay/syncplay)
- [FFmpeg - Create a mosaic out of several input videos](https://trac.ffmpeg.org/wiki/Create%20a%20mosaic%20out%20of%20several%20input%20videos)

## License

MIT license
