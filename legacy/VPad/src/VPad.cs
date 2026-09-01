// VPad - ViGEmBus と直接 IOCTL で話す仮想ゲームパッドツール
//
// ViGEmClient.dll を使わず、ドライバのデバイスインタフェースを直接開いて
// Xbox360 互換パッドを最大4本ぶら下げる。キーボードからの操作とスクリプト
// 実行の両方に対応。
//
// 定義の出典 (公開ヘッダ):
//   GUID_DEVINTERFACE_BUSENUM_VIGEM = {96E42B22-F5E9-42F8-B043-ED0F932F014F}
//   FILE_DEVICE_BUSENUM = FILE_DEVICE_BUS_EXTENDER (0x2A)
//   IOCTL_VIGEM_BASE = 0x801
//   BUSENUM_W_IOCTL(i) = CTL_CODE(0x2A, i, METHOD_BUFFERED, FILE_WRITE_DATA)
//
// C# 5 / .NET Framework 4.x。外部依存なし。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VPad
{
    internal static class Native
    {
        public static readonly Guid GUID_DEVINTERFACE_BUSENUM_VIGEM =
            new Guid(0x96E42B22, 0xF5E9, 0x42F8, 0xB0, 0x43, 0xED, 0x0F, 0x93, 0x2F, 0x01, 0x4F);

        // CTL_CODE(DeviceType, Function, Method, Access)
        //   = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
        private const uint FILE_DEVICE_BUS_EXTENDER = 0x0000002A;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_WRITE_DATA = 0x0002;
        private const uint IOCTL_VIGEM_BASE = 0x801;

        private static uint W(uint index)
        {
            return (FILE_DEVICE_BUS_EXTENDER << 16) | (FILE_WRITE_DATA << 14) | (index << 2) | METHOD_BUFFERED;
        }

        public static readonly uint IOCTL_VIGEM_PLUGIN_TARGET = W(IOCTL_VIGEM_BASE + 0x000);   // 0x2AA004
        public static readonly uint IOCTL_VIGEM_UNPLUG_TARGET = W(IOCTL_VIGEM_BASE + 0x001);   // 0x2AA008
        public static readonly uint IOCTL_VIGEM_CHECK_VERSION = W(IOCTL_VIGEM_BASE + 0x002);   // 0x2AA00C
        public static readonly uint IOCTL_XUSB_SUBMIT_REPORT = W(IOCTL_VIGEM_BASE + 0x201);    // 0x2AA808

        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x01;
        public const uint FILE_SHARE_WRITE = 0x02;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo,
            ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailData, uint detailSize,
            out uint requiredSize, IntPtr devInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
            byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);

        [DllImport("winmm.dll")]
        public static extern uint joyGetNumDevs();

        [StructLayout(LayoutKind.Sequential)]
        public struct JOYINFO { public uint X, Y, Z, Buttons; }

        [DllImport("winmm.dll")]
        public static extern uint joyGetPos(uint id, ref JOYINFO info);
    }

    /// <summary>Xbox360 互換の仮想パッド。ハンドルを閉じるとデバイスも消える。</summary>
    internal sealed class ViGEmBus : IDisposable
    {
        private IntPtr _h = (IntPtr)(-1);
        private readonly List<uint> _plugged = new List<uint>();

        public string DevicePath { get; private set; }

        public static string FindDevicePath()
        {
            Guid g = Native.GUID_DEVINTERFACE_BUSENUM_VIGEM;
            IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == (IntPtr)(-1)) return null;
            try
            {
                var did = new Native.SP_DEVICE_INTERFACE_DATA();
                did.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; ; i++)
                {
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
                    uint need;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need == 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        // 64bit では cbSize は 8
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                        uint got;
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, out got, IntPtr.Zero))
                            return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        public void Open()
        {
            DevicePath = FindDevicePath();
            if (DevicePath == null)
                throw new InvalidOperationException(
                    "ViGEmBus のデバイスインタフェースが見つかりません。ドライバが動作しているか確認してください。");

            _h = Native.CreateFile(DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (_h == (IntPtr)(-1))
                throw new InvalidOperationException("CreateFile に失敗しました err=" + Marshal.GetLastWin32Error());

            // バージョン確認 (失敗しても致命的ではない)
            var ver = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes((uint)8), 0, ver, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)0x0001), 0, ver, 4, 4);
            int ret;
            Native.DeviceIoControl(_h, Native.IOCTL_VIGEM_CHECK_VERSION, ver, ver.Length, null, 0, out ret, IntPtr.Zero);
        }

        /// <summary>Xbox360 互換パッドを1本追加する。serial は 1 以上。</summary>
        public void Plug(uint serial)
        {
            var b = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes((uint)16), 0, b, 0, 4);   // Size
            Buffer.BlockCopy(BitConverter.GetBytes(serial), 0, b, 4, 4);     // SerialNo
            Buffer.BlockCopy(BitConverter.GetBytes((uint)0), 0, b, 8, 4);    // TargetType = Xbox360Wired
            // VendorId / ProductId は 0 のままでドライバ既定値
            int ret;
            if (!Native.DeviceIoControl(_h, Native.IOCTL_VIGEM_PLUGIN_TARGET, b, b.Length, b, b.Length, out ret, IntPtr.Zero))
                throw new InvalidOperationException("PLUGIN_TARGET(serial=" + serial + ") 失敗 err=" + Marshal.GetLastWin32Error());
            _plugged.Add(serial);
        }

        public void Unplug(uint serial)
        {
            var b = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes((uint)8), 0, b, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(serial), 0, b, 4, 4);
            int ret;
            Native.DeviceIoControl(_h, Native.IOCTL_VIGEM_UNPLUG_TARGET, b, b.Length, b, b.Length, out ret, IntPtr.Zero);
            _plugged.Remove(serial);
        }

        /// <summary>ボタン状態を送る。buttons は XUSB_GAMEPAD_* のビット和。</summary>
        public bool Report(uint serial, ushort buttons, byte lt, byte rt, short lx, short ly, short rx, short ry)
        {
            var b = new byte[20];
            Buffer.BlockCopy(BitConverter.GetBytes((uint)20), 0, b, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(serial), 0, b, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(buttons), 0, b, 8, 2);
            b[10] = lt; b[11] = rt;
            Buffer.BlockCopy(BitConverter.GetBytes(lx), 0, b, 12, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(ly), 0, b, 14, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(rx), 0, b, 16, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(ry), 0, b, 18, 2);
            int ret;
            return Native.DeviceIoControl(_h, Native.IOCTL_XUSB_SUBMIT_REPORT, b, b.Length, b, b.Length, out ret, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (_h != (IntPtr)(-1))
            {
                foreach (var s in _plugged.ToArray()) { try { Unplug(s); } catch { } }
                Native.CloseHandle(_h);
                _h = (IntPtr)(-1);
            }
        }
    }

    internal static class Buttons
    {
        public const ushort DPAD_UP = 0x0001;
        public const ushort DPAD_DOWN = 0x0002;
        public const ushort DPAD_LEFT = 0x0004;
        public const ushort DPAD_RIGHT = 0x0008;
        public const ushort START = 0x0010;
        public const ushort BACK = 0x0020;
        public const ushort LEFT_THUMB = 0x0040;
        public const ushort RIGHT_THUMB = 0x0080;
        public const ushort LEFT_SHOULDER = 0x0100;
        public const ushort RIGHT_SHOULDER = 0x0200;
        public const ushort GUIDE = 0x0400;
        public const ushort A = 0x1000;
        public const ushort B = 0x2000;
        public const ushort X = 0x4000;
        public const ushort Y = 0x8000;

        public static ushort Parse(string name)
        {
            switch (name.Trim().ToUpperInvariant())
            {
                case "UP": return DPAD_UP;
                case "DOWN": return DPAD_DOWN;
                case "LEFT": return DPAD_LEFT;
                case "RIGHT": return DPAD_RIGHT;
                case "START": return START;
                case "BACK": return BACK;
                case "LB": return LEFT_SHOULDER;
                case "RB": return RIGHT_SHOULDER;
                case "LS": return LEFT_THUMB;
                case "RS": return RIGHT_THUMB;
                case "GUIDE": return GUIDE;
                case "A": return A;
                case "B": return B;
                case "X": return X;
                case "Y": return Y;
                default: return 0;
            }
        }

        public static string Text(ushort m)
        {
            if (m == 0) return "-";
            var sb = new StringBuilder();
            if ((m & DPAD_UP) != 0) sb.Append("Up ");
            if ((m & DPAD_DOWN) != 0) sb.Append("Down ");
            if ((m & DPAD_LEFT) != 0) sb.Append("Left ");
            if ((m & DPAD_RIGHT) != 0) sb.Append("Right ");
            if ((m & A) != 0) sb.Append("A ");
            if ((m & B) != 0) sb.Append("B ");
            if ((m & X) != 0) sb.Append("X ");
            if ((m & Y) != 0) sb.Append("Y ");
            if ((m & START) != 0) sb.Append("Start ");
            if ((m & BACK) != 0) sb.Append("Back ");
            if ((m & LEFT_SHOULDER) != 0) sb.Append("LB ");
            if ((m & RIGHT_SHOULDER) != 0) sb.Append("RB ");
            return sb.ToString().Trim();
        }
    }

    internal static class Program
    {
        private static void Usage()
        {
            Console.WriteLine();
            Console.WriteLine("VPad - ViGEmBus 直叩きの仮想ゲームパッド (Xbox360互換, 最大4本)");
            Console.WriteLine();
            Console.WriteLine("  VPad.exe info                     ドライバとデバイスの状態を表示");
            Console.WriteLine("  VPad.exe test [--pads N]          N本挿して認識を確認し、抜いて終了");
            Console.WriteLine("  VPad.exe hold --pads N <指定...>  指定したボタンを押しっぱなしにする");
            Console.WriteLine("  VPad.exe script --pads N --seq S  スクリプト実行");
            Console.WriteLine("  VPad.exe keys --pads N            キーボードで操作 (下記の割り当て)");
            Console.WriteLine("  VPad.exe file --pads N --path F   ファイルでボタン状態を指示 (動的制御用)");
            Console.WriteLine();
            Console.WriteLine("  hold の指定:   --p2 A          パッド2のAを押しっぱなし");
            Console.WriteLine("                 --p2 Up+A       複数は + でつなぐ");
            Console.WriteLine("                 --hold-ms 5000  保持時間 (既定 10000)");
            Console.WriteLine();
            Console.WriteLine("  script の書式: pad:buttons:ms を , でつなぐ");
            Console.WriteLine("                 --seq 2:A:200,2:Right:600,3:A:200");
            Console.WriteLine();
            Console.WriteLine("  keys の割り当て (パッド1〜4):");
            Console.WriteLine("     P1  W A S D / F G      P2  ↑←↓→ / K L");
            Console.WriteLine("     P3  T F G H / V B      P4  I J K L / N M");
            Console.WriteLine();
            Console.WriteLine("  ボタン名: Up Down Left Right A B X Y Start Back LB RB LS RS Guide");
            Console.WriteLine("            SUp SDown SLeft SRight (左スティック)  LT RT (トリガー)");
            Console.WriteLine();
            Console.WriteLine("  ※ パッドはプロセスが生きているあいだだけ存在します。Ctrl+C で撤去。");
            Console.WriteLine();
        }

        private static void ShowJoysticks(string label)
        {
            var info = new Native.JOYINFO();
            int n = 0;
            var ids = new List<uint>();
            for (uint i = 0; i < 16; i++)
                if (Native.joyGetPos(i, ref info) == 0) { n++; ids.Add(i); }
            Console.Write("  {0}: 接続 {1} 本", label, n);
            if (n > 0) { Console.Write("  (id="); Console.Write(string.Join(",", ids.ConvertAll(x => x.ToString()).ToArray())); Console.Write(")"); }
            Console.WriteLine();
        }

        private static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (argv.Length == 0) { Usage(); return 0; }
            string cmd = argv[0].ToLowerInvariant();
            if (cmd == "--help" || cmd == "-h" || cmd == "/?") { Usage(); return 0; }

            int pads = 1, holdMs = 10000;
            string seq = null;
            var holds = new ushort[5];
            for (int i = 1; i < argv.Length; i++)
            {
                string a = argv[i].ToLowerInvariant();
                string nx = (i + 1 < argv.Length) ? argv[i + 1] : null;
                if (a == "--pads" && nx != null) { int.TryParse(nx, out pads); i++; }
                else if (a == "--hold-ms" && nx != null) { int.TryParse(nx, out holdMs); i++; }
                else if (a == "--seq" && nx != null) { seq = nx; i++; }
                else if (a.Length == 4 && a.StartsWith("--p") && nx != null)
                {
                    int p = a[3] - '0';
                    if (p >= 1 && p <= 4)
                    {
                        ushort m = 0;
                        foreach (var part in nx.Split('+')) m |= Buttons.Parse(part);
                        holds[p] = m;
                        i++;
                    }
                }
            }
            if (pads < 1) pads = 1;
            if (pads > 4) pads = 4;

            if (cmd == "info")
            {
                Console.WriteLine("=== VPad info ===");
                string path = ViGEmBus.FindDevicePath();
                Console.WriteLine("  ViGEmBus デバイスパス: {0}", path == null ? "見つかりません" : path);
                Console.WriteLine("  joyGetNumDevs (ドライバ上限): {0}", Native.joyGetNumDevs());
                ShowJoysticks("現在");
                Console.WriteLine("  IOCTL PLUGIN=0x{0:X}  UNPLUG=0x{1:X}  SUBMIT=0x{2:X}",
                    Native.IOCTL_VIGEM_PLUGIN_TARGET, Native.IOCTL_VIGEM_UNPLUG_TARGET, Native.IOCTL_XUSB_SUBMIT_REPORT);
                return path == null ? 1 : 0;
            }

            using (var bus = new ViGEmBus())
            {
                try { bus.Open(); }
                catch (Exception ex) { Console.WriteLine(ex.Message); return 2; }
                Console.WriteLine("ViGEmBus に接続しました");
                Console.WriteLine("  {0}", bus.DevicePath);
                ShowJoysticks("挿す前");

                for (uint s = 1; s <= (uint)pads; s++)
                {
                    try { bus.Plug(s); Console.WriteLine("  パッド {0} を接続しました", s); }
                    catch (Exception ex) { Console.WriteLine("  " + ex.Message); return 3; }
                    Thread.Sleep(400);
                }
                Thread.Sleep(800);
                ShowJoysticks("挿した後");

                // 全パッドを中立状態にしておく
                for (uint s = 1; s <= (uint)pads; s++) bus.Report(s, 0, 0, 0, 0, 0, 0, 0);

                bool stop = false;
                Console.CancelKeyPress += delegate (object o, ConsoleCancelEventArgs e) { stop = true; e.Cancel = true; };

                if (cmd == "test")
                {
                    Console.WriteLine();
                    Console.WriteLine("3秒後に撤去します。");
                    Thread.Sleep(3000);
                }
                else if (cmd == "hold")
                {
                    Console.WriteLine();
                    for (int p = 1; p <= pads; p++)
                        if (holds[p] != 0) Console.WriteLine("  パッド{0}: {1} を押しっぱなし", p, Buttons.Text(holds[p]));
                    var sw = Stopwatch.StartNew();
                    while (!stop && sw.ElapsedMilliseconds < holdMs)
                    {
                        for (uint s = 1; s <= (uint)pads; s++) bus.Report(s, holds[s], 0, 0, 0, 0, 0, 0);
                        Thread.Sleep(8);
                    }
                    for (uint s = 1; s <= (uint)pads; s++) bus.Report(s, 0, 0, 0, 0, 0, 0, 0);
                }
                else if (cmd == "script")
                {
                    if (seq == null) { Console.WriteLine("--seq を指定してください"); return 4; }
                    Console.WriteLine();
                    foreach (var stepRaw in seq.Split(','))
                    {
                        if (stop) break;
                        var step = stepRaw.Trim();
                        if (step.Length == 0) continue;
                        var f = step.Split(':');
                        if (f.Length < 3) continue;
                        int p; int ms;
                        if (!int.TryParse(f[0], out p) || !int.TryParse(f[2], out ms)) continue;
                        ushort m = 0;
                        foreach (var part in f[1].Split('+')) m |= Buttons.Parse(part);
                        Console.WriteLine("  パッド{0}: {1} を {2}ms", p, Buttons.Text(m), ms);
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < ms)
                        {
                            bus.Report((uint)p, m, 0, 0, 0, 0, 0, 0);
                            Thread.Sleep(8);
                        }
                        bus.Report((uint)p, 0, 0, 0, 0, 0, 0, 0);
                        Thread.Sleep(90);
                    }
                }
                else if (cmd == "file")
                {
                    string path = null;
                    for (int i = 1; i < argv.Length; i++)
                        if (argv[i].ToLowerInvariant() == "--path" && i + 1 < argv.Length) path = argv[i + 1];
                    if (path == null) { Console.WriteLine("--path を指定してください"); return 4; }
                    Console.WriteLine();
                    Console.WriteLine("ファイル駆動モード: {0}", path);
                    Console.WriteLine("  1行につき  pad:buttons   例)  2:Up+A");
                    Console.WriteLine("  空ファイルで全パッド中立。Ctrl+C で終了。");
                    var cur = new ushort[5];
                    var shown = new ushort[5];
                    var sx = new short[5]; var sy = new short[5];
                    var tl = new byte[5]; var tr2 = new byte[5];
                    string lastText = null;
                    long tick = 0;
                    while (!stop)
                    {
                        try
                        {
                            string txt = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
                            if (txt != lastText)
                            {
                                lastText = txt;
                                for (int p2 = 1; p2 <= 4; p2++) { cur[p2] = 0; sx[p2] = 0; sy[p2] = 0; tl[p2] = 0; tr2[p2] = 0; }
                                foreach (var lineRaw in txt.Split(new char[] { (char)10 }))
                                {
                                    var line = lineRaw.Trim();
                                    if (line.Length == 0 || line[0] == '#') continue;
                                    int c = line.IndexOf(':');
                                    if (c <= 0) continue;
                                    int pi;
                                    if (!int.TryParse(line.Substring(0, c).Trim(), out pi)) continue;
                                    if (pi < 1 || pi > 4) continue;
                                    ushort m = 0;
                                    foreach (var part in line.Substring(c + 1).Split('+'))
                                    {
                                        var t = part.Trim().ToUpperInvariant();
                                        if (t == "SUP") sy[pi] = 30000;
                                        else if (t == "SDOWN") sy[pi] = -30000;
                                        else if (t == "SLEFT") sx[pi] = -30000;
                                        else if (t == "SRIGHT") sx[pi] = 30000;
                                        else if (t == "LT") tl[pi] = 255;
                                        else if (t == "RT") tr2[pi] = 255;
                                        else m |= Buttons.Parse(t);
                                    }
                                    cur[pi] = m;
                                }
                                var sb = new StringBuilder("  ");
                                for (int p2 = 1; p2 <= pads; p2++) sb.AppendFormat("P{0}:[{1}]  ", p2, Buttons.Text(cur[p2]));
                                Console.WriteLine(sb.ToString());
                                for (int p2 = 1; p2 <= 4; p2++) shown[p2] = cur[p2];
                            }
                        }
                        catch { }
                        for (uint s2 = 1; s2 <= (uint)pads; s2++)
                            bus.Report(s2, cur[s2], tl[s2], tr2[s2], sx[s2], sy[s2], 0, 0);
                        tick++;
                        Thread.Sleep(8);
                    }
                }
                else if (cmd == "keys")
                {
                    int[][] map = {
                        null,
                        new[]{ 0x57, 0x53, 0x41, 0x44, 0x46, 0x47 },   // P1 W S A D F G
                        new[]{ 0x26, 0x28, 0x25, 0x27, 0x4B, 0x4C },   // P2 ↑↓←→ K L
                        new[]{ 0x54, 0x47, 0x46, 0x48, 0x56, 0x42 },   // P3 T G F H V B
                        new[]{ 0x49, 0x4B, 0x4A, 0x4C, 0x4E, 0x4D },   // P4 I K J L N M
                    };
                    Console.WriteLine();
                    Console.WriteLine("キーボードで操作します。Ctrl+C で終了。");
                    var last = new ushort[5];
                    long tick = 0;
                    while (!stop)
                    {
                        for (int p = 1; p <= pads; p++)
                        {
                            var k = map[p];
                            ushort m = 0;
                            if ((Native.GetAsyncKeyState(k[0]) & 0x8000) != 0) m |= Buttons.DPAD_UP;
                            if ((Native.GetAsyncKeyState(k[1]) & 0x8000) != 0) m |= Buttons.DPAD_DOWN;
                            if ((Native.GetAsyncKeyState(k[2]) & 0x8000) != 0) m |= Buttons.DPAD_LEFT;
                            if ((Native.GetAsyncKeyState(k[3]) & 0x8000) != 0) m |= Buttons.DPAD_RIGHT;
                            if ((Native.GetAsyncKeyState(k[4]) & 0x8000) != 0) m |= Buttons.A;
                            if ((Native.GetAsyncKeyState(k[5]) & 0x8000) != 0) m |= Buttons.B;
                            bus.Report((uint)p, m, 0, 0, 0, 0, 0, 0);
                            if (m != last[p]) { last[p] = m; }
                        }
                        if (++tick % 120 == 0)
                        {
                            var sb = new StringBuilder("  ");
                            for (int p = 1; p <= pads; p++) sb.AppendFormat("P{0}:[{1}]  ", p, Buttons.Text(last[p]));
                            Console.WriteLine(sb.ToString());
                        }
                        Thread.Sleep(8);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("パッドを撤去します。");
            }
            Thread.Sleep(500);
            ShowJoysticks("撤去後");
            return 0;
        }
    }
}
