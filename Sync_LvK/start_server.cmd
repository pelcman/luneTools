@echo off
chcp 932 >nul
title LvKSyncServer
echo キー入力を中継するサーバーです。
echo 同一PC内だけで試すなら、このまま (127.0.0.1 で待ち受け) でOKです。
echo 別PCから繋ぐ場合は --bind 0.0.0.0 に変えてファイアウォールを許可してください。
echo.
"%~dp0bin\LvKSyncServer.exe" --bind 127.0.0.1 --players 4
pause
