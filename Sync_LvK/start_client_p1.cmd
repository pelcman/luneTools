@echo off
chcp 932 >nul
title LvKSyncClient - P1
echo プレイヤー1 のクライアントです。
echo 同じPCの 0 番目の RPG_RT に接続します。
echo ローカル入力の割り当て: W,S,A,D,F,G  (上,下,左,右,A,B)
echo.
"%~dp0bin\LvKSyncClient.exe" --host 127.0.0.1 --slot 1 --index 0 --local-keys W,S,A,D,F,G
pause
