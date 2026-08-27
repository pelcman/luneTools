@echo off
chcp 932 >nul
title LvKSync Client - P4
echo.
echo  プレイヤー4 のクライアントです。
echo  ローカル入力の割り当て: U I O P + J K
echo.
set /p HOST=サーバーのIP (未入力なら 127.0.0.1): 
if "%HOST%"=="" set HOST=127.0.0.1
echo.
"%~dp0bin\LvKSyncClient.exe" --host %HOST% --slot 4 --index 3 --local-keys I,P,O,U,J,K
pause
