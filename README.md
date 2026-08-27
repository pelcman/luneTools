# luneTools

「るねキャラｖｓカワイコチャンズ」(RPG Maker 2003 / Maniacs Patch) 向けのツール置き場。

## 収録ツール

### [Sync_LvK](Sync_LvK/)

**オンライン対戦用ツール群。最大4人対応。**

```
Server/   中継サーバー + ポート開放/閉鎖の bat
Client/   プレイヤー側（ゲームと同じPCで動かす）
```

| | 役割 |
|---|---|
| `LvKSyncServer` | キー入力を仲介する中継サーバー。ゲームには触らない |
| `LvKSyncClient` | ゲームと同じPCで動かす送受信クライアント |
| `SyncLvK` | 状態同期版（ツクール改造なしで動く。デモ用） |

**ポートは TCP 47801 の1つだけ。開放が必要なのはサーバー側のPCだけ。**
`Server/open_port.bat` と `Server/close_port.bat` で管理する（管理者権限は自動昇格）。

ツクール側にネットワーク入力ブロックの受け取り処理が必要。
インタフェースは1コマンドで済む。詳細は [Sync_LvK/README.md](Sync_LvK/README.md)。

### [VPad](VPad/)

**ViGEmBus と直接 IOCTL で話す仮想ゲームパッド。** Xbox360 互換のパッドを最大4本、
ソフトウェアだけで生やす。`ViGEmClient.dll` を使わないので追加ライブラリ不要。

4人対戦の検証のように、物理パッドが足りない場面で使う。

```
VPad.exe test --pads 4                   4本挿して認識を確認
VPad.exe file --pads 4 --path pads.txt   ファイルで動的に操作
```

## ビルド

いずれも .NET Framework 4.x 同梱の `csc.exe` でビルドできる。外部依存なし。
各ツールのフォルダで `build.cmd` を実行する。
