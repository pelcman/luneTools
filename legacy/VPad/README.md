# VPad

**ViGEmBus と直接 IOCTL で話す仮想ゲームパッドツール。** Xbox360 互換のパッドを
最大4本、ソフトウェアだけで生やす。

`ViGEmClient.dll` を使わない。ドライバのデバイスインタフェースを自分で開いて
IOCTL を投げるので、**追加のライブラリを一切ダウンロードせずに動く**。

## 必要なもの

- [ViGEmBus](https://vigembusdriver.com/) ドライバがインストール済みであること
- Windows / .NET Framework 4.x

## 使い方

```
VPad.exe info                      ドライバとデバイスの状態を表示
VPad.exe test --pads 4             4本挿して認識を確認し、抜いて終了
VPad.exe keys --pads 4             キーボードで操作
VPad.exe hold --pads 2 --p2 Up+A   指定ボタンを押しっぱなし
VPad.exe script --pads 2 --seq 2:A:200,2:Right:600
VPad.exe file --pads 4 --path pads.txt   ファイルでボタン状態を指示
```

### file モード（外部から動的に操作する用）

`pads.txt` に1行ずつ `pad:buttons` と書くと、その状態が即座に反映される。
空ファイルで全パッド中立。

```
2:Up+A
3:SRight
```

### ボタン名

```
Up Down Left Right A B X Y Start Back LB RB LS RS Guide
SUp SDown SLeft SRight   左スティック
LT RT                    トリガー
```

### keys モードの割り当て

```
P1  W A S D / F G        P2  ↑←↓→ / K L
P3  T F G H / V B        P4  I J K L / N M
```

## 注意

**パッドはプロセスが生きているあいだだけ存在する。** VPad を終了すると
Windows からデバイスが消える。

## 実装メモ

公開ヘッダから起こした定義:

```
GUID_DEVINTERFACE_BUSENUM_VIGEM = {96E42B22-F5E9-42F8-B043-ED0F932F014F}
FILE_DEVICE_BUSENUM = FILE_DEVICE_BUS_EXTENDER (0x2A)
IOCTL_VIGEM_BASE = 0x801
BUSENUM_W_IOCTL(i) = CTL_CODE(0x2A, i, METHOD_BUFFERED, FILE_WRITE_DATA)

  PLUGIN_TARGET  0x2AA004
  UNPLUG_TARGET  0x2AA008
  CHECK_VERSION  0x2AA00C
  XUSB_SUBMIT    0x2AA808
```

デバイスパスは SetupAPI で `GUID_DEVINTERFACE_BUSENUM_VIGEM` を列挙して取得する。

## ビルド

```
build.cmd
```
