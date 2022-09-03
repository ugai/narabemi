set DOWNLOADED_FILENAME=ffmpeg-n4.4-latest-win64-lgpl-shared-4.4.zip
set DOWNLOADED_FILENAME_NOEXT=%DOWNLOADED_FILENAME:~0,-4%
set DOWNLOAD_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/%DOWNLOADED_FILENAME%
set DEST_NAME=ffmpeg

echo download: "%DOWNLOAD_URL%"
powershell -Command "Invoke-WebRequest -Uri \"%DOWNLOAD_URL%\" -OutFile \"%DOWNLOADED_FILENAME%\""

echo unzip: "%DOWNLOADED_FILENAME%"
powershell -Command "Expand-Archive -Path \"%DOWNLOADED_FILENAME%\" -DestinationPath . "

move "%DOWNLOADED_FILENAME_NOEXT%" "%DEST_NAME%"
echo %DOWNLOAD_URL% > "%DEST_NAME%\%DOWNLOADED_FILENAME_NOEXT%.txt"

echo done
pause
