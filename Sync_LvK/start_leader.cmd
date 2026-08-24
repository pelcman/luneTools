@echo off
chcp 932 >nul
title SyncLvK - leader
echo 操作する側 ^(leader^) です。127.0.0.1 に接続します。
echo 別PCと繋ぐときは --host に相手のIPを指定してください。
echo.
"%~dp0bin\SyncLvK.exe" --role leader --host 127.0.0.1 --index 0
pause
