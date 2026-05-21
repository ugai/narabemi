echo "%~nx0" START
echo cd %~dp0
pushd %~dp0

echo Load VsDevCmd
set VSWHERE="%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`%VSWHERE% -latest -prerelease -property installationPath`) do (
  if exist "%%i\Common7\Tools\vsdevcmd.bat" (
    call "%%i\Common7\Tools\vsdevcmd.bat"
  )
)

echo Compile Shader

for %%f in (*.hlsl) do (
    echo %%~nf

    :: NOTE:
    ::   PS 3.0 doesn't support software rendering!!
    fxc /T ps_2_0 /Fo "%%~nf.fxc" "%%~nf.hlsl"
)

popd
echo "%~nx0" END
