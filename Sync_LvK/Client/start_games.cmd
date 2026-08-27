@echo off
chcp 932 >nul
setlocal
set BASE=%~dp0..\..
if not exist "%BASE%\LvK" (
  echo ゲーム本体 ^(LvK フォルダ^) が見つかりません: %BASE%\LvK
  pause
  exit /b 1
)
if not exist "%BASE%\LvK2" (
  echo LvK2 ^(2つ目のインスタンス用^) を作成しています...
  xcopy /E /I /Q /Y "%BASE%\LvK" "%BASE%\LvK2" >nul
)
echo 1つ目を起動します...
start "LvK-1" /D "%BASE%\LvK" "%BASE%\LvK\RPG_RT.exe"
timeout /t 4 >nul
echo 2つ目を起動します...
start "LvK-2" /D "%BASE%\LvK2" "%BASE%\LvK2\RPG_RT.exe"
echo.
echo 両方とも対戦画面まで進めてからクライアントを起動してください。
pause
