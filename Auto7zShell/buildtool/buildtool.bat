@echo off

REM 这个脚本只管把 buildtool.exe 编出来，真正的构建由它再去调 vcvars 和 cl。
setlocal

set "TOOL_DIR=%~dp0"
REM 默认按 VS2022 社区版的默认安装位置猜，下面再补几个其它 SKU，装了别的版本也能直接跑。
set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set "DOWNLOAD=https://visualstudio.microsoft.com/downloads/"

REM 先试默认路径，命中就跳过整串候选，省掉多余的存在性检查。
if exist "%VCVARS%" goto havevcvars
REM 候选按常见度排：企业版、专业版、Build Tools，x86 目录放最后，64 位系统上工具链通常在 Program Files 下。
for %%P in (
  "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
) do if exist %%P set "VCVARS=%%~P"

:havevcvars
if not exist "%VCVARS%" goto novcvars

REM vcvars 会刷一屏环境信息，输出丢掉，只看它有没有把环境配好。
call "%VCVARS%" >nul
REM 环境没配起来就别往下走，否则 cl 的报错会盖掉真正的原因。
if errorlevel 1 goto novcvars

REM 先切到脚本目录，产物路径都按 %TOOL_DIR% 拼，从哪儿调用结果都一样。
pushd "%TOOL_DIR%"

REM /utf-8 让源码里的中文按 UTF-8 解析，/MT 静态链接 CRT，编出的 exe 拷到别的机器上不用带运行库。
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
REM 找不到工具链就直接把下载页打开，只给网址大多数人不会自己去点。
start "" "%DOWNLOAD%"
endlocal
exit /b 1

:failed
REM 失败分支也要 popd 回去，否则调用方脚本的当前目录会停在工具目录里。
popd
echo.
echo Build failed.
endlocal
exit /b 1
