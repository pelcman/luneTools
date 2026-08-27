@echo off
chcp 932 >nul
setlocal
set PORT=47801
set RULE=LvKSync Server (TCP %PORT%)

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo 管理者権限が必要です。昇格します...
  powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
  exit /b
)

echo.
echo  LvKSync サーバー用のポートを開放します
echo    プロトコル : TCP
echo    ポート     : %PORT%
echo    方向       : 受信 (inbound)
echo.

netsh advfirewall firewall show rule name="%RULE%" >nul 2>&1
if %errorlevel% equ 0 (
  echo すでに開放済みです。いったん削除して作り直します。
  netsh advfirewall firewall delete rule name="%RULE%" >nul 2>&1
)

netsh advfirewall firewall add rule name="%RULE%" dir=in action=allow protocol=TCP localport=%PORT%
if errorlevel 1 (
  echo.
  echo  ** 開放に失敗しました **
  pause
  exit /b 1
)

echo.
echo  開放しました。
echo.
echo  [インターネット越しに対戦する場合]
echo   ルーターのポート転送も必要です。TCP %PORT% をこのPCへ転送してください。
echo   このバッチが設定するのは Windows ファイアウォールだけです。
echo.
echo  [プレイヤー側のPC]
echo   ポート開放は不要です。外向きの接続しかしません。
echo.
pause
