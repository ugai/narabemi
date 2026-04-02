# Narabemi

Quick side-by-side video comparison tool.

## Screenshots

![screenshot main2](screenshots/ss2.gif?raw=true)
![screenshot main1](screenshots/ss.jpg?raw=true)

*© Blender Foundation | [cloud.blender.org/spring](http://cloud.blender.org/spring), [studio.blender.org/films/big-buck-bunny](https://studio.blender.org/films/big-buck-bunny)*

### Custom Blend Shader

![screenshot custom blend shader](screenshots/ss_custom_shader.jpg?raw=true)

```hlsl
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color0 = tex2D(input0, uv);
    float4 color1 = tex2D(input1, uv);
    float4 color = lerp(color0, color1, step(ratio, uv.x) * saturate(step(uv.y % 0.2, 0.1))); // stripe blend
    return lerp(color, borderColor,
        step(uv.x - (borderWidth / widthPx / 2.0f), ratio) *
        step(ratio, uv.x + borderWidth / widthPx / 2.0f));
}
```

### Comparing Subtitles

![screenshot subtitles](screenshots/ss_subs.jpg?raw=true)

## Installation

### Download

- [Releases](https://github.com/ugai/narabemi/releases)

### Set FFmpeg path

1. [Download](https://ffmpeg.org/download.html) the FFmpeg 4.4 binary and put it somewhere.
2. Edit the `appsettings.json` file to set the path to FFmpeg's `bin` directory.

Or just run the `download_ffmpeg.bat` file.

## Building from source

### Prerequisites

- [.NET 6.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/6.0) — required to build and run the application
- **FXC shader compiler** — required for the PreBuild step that compiles HLSL pixel shaders to `.fxc` bytecode
  - Included with the [Windows SDK](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/) (install the "Windows SDK for Desktop C++ x86 Apps" or "Windows SDK for Desktop C++ amd64 Apps" component)
  - Alternatively available via the [Microsoft.Windows.SDK.CPP NuGet package](https://www.nuget.org/packages/Microsoft.Windows.SDK.CPP)

### Steps

```bash
# 1. Download FFmpeg binaries (places them at ./ffmpeg/bin by default)
./download_ffmpeg.bat

# 2. Build
dotnet build Narabemi/Narabemi.csproj

# 3. Run
dotnet run --project Narabemi/Narabemi.csproj
```

## Limitations

- Video playback synchronization is not frame-accurate, not perfect.

## Alternatives

- [Image Comparison & Analysis Tool (ICAT)](https://www.nvidia.com/en-us/geforce/technologies/icat/)
- [video-compare](https://github.com/pixop/video-compare)
- [GridPlayer](https://github.com/vzhd1701/gridplayer)
- [Syncplay](https://github.com/Syncplay/syncplay)
- [FFmpeg - Create a mosaic out of several input videos](https://trac.ffmpeg.org/wiki/Create%20a%20mosaic%20out%20of%20several%20input%20videos)

## License

MIT license
