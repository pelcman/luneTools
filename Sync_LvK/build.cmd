@echo off
chcp 932 >nul
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe が見つかりません: %CSC%
  exit /b 1
)
if not exist "%~dp0Server\bin" mkdir "%~dp0Server\bin"
if not exist "%~dp0Client\bin" mkdir "%~dp0Client\bin"

set GUIREF=-r:System.Windows.Forms.dll -r:System.Drawing.dll

echo [1/5] Server\bin\LvKSyncServerGui.exe ^(画面あり^)
"%CSC%" -nologo -platform:x64 -optimize+ -target:winexe %GUIREF% -out:"%~dp0Server\bin\LvKSyncServerGui.exe" "%~dp0src\Common.cs" "%~dp0src\ServerGui.cs"
if errorlevel 1 goto :fail

echo [2/5] Client\bin\LvKSyncClientGui.exe ^(画面あり^)
"%CSC%" -nologo -platform:x64 -optimize+ -target:winexe %GUIREF% -out:"%~dp0Client\bin\LvKSyncClientGui.exe" "%~dp0src\Common.cs" "%~dp0src\ClientGui.cs"
if errorlevel 1 goto :fail

echo [3/5] Server\bin\LvKSyncServer.exe ^(コマンド版^)
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0Server\bin\LvKSyncServer.exe" "%~dp0src\Common.cs" "%~dp0src\Server.cs"
if errorlevel 1 goto :fail

echo [4/5] Client\bin\LvKSyncClient.exe ^(コマンド版^)
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0Client\bin\LvKSyncClient.exe" "%~dp0src\Common.cs" "%~dp0src\Client.cs"
if errorlevel 1 goto :fail

echo [5/5] Client\bin\SyncLvK.exe ^(旧: 状態同期^)
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0Client\bin\SyncLvK.exe" "%~dp0src\SyncLvK.cs"
if errorlevel 1 goto :fail

echo.
echo ビルド完了
exit /b 0

:fail
echo ビルド失敗
exit /b 1
