@echo off
chcp 932 >nul
setlocal
rem 旧: 画面なしのコマンド版。ふだんの対戦では使いません。
rem     プロトコルが古く、設定メニューの同期などを知りません。
rem     詳しくは ..\README.md
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set COMMON=%~dp0..\..\Sync_LvK\src\Common.cs
if not exist "%CSC%" ( echo csc.exe が見つかりません & exit /b 1 )
if not exist "%COMMON%" ( echo Common.cs が見つかりません: %COMMON% & exit /b 1 )
if not exist "%~dp0bin" mkdir "%~dp0bin"

echo bin\LvKSyncServer.exe
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\LvKSyncServer.exe" "%COMMON%" "%~dp0src\Server.cs"
if errorlevel 1 goto :fail

echo bin\LvKSyncClient.exe
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\LvKSyncClient.exe" "%COMMON%" "%~dp0src\Client.cs"
if errorlevel 1 goto :fail

echo.
echo ビルド完了
exit /b 0

:fail
echo ビルド失敗
exit /b 1
