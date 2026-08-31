# luneTools

「るねキャラｖｓカワイコチャンズ」(RPG Maker 2003 / Maniacs Patch) 向けのツール置き場。

## 収録ツール

### [Sync_LvK](Sync_LvK/)

**オンラインで対戦するためのツール。最大4人。**

使う人によって読むフォルダが違います。

| あなたは | 開くフォルダ |
|---|---|
| 対戦部屋を立てる（ホスト） | [`Sync_LvK/Server/`](Sync_LvK/Server/) |
| 対戦に参加する（プレイヤー） | [`Sync_LvK/Client/`](Sync_LvK/Client/) |

**ホスト**は `open_port.bat` → `start_server.cmd` の2つを実行するだけ。
**プレイヤー**はゲームを対戦画面まで進めてから、自分の番号の
`start_client_pN.cmd` を実行してIPを入力するだけです。

ポートは **TCP 47801 の1つだけ**で、開放が必要なのはホストのPCだけ。
プレイヤー側はファイアウォールもルーターも触りません。

> いまはツール側の通信までが完成した状態です。実際に対戦が成立するには、
> ツクール側に「届いた入力を読む」処理を1コマンド足す必要があります。
> 詳細は [Sync_LvK/README.md](Sync_LvK/README.md)。

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
