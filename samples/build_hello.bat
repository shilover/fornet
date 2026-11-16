@echo off
rem build the sample lobby plugin lobby_hello.dll (LLVM toolchain, no CRT)
setlocal
cd /d "%~dp0"
set "LLVM=%LLVM_DIR%"
if "%LLVM%"=="" set "LLVM=C:\Program Files\LLVM\bin"
if not exist "%LLVM%\clang.exe" (echo clang.exe not found, set LLVM_DIR & exit /b 1)
set CFLAGS=--target=x86_64-pc-windows-msvc -O2 -Wall -ffreestanding -fno-stack-protector -fno-builtin -mno-stack-arg-probe
set LFLAGS=/nologo /dll /nodefaultlib /entry:DllMain /subsystem:windows /machine:x64
if not exist hello\out mkdir hello\out
"%LLVM%\llvm-dlltool.exe" -m i386:x86-64 -d kernel32.def -l hello\out\kernel32.lib || exit /b 1
"%LLVM%\clang.exe" %CFLAGS% -c hello\lobby_hello.c -o hello\out\lobby_hello.obj || exit /b 1
"%LLVM%\lld-link.exe" %LFLAGS% /out:hello\lobby_hello.dll hello\out\lobby_hello.obj hello\out\kernel32.lib || exit /b 1
echo BUILD hello OK
