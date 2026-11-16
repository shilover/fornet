@echo off
rem Build the lobby helper with the csc.exe that ships with Windows (.NET Framework 4).
rem build.bat %FORNET_EXE%.exe (the base; the bridge transport Bridge.cs is generic and always compiled, inert without a bridge DLL)
rem build.bat release %FORNET_EXE%-release.exe = same sources (kept for the release / export scripts)
rem build.bat base %FORNET_EXE%-base.exe = same sources (tests\run_all.py builds this one)
rem FORNET_EXE = output name without .exe, default FornetLobby.
rem Game-specific C# (a local AI opponent, the game's names, its wire names for WireTag.cs) is not part of the base: set FORNET_GAME_CS
rem to a ;-separated list of absolute .cs paths and they are compiled in (the AOT project reads the same variable).
rem Without it the base builds with the neutral names and no local AI.
rem The lobby never touches any platform SDK: a bridge DLL inside the game (an extension package) opens a named pipe and the lobby only speaks that pipe.
chcp 65001 >nul
pushd "%~dp0"
if not defined FORNET_EXE set "FORNET_EXE=FornetLobby"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.IO.Compression.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll
set COMMON=Program.cs Transport.cs Mesh.cs MeshMp.cs BridgeUp.cs Ext.cs NetExtras.cs ServerLobby.cs RankedApi.cs Reliable.cs LocalAiApi.cs GameInfo.cs GamePipe.cs WireTag.cs
rem Ranked play is a plugin; no build variant carries ranked code (`base` = release)
set GAMECS=
if defined FORNET_GAME_CS set "GAMECS=%FORNET_GAME_CS:;= %"
if /i "%~1"=="release" goto release
if /i "%~1"=="base" goto base
%CSC% /out:%FORNET_EXE%.exe %COMMON% Bridge.cs BridgeRoomLink.cs %GAMECS%
goto done
:release
%CSC% /out:%FORNET_EXE%-release.exe %COMMON% Bridge.cs BridgeRoomLink.cs %GAMECS%
goto done
:base
%CSC% /out:%FORNET_EXE%-base.exe %COMMON% Bridge.cs BridgeRoomLink.cs %GAMECS%
:done
if errorlevel 1 (popd & exit /b 1)
popd
echo BUILD OK
