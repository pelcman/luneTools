@echo off
chcp 932 >nul
setlocal
set BASE=%~dp0..
if not exist "%BASE%\LvK2" (
  echo LvK2 ^(2つ目のインスタンス用^) を作成しています...
  xcopy /E /I /Q /Y "%BASE%\LvK" "%BASE%\LvK2" >nul
)
echo 1つ目 ^(leader / 自分が操作する側^) を起動します...
start "LvK-leader" /D "%BASE%\LvK" "%BASE%\LvK\RPG_RT.exe"
timeout /t 4 >nul
echo 2つ目 ^(follower / 同期される側^) を起動します...
start "LvK-follower" /D "%BASE%\LvK2" "%BASE%\LvK2\RPG_RT.exe"
echo.
echo 両方とも対戦画面まで進めてから、start_follower.cmd -^> start_leader.cmd の順に実行してください。
pause
