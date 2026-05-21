@echo off
:: Benchmark tool for Narabemi GPU pipeline
:: Usage: bench.bat [seconds] [video_a.mp4] [video_b.mp4]
:: Example: bench.bat 10 "C:\Videos\a.mp4" "C:\Videos\b.mp4"

set SECONDS=%1
set VIDEO_A=%2
set VIDEO_B=%3

if "%SECONDS%"=="" set SECONDS=10
if "%VIDEO_A%"=="" set VIDEO_A=C:\git\narabemi\_temp\jizou.mp4
if "%VIDEO_B%"=="" set VIDEO_B=C:\git\narabemi\_temp\jizou.mp4

echo Running Narabemi benchmark...
echo   Duration: %SECONDS%s
echo   Video A: %VIDEO_A%
echo   Video B: %VIDEO_B%

dotnet run --project Narabemi/Narabemi.csproj -c Release -- --bench %SECONDS% --video-a %VIDEO_A% --video-b %VIDEO_B%

if %ERRORLEVEL%==0 (
    echo Benchmark complete. See Narabemi.log for timing details.
) else (
    echo Benchmark failed with exit code %ERRORLEVEL%
)
