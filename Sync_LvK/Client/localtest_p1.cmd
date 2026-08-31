@echo off
chcp 932 >nul
title 検証用 LvKSync クライアント - 1P (このPCの1番目のゲーム)

cls
echo ==========================================================
echo   ★ 同一PC検証用 ★   1P / このPCの1番目のゲーム
echo ==========================================================
echo.
echo   1台のPCでゲームを複数起動して試すためのものです。
echo   本番の対戦では start_client_p1.cmd を使ってください。
echo.
echo   操作キー : 方向キー ← ↑ ↓ →  と  Z X
echo.
echo   ※ ゲームを 1 つ以上起動しておく必要があります。
echo      起動が古い順に 1番目, 2番目 ... と数えます。
echo.
pause
"%~dp0bin\LvKSyncClient.exe" --host 127.0.0.1 --port 47801 --slot 1 --index 0 --local-keys Up,Down,Left,Right,Z,X
pause
