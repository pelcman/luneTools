@echo off
chcp 932 >nul
title LvKSync Client - P1
echo.
echo  プレイヤー1 のクライアントです。
echo  ローカル入力の割り当て: 方向キー + Z X
echo.
set /p HOST=サーバーのIP (未入力なら 127.0.0.1): 
if "%HOST%"=="" set HOST=127.0.0.1
echo.
"%~dp0bin\LvKSyncClient.exe" --host %HOST% --slot 1 --index 0 --local-keys Up,Down,Left,Right,Z,X
pause
