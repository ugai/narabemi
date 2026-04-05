@echo off
setlocal

:: Compile Narabemi D3D11 shaders using fxc.exe
:: Run this script from the Narabemi/Shaders directory, or adjust paths below.

:: Try fxc.exe in PATH first, fall back to Windows SDK location
where fxc.exe >nul 2>&1
if %errorlevel%==0 (
    set FXC=fxc.exe
) else (
    set FXC=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe
)
set OUT=%~dp0

echo Compiling fullscreen_vs.hlsl (vs_5_0)...
%FXC% /T vs_5_0 /E main /Fo "%OUT%fullscreen_vs.cso" "%OUT%fullscreen_vs.hlsl"
if errorlevel 1 goto :error

echo Compiling blend_horizontal.hlsl (ps_5_0)...
%FXC% /T ps_5_0 /E main /Fo "%OUT%blend_horizontal.cso" "%OUT%blend_horizontal.hlsl"
if errorlevel 1 goto :error

echo Compiling blend_vertical.hlsl (ps_5_0)...
%FXC% /T ps_5_0 /E main /Fo "%OUT%blend_vertical.cso" "%OUT%blend_vertical.hlsl"
if errorlevel 1 goto :error

echo All shaders compiled successfully.
goto :end

:error
echo Shader compilation failed.
exit /b 1

:end
endlocal
