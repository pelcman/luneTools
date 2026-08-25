@echo off
chcp 932 >nul
title LvKSyncClient - P4
echo プレイヤー4 のクライアントです。
echo 同じPCの 3 番目の RPG_RT に接続します。
echo ローカル入力の割り当て: I,K,J,L,N,M  (上,下,左,右,A,B)
echo.
"%~dp0bin\LvKSyncClient.exe" --host 127.0.0.1 --slot 4 --index 3 --local-keys I,K,J,L,N,M
pause
