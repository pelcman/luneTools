# luneTools

「るねキャラｖｓカワイコチャンズ」(RPG Maker 2003 / Maniacs Patch) 向けのツール置き場。

## 収録ツール

### [Sync_LvK](Sync_LvK/)

2つのゲームインスタンス間で**キャラクターの状態を TCP 経由で同期**するツール。
同一PC上の2プロセスでも、別PC同士でも動く。接続先IPはコマンドラインと設定ファイルの
両方から差し替えられる（既定は `127.0.0.1`）。

位置だけでなくジャンプの高さ・しゃがみ・攻撃モーション・ダメージ数値まで転送される。
詳細は [Sync_LvK/README.md](Sync_LvK/README.md) を参照。

```
SyncLvK.exe --role follower --listen
SyncLvK.exe --role leader --host 192.168.1.20
```

## ビルド

いずれも .NET Framework 4.x 同梱の `csc.exe` でビルドできる。外部依存なし。
各ツールのフォルダで `build.cmd` を実行する。
