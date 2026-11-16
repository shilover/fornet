@echo off
rem NativeAOT build of the lobby program (same source as build.bat). ASCII only.
rem   out\release\fornet_lobby.dll              release DLL: packages / public export, loaded by the game's Lua
rem   out\dev\fornet_lobby.dll                  dev DLL (same code today; a game's deploy script copies this one)
rem   out\headless\FornetLobby-headless.exe     same code as a windowless exe, not shipped: for tests
rem Needs .NET 8 SDK (dotnet on PATH, or set FORNET_DOTNET) + MSVC build tools (link.exe, Windows SDK).
rem Game C# (FORNET_GAME_CS, see ..\build.bat) is compiled in the same way as the exe.
rem ILCompiler finds MSVC with vswhere.exe, so the VS Installer folder goes on PATH.
pushd "%~dp0"
rem From Git Bash %ProgramFiles(x86)% can be empty, so also add a fixed path
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
if not defined FORNET_DOTNET set "FORNET_DOTNET=dotnet"
if not "%FORNET_DOTNET%"=="dotnet" if not exist "%FORNET_DOTNET%" set "FORNET_DOTNET=dotnet"
set PUB="%FORNET_DOTNET%" publish FornetLobbyAot.csproj -c Release -nologo -v q
%PUB% -o out\release -p:IntermediateOutputPath=obj\release\ -p:OutputPath=bin\release\ || goto fail
%PUB% -o out\dev -p:IntermediateOutputPath=obj\dev\ -p:OutputPath=bin\dev\ || goto fail
%PUB% -o out\headless -p:FornetExe=true -p:IntermediateOutputPath=obj\headless\ -p:OutputPath=bin\headless\ || goto fail
del /q out\release\*.pdb out\dev\*.pdb out\headless\*.pdb 2>nul
popd
echo BUILD AOT OK
exit /b 0
:fail
popd
echo BUILD AOT FAILED
exit /b 1
