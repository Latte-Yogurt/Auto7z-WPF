@echo off
rem Build buildtool.exe from BuildTool.cpp.
rem
rem The build tool itself is a small standalone program, so it is compiled here
rem rather than by buildtool.exe. It needs the MSVC x64 toolchain.

setlocal

set "TOOL_DIR=%~dp0"
rem Preferred toolchain on this machine: Visual Studio 2022 Community.
set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set "DOWNLOAD=https://visualstudio.microsoft.com/downloads/"

rem If that exact path is gone, fall back to the other 2022 editions before
rem giving up -- a reinstalled or renamed installation should still build.
if exist "%VCVARS%" goto havevcvars
for %%P in (
  "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
) do if exist %%P set "VCVARS=%%~P"

:havevcvars
if not exist "%VCVARS%" goto novcvars

call "%VCVARS%" >nul
if errorlevel 1 goto novcvars

pushd "%TOOL_DIR%"

rem /utf-8 is required, the source has Chinese text in it.
cl /nologo /utf-8 /EHsc /O2 /MT /W3 /DUNICODE /D_UNICODE ^
   /Fe:"%TOOL_DIR%buildtool.exe" "%TOOL_DIR%BuildTool.cpp" ^
   ole32.lib shell32.lib advapi32.lib shlwapi.lib user32.lib
if errorlevel 1 goto failed

popd
echo Built %TOOL_DIR%buildtool.exe
endlocal
exit /b 0

:novcvars
echo vcvars64.bat not found: %VCVARS%
echo.
echo Need Visual Studio 2022 (Community/Professional/Enterprise) or the
echo Build Tools with the x64 C++ toolchain.
echo Download: %DOWNLOAD%
echo.
echo Pick the Desktop development with C++ workload, and keep the
echo MSVC build tools plus a Windows SDK component.
echo.
start "" "%DOWNLOAD%"
endlocal
exit /b 1

:failed
popd
echo.
echo Build failed.
endlocal
exit /b 1
