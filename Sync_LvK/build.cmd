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

echo [1/2] Server\bin\LvKSyncServerGui.exe ^(画面あり^)
"%CSC%" -nologo -platform:x64 -optimize+ -target:winexe %GUIREF% -out:"%~dp0Server\bin\LvKSyncServerGui.exe" "%~dp0src\Common.cs" "%~dp0src\InputView.cs" "%~dp0src\ServerGui.cs"
if errorlevel 1 goto :fail

echo [2/2] Client\bin\LvKSyncClientGui.exe ^(画面あり^)
"%CSC%" -nologo -platform:x64 -optimize+ -target:winexe %GUIREF% -out:"%~dp0Client\bin\LvKSyncClientGui.exe" "%~dp0src\Common.cs" "%~dp0src\InputView.cs" "%~dp0src\GamePatch.cs" "%~dp0src\ClientGui.cs"
if errorlevel 1 goto :fail

echo.
echo ビルド完了
exit /b 0

:fail
echo ビルド失敗
exit /b 1
