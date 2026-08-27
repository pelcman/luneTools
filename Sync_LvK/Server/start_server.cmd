@echo off
chcp 932 >nul
title LvKSync Server
echo.
echo  キー入力を中継するサーバーです。ゲームには一切触りません。
echo  待ち受け: 0.0.0.0:47801  (最大4人)
echo.
echo  同一PC内だけで試すなら --bind 127.0.0.1 にするとFW警告が出ません。
echo.
"%~dp0bin\LvKSyncServer.exe" --bind 0.0.0.0 --players 4
pause
