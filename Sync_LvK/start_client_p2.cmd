@echo off
chcp 932 >nul
title LvKSyncClient - P2
echo プレイヤー2 のクライアントです。
echo 同じPCの 1 番目の RPG_RT に接続します。
echo ローカル入力の割り当て: Up,Down,Left,Right,K,L  (上,下,左,右,A,B)
echo.
"%~dp0bin\LvKSyncClient.exe" --host 127.0.0.1 --slot 2 --index 1 --local-keys Up,Down,Left,Right,K,L
pause
