@echo off
chcp 932 >nul
title LvKSync クライアント - 1P
set PORT=47801

cls
echo ==========================================================
echo   LvKSync クライアント   あなたは 1P です
echo ==========================================================
echo.
echo   あなたの操作キー :  方向キー ← ↑ ↓ →  と  Z X
echo.
echo ----------------------------------------------------------
echo   準備できていますか
echo ----------------------------------------------------------
echo.
echo    1. ゲーム ^(RPG_RT.exe^) を起動する
echo    2. 対戦画面まで進める     ^<^<^< ここ大事
echo.
echo       キャラクター選択の画面ではまだダメです。
echo       実際にキャラが並んで戦える画面まで進めてください。
echo.

rem --- ゲームが起動しているか確認 ---
set GAMEFOUND=
for /f "tokens=1" %%A in ('tasklist /FI "IMAGENAME eq RPG_RT.exe" /NH 2^>nul') do (
  if /I "%%A"=="RPG_RT.exe" set GAMEFOUND=1
)
if not defined GAMEFOUND (
  echo   [!!] ゲームがまだ起動していません。
  echo.
  echo        先にゲームを起動してから、もう一度このファイルを
  echo        実行してください。
  echo.
  pause
  exit /b
) else (
  echo   [OK] ゲームの起動を確認しました
)
echo.
echo ----------------------------------------------------------
echo.
set /p HOST=サーバーのIPを入力して Enter ^(自分のPCなら空Enter^): 
if "%HOST%"=="" set HOST=127.0.0.1
echo.
echo   %HOST%:%PORT% に接続します...
echo.

"%~dp0bin\LvKSyncClient.exe" --host %HOST% --port %PORT% --slot 1 --index 0 --local-keys Up,Down,Left,Right,Z,X

echo.
echo 終了しました。
pause
