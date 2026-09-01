@echo off
chcp 932 >nul
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe が見つかりません: %CSC%
  exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"

echo bin\LvKPatch.exe
"%CSC%" -nologo -platform:x64 -optimize+ -target:winexe -r:System.Windows.Forms.dll -r:System.Drawing.dll -out:"%~dp0bin\LvKPatch.exe" "%~dp0..\Sync_LvK\src\GamePatch.cs" "%~dp0src\LvKPatch.cs"
if errorlevel 1 goto :fail

echo.
echo ビルド完了
exit /b 0

:fail
echo ビルド失敗
exit /b 1
