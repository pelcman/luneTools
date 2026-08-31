@echo off
chcp 932 >nul
setlocal
title 動作テスト用 - ゲームを2つ起動
set BASE=%~dp0..\..

cls
echo ==========================================================
echo   動作テスト用 : 1台のPCでゲームを2つ起動します
echo ==========================================================
echo.
echo   本番の対戦では使いません。動作確認用です。
echo.
if not exist "%BASE%\LvK" (
  echo   [!!] ゲーム本体が見つかりません
  echo        探した場所 : %BASE%\LvK
  echo.
  pause
  exit /b 1
)
if not exist "%BASE%\LvK2" (
  echo   2つ目用のフォルダ ^(LvK2^) を作成しています。少し待ってください...
  xcopy /E /I /Q /Y "%BASE%\LvK" "%BASE%\LvK2" >nul
)
echo   1つ目を起動します...
start "LvK-1" /D "%BASE%\LvK" "%BASE%\LvK\RPG_RT.exe"
timeout /t 4 >nul
echo   2つ目を起動します...
start "LvK-2" /D "%BASE%\LvK2" "%BASE%\LvK2\RPG_RT.exe"
echo.
echo ----------------------------------------------------------
echo   このあとやること
echo ----------------------------------------------------------
echo.
echo    1. 両方のウィンドウを対戦画面まで進める
echo    2. Server\start_server.cmd を実行
echo    3. start_client_p1.cmd と start_client_p2.cmd を実行
echo.
echo   ※ 1台で複数動かすには、ツクール側で
echo      「ゲームのオプション設定」の Behavior when inactive = Run
echo      が設定されている必要があります。
echo.
pause
