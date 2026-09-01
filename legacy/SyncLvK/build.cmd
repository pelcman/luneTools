@echo off
chcp 932 >nul
setlocal
rem 旧: 状態同期。入力を配る方式に置き換わりました。
rem     詳しくは ..\README.md
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo csc.exe が見つかりません & exit /b 1 )
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" -nologo -platform:x64 -optimize+ -out:"%~dp0bin\SyncLvK.exe" "%~dp0src\SyncLvK.cs"
if errorlevel 1 ( echo ビルド失敗 & exit /b 1 )
echo ビルド完了
