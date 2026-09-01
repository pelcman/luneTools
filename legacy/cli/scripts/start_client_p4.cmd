@echo off
chcp 932 >nul
title LvKSync クライアント - 4P
set PORT=47801

cls
echo ==========================================================
echo   LvKSync クライアント   あなたは 4P です
echo ==========================================================
echo.
echo   あなたの操作キー :  U I O P  と  J K
echo.
echo   このPCで動いているゲーム1つに接続します。
echo.
echo ----------------------------------------------------------
echo   準備できていますか
echo ----------------------------------------------------------
echo.
echo    1. ゲーム ^(RPG_RT.exe^) を起動する  ^(タイトル画面のままでOK^)
echo    2. このツールを起動する            ^<^<^< 今ここ
echo    3. そのあとキャラクター選択へ進む
echo.
echo       キャラクター選択より前に接続しておいてください。
echo       全員のカーソルを合わせる必要があるためです。
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

"%~dp0bin\LvKSyncClient.exe" --host %HOST% --port %PORT% --slot 4 --index 0 --local-keys O,I,P,U,J,K

echo.
echo 終了しました。
pause
