@echo off
chcp 932 >nul
title LvKSyncClient - P3
echo プレイヤー3 のクライアントです。
echo 同じPCの 2 番目の RPG_RT に接続します。
echo ローカル入力の割り当て: T,G,F,H,V,B  (上,下,左,右,A,B)
echo.
"%~dp0bin\LvKSyncClient.exe" --host 127.0.0.1 --slot 3 --index 2 --local-keys T,G,F,H,V,B
pause
