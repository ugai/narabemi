@echo off
:: Visual snapshot tool for Narabemi
:: Usage: snapshot.bat [video_a.mp4] [video_b.mp4] [seek_seconds] [output.png]
::
:: Example (single video, horizontal split at same file):
::   snapshot.bat "C:\Videos\test.mp4" "" 5 snapshot.png
::
:: Example (two videos, seek to 10 seconds):
::   snapshot.bat "C:\Videos\a.mp4" "C:\Videos\b.mp4" 10 snapshot.png

set VIDEO_A=%1
set VIDEO_B=%2
set SEEK=%3
set OUTPUT=%4

if "%SEEK%"=="" set SEEK=5
if "%OUTPUT%"=="" set OUTPUT=snapshot.png

set ARGS=--snapshot --seek %SEEK% -o %OUTPUT%
if not "%VIDEO_A%"=="" set ARGS=%ARGS% --video-a %VIDEO_A%
if not "%VIDEO_B%"=="" set ARGS=%ARGS% --video-b %VIDEO_B%

echo Running Narabemi in snapshot mode...
echo   Seek: %SEEK%s
echo   Output: %OUTPUT%

dotnet run --project Narabemi/Narabemi.csproj -- %ARGS%

if %ERRORLEVEL%==0 (
    echo Snapshot saved: %OUTPUT%
) else (
    echo Snapshot failed with exit code %ERRORLEVEL%
)
