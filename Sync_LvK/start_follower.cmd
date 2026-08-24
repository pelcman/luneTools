@echo off
chcp 932 >nul
title SyncLvK - follower
echo 同期される側 ^(follower^) です。待ち受けます。
echo 2つ目に起動したゲームを対戦画面まで進めておいてください。
echo.
"%~dp0bin\SyncLvK.exe" --role follower --listen --bind 127.0.0.1 --index 1
pause
