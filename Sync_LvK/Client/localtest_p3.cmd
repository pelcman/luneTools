@echo off
chcp 932 >nul
title 検証用 LvKSync クライアント - 3P (このPCの3番目のゲーム)

cls
echo ==========================================================
echo   ★ 同一PC検証用 ★   3P / このPCの3番目のゲーム
echo ==========================================================
echo.
echo   1台のPCでゲームを複数起動して試すためのものです。
echo   本番の対戦では start_client_p3.cmd を使ってください。
echo.
echo   操作キー : A S D F  と  G H
echo.
echo   ※ ゲームを 3 つ以上起動しておく必要があります。
echo      起動が古い順に 1番目, 2番目 ... と数えます。
echo.
pause
"%~dp0bin\LvKSyncClient.exe" --host 127.0.0.1 --port 47801 --slot 3 --index 2 --local-keys S,F,D,A,G,H
pause
