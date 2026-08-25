@echo off
chcp 932 >nul
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\VPad.exe" "%~dp0src\VPad.cs"
if errorlevel 1 ( echo ビルド失敗 & exit /b 1 )
echo ビルド成功: %~dp0bin\VPad.exe
