@echo off
chcp 932 >nul
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe が見つかりません: %CSC%
  exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"

echo [1/3] LvKSyncServer
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\LvKSyncServer.exe" "%~dp0src\Common.cs" "%~dp0src\Server.cs"
if errorlevel 1 goto :fail

echo [2/3] LvKSyncClient
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\LvKSyncClient.exe" "%~dp0src\Common.cs" "%~dp0src\Client.cs"
if errorlevel 1 goto :fail

echo [3/3] SyncLvK ^(旧: 状態同期・2人^)
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\SyncLvK.exe" "%~dp0src\SyncLvK.cs"
if errorlevel 1 goto :fail

echo.
echo ビルド成功: %~dp0bin
exit /b 0

:fail
echo ビルド失敗
exit /b 1
