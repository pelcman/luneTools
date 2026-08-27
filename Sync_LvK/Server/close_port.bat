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
echo  LvKSync サーバー用のポート開放を解除します (TCP %PORT%)
echo.

netsh advfirewall firewall show rule name="%RULE%" >nul 2>&1
if %errorlevel% neq 0 (
  echo 該当する規則がありません。すでに閉じています。
  echo.
  pause
  exit /b 0
)

netsh advfirewall firewall delete rule name="%RULE%"
if errorlevel 1 (
  echo.
  echo  ** 解除に失敗しました **
  pause
  exit /b 1
)

echo.
echo  解除しました。
echo  ルーターにポート転送を設定した場合は、そちらも忘れずに戻してください。
echo.
pause
