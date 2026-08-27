@echo off
chcp 932 >nul
title LvKSync Client - P3
echo.
echo  プレイヤー3 のクライアントです。
echo  ローカル入力の割り当て: A S D F + G H
echo.
set /p HOST=サーバーのIP (未入力なら 127.0.0.1): 
if "%HOST%"=="" set HOST=127.0.0.1
echo.
"%~dp0bin\LvKSyncClient.exe" --host %HOST% --slot 3 --index 2 --local-keys S,F,D,A,G,H
pause
