@echo off
chcp 932 >nul
setlocal EnableDelayedExpansion
title LvKSync サーバー
set PORT=47801
set RULE=LvKSync Server (TCP %PORT%)

cls
echo ==========================================================
echo   LvKSync サーバー
echo ==========================================================
echo.
echo   みんなの入力を中継する係です。
echo   このPCではゲームを動かす必要はありません。
echo.

rem --- ファイアウォールの状態を確認 ---
netsh advfirewall firewall show rule name="%RULE%" >nul 2>&1
if %errorlevel% equ 0 (
  echo   [OK] ファイアウォール : ポート %PORT% は開放済みです
) else (
  echo   [!!] ファイアウォール : ポート %PORT% がまだ閉じています
  echo.
  echo        同じフォルダの open_port.bat を先に実行してください。
  echo        ^(このまま続けると、他のPCから繋がりません^)
  echo.
  choice /C YN /M "それでも起動しますか"
  if errorlevel 2 exit /b
)
echo.

rem --- 参加者に伝えるIPアドレスを表示 ---
echo ----------------------------------------------------------
echo   参加者に伝えるアドレス
echo ----------------------------------------------------------
echo.
echo   [同じ家/同じ回線の人には この番号]
for /f "tokens=2 delims=:" %%A in ('ipconfig ^| findstr /C:"IPv4"') do (
  set IP=%%A
  set IP=!IP: =!
  echo       !IP!
)
echo.
echo   [インターネット越しの人には グローバルIP]
echo       https://www.cman.jp/network/support/go_access.cgi
echo       などで調べた番号を伝えてください。
echo       ルーターのポート転送 ^(TCP %PORT%^) も必要です。
echo.
echo ----------------------------------------------------------
echo.
echo   起動します。終了は Ctrl+C 。
echo.
pause

"%~dp0bin\LvKSyncServer.exe" --bind 0.0.0.0 --players 4 --port %PORT%
echo.
echo サーバーを終了しました。
pause
