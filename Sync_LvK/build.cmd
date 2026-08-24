@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe が見つかりません: %CSC%
  exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\SyncLvK.exe" "%~dp0src\SyncLvK.cs"
if errorlevel 1 (
  echo ビルド失敗
  exit /b 1
)
echo ビルド成功: %~dp0bin\SyncLvK.exe
