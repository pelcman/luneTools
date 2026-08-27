@echo off
chcp 932 >nul
title LvKSync Client - P2
echo.
echo  プレイヤー2 のクライアントです。
echo  ローカル入力の割り当て: Q W E R + T Y
echo.
set /p HOST=サーバーのIP (未入力なら 127.0.0.1): 
if "%HOST%"=="" set HOST=127.0.0.1
echo.
"%~dp0bin\LvKSyncClient.exe" --host %HOST% --slot 2 --index 1 --local-keys W,R,E,Q,T,Y
pause
