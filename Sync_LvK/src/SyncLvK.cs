// SyncLvK - るねキャラvsカワイコチャンズ (RPG Maker 2003 / Maniacs) 状態同期ツール
//
// 2台(または同一PC上の2プロセス)の RPG_RT のあいだで、キャラクターの状態変数を
// TCP 経由で複製する。接続先IP・ポート・同期対象の変数は ini とコマンドラインの
// 両方から変更できる。
//
// C# 5 / .NET Framework 4.x 向け。外部依存なし。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SyncLvK
{
    #region プロセスメモリ

    internal static class Native
    {
        public const int PROCESS_VM_READ = 0x0010;
        public const int PROCESS_VM_WRITE = 0x0020;
        public const int PROCESS_VM_OPERATION = 0x0008;
        public const int PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);

        [DllImport("kernel32.dll")]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);

        [DllImport("kernel32.dll")]
        public static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, int len);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        public const uint MEM_COMMIT = 0x1000;
        public const uint PAGE_GUARD = 0x100;
    }

    /// <summary>RPG_RT のプロセスメモリに対する読み書き。</summary>
    internal sealed class GameMemory : IDisposable
    {
        private IntPtr _h;
        public int Pid { get; private set; }
        public long VarBase { get; private set; }

        private readonly byte[] _scratch4 = new byte[4];

        public GameMemory(int pid)
        {
            Pid = pid;
            _h = Native.OpenProcess(
                Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE |
                Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION, false, pid);
            if (_h == IntPtr.Zero)
                throw new InvalidOperationException(
                    "OpenProcess に失敗しました (pid=" + pid + ", err=" + Marshal.GetLastWin32Error() +
                    ")。管理者権限が必要な場合があります。");
        }

        public bool IsValid { get { return _h != IntPtr.Zero; } }

        public void SetVarBase(long b) { VarBase = b; }

        public int ReadVar(int index)
        {
            int got;
            if (!Native.ReadProcessMemory(_h, (IntPtr)(VarBase + (long)index * 4), _scratch4, 4, out got)) return 0;
            return BitConverter.ToInt32(_scratch4, 0);
        }

        public bool ReadVars(int[] indices, int[] dest)
        {
            for (int i = 0; i < indices.Length; i++) dest[i] = ReadVar(indices[i]);
            return true;
        }

        public void WriteVar(int index, int value)
        {
            int wrote;
            Native.WriteProcessMemory(_h, (IntPtr)(VarBase + (long)index * 4), BitConverter.GetBytes(value), 4, out wrote);
        }

        public void WriteVars(int[] indices, int[] values)
        {
            for (int i = 0; i < indices.Length; i++) WriteVar(indices[i], values[i]);
        }

        public bool ReadRaw(long addr, byte[] buf, int size)
        {
            int got;
            return Native.ReadProcessMemory(_h, (IntPtr)addr, buf, size, out got) && got == size;
        }

        internal sealed class Region { public long Base; public int Size; public byte[] Data; }

        /// <summary>書き込み可能なコミット済み領域を列挙する。</summary>
        internal List<Region> Snapshot(bool withData)
        {
            var list = new List<Region>();
            long addr = 0x10000;
            const long max = 0x7FFFFFFF0000;
            long total = 0;
            while (addr < max)
            {
                Native.MEMORY_BASIC_INFORMATION mbi;
                if (Native.VirtualQueryEx(_h, (IntPtr)addr, out mbi,
                        Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION))) == 0) break;
                long rs = (long)mbi.RegionSize;
                if (rs <= 0) break;

                uint pr = mbi.Protect & 0xFF;
                bool writable = (pr == 0x04 || pr == 0x40 || pr == 0x08 || pr == 0x80);
                bool guarded = (mbi.Protect & Native.PAGE_GUARD) != 0;
                bool ok = mbi.State == Native.MEM_COMMIT && writable && !guarded
                          && rs >= 0x1000 && rs <= 512L * 1024 * 1024;

                if (ok && total < 3000L * 1024 * 1024)
                {
                    var r = new Region();
                    r.Base = (long)mbi.BaseAddress;
                    r.Size = (int)rs;
                    if (withData)
                    {
                        r.Data = new byte[r.Size];
                        int got;
                        if (!Native.ReadProcessMemory(_h, mbi.BaseAddress, r.Data, r.Size, out got) || got != r.Size)
                            r.Data = null;
                    }
                    if (!withData || r.Data != null) { list.Add(r); total += rs; }
                }
                addr += rs;
            }
            return list;
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { Native.CloseHandle(_h); _h = IntPtr.Zero; }
        }
    }

    #endregion

    #region 変数配列ベースの特定

    /// <summary>
    /// RPG_RT の変数配列の先頭アドレスを、2種類のシグネチャで探す。
    /// ASLR があるため起動のたびに変わる。対戦中でないと成立しない点に注意。
    /// </summary>
    internal static class VarBaseFinder
    {
        private const int Sentinel = -999999999;
        private static readonly int[] SentinelIdx = { 20001, 20002, 20201, 20202, 20401, 20402, 20601, 20602 };
        private static readonly int[] AntiIdx = { 20000, 20003, 20200, 20203 };
        public const int TickVar = 654;

        public static long Find(GameMemory mem, out string method)
        {
            method = null;
            var regs = mem.Snapshot(true);
            var buf = new byte[4];

            // 手がかり 1: センチネル値 -999999999 が 200 刻みで並ぶブロック
            foreach (var r in regs)
            {
                for (int off = 0; off + 4 <= r.Size; off += 4)
                {
                    if (BitConverter.ToInt32(r.Data, off) != Sentinel) continue;
                    long b0 = r.Base + off - 20001L * 4;
                    bool ok = true;
                    foreach (int k in SentinelIdx)
                    {
                        if (!mem.ReadRaw(b0 + (long)k * 4, buf, 4) || BitConverter.ToInt32(buf, 0) != Sentinel) { ok = false; break; }
                    }
                    if (ok)
                    {
                        foreach (int k in AntiIdx)
                        {
                            if (mem.ReadRaw(b0 + (long)k * 4, buf, 4) && BitConverter.ToInt32(buf, 0) == Sentinel) { ok = false; break; }
                        }
                    }
                    if (ok && TickAdvancing(mem, b0)) { method = "sentinel"; return b0; }
                }
            }

            // 手がかり 2: V[1001..1020] に 10201..10220 の連番が入る索引テーブル
            foreach (var r in regs)
            {
                for (int off = 0; off + 80 <= r.Size; off += 4)
                {
                    if (BitConverter.ToInt32(r.Data, off) != 10201) continue;
                    bool run = true;
                    for (int k = 1; k < 20; k++)
                    {
                        if (BitConverter.ToInt32(r.Data, off + k * 4) != 10201 + k) { run = false; break; }
                    }
                    if (!run) continue;
                    long b0 = r.Base + off - 1001L * 4;
                    if (TickAdvancing(mem, b0)) { method = "index-table"; return b0; }
                }
            }
            return 0;
        }

        private static bool TickAdvancing(GameMemory mem, long b0)
        {
            var buf = new byte[4];
            if (!mem.ReadRaw(b0 + (long)TickVar * 4, buf, 4)) return false;
            int t1 = BitConverter.ToInt32(buf, 0);
            if (t1 <= 0 || t1 > 50000000) return false;
            Thread.Sleep(120);
            if (!mem.ReadRaw(b0 + (long)TickVar * 4, buf, 4)) return false;
            return BitConverter.ToInt32(buf, 0) > t1;
        }
    }

    #endregion

    #region 設定

    internal sealed class Config
    {
        public string Host = "127.0.0.1";
        public string Bind = "0.0.0.0";         // listen 時のバインド先。127.0.0.1 なら FW 警告が出ない
        public int Port = 47801;
        public bool Listen = false;
        public string Role = "leader";          // leader / follower / peer
        public int Pid = 0;                     // 0 = 自動検出
        public int Index = 0;                   // 同名プロセスが複数あるとき何番目か
        public string SendVars = DefaultVars;
        public string RecvVars = DefaultVars;
        public int StatusMs = 1000;
        public bool Verbose = false;

        // 実測でキャラクターの状態が入っていた範囲
        public const string DefaultVars =
            "10191-10211,10228-10282,10382-10399,22201-22241,22252-22260,22290-22299,22401-22441,22453-22460,22490-22499";

        public static Config Load(string path)
        {
            var c = new Config();
            if (!File.Exists(path)) return c;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "host": c.Host = v; break;
                    case "bind": c.Bind = v; break;
                    case "port": int.TryParse(v, out c.Port); break;
                    case "listen": c.Listen = ParseBool(v); break;
                    case "role": c.Role = v.ToLowerInvariant(); break;
                    case "pid": int.TryParse(v, out c.Pid); break;
                    case "index": int.TryParse(v, out c.Index); break;
                    case "sendvars": c.SendVars = v; break;
                    case "recvvars": c.RecvVars = v; break;
                    case "statusms": int.TryParse(v, out c.StatusMs); break;
                    case "verbose": c.Verbose = ParseBool(v); break;
                }
            }
            return c;
        }

        private static bool ParseBool(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        public static void WriteDefault(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SyncLvK 設定ファイル");
            sb.AppendLine("# コマンドライン引数のほうが優先されます。");
            sb.AppendLine();
            sb.AppendLine("[network]");
            sb.AppendLine("# 接続先IP。listen=true のときは無視されます。");
            sb.AppendLine("host = 127.0.0.1");
            sb.AppendLine("# listen 時のバインド先。同一PC内だけで試すなら 127.0.0.1 にすると");
            sb.AppendLine("# ファイアウォールの警告が出ません。別PCと繋ぐときは 0.0.0.0。");
            sb.AppendLine("bind = 0.0.0.0");
            sb.AppendLine("port = 47801");
            sb.AppendLine("# true にすると待ち受け側(サーバ)になります。");
            sb.AppendLine("listen = false");
            sb.AppendLine();
            sb.AppendLine("[sync]");
            sb.AppendLine("# leader   = 自分の状態を送るだけ");
            sb.AppendLine("# follower = 受け取った状態を書き込むだけ");
            sb.AppendLine("# peer     = 双方向 (sendvars を送り recvvars を受け取る)");
            sb.AppendLine("role = leader");
            sb.AppendLine();
            sb.AppendLine("# 同期する変数。'a-b' の範囲指定とカンマ区切りが使えます。");
            sb.AppendLine("sendvars = " + DefaultVars);
            sb.AppendLine("recvvars = " + DefaultVars);
            sb.AppendLine();
            sb.AppendLine("[process]");
            sb.AppendLine("# 0 なら RPG_RT.exe を自動検出します。");
            sb.AppendLine("pid = 0");
            sb.AppendLine("# RPG_RT が複数あるときに何番目を使うか (起動が古い順に 0,1,...)");
            sb.AppendLine("index = 0");
            sb.AppendLine();
            sb.AppendLine("[misc]");
            sb.AppendLine("statusms = 1000");
            sb.AppendLine("verbose = false");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
    }

    #endregion

    internal static class Program
    {
        private const uint Magic = 0x534B564C; // "LVKS"

        private static int[] ParseVars(string spec)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(spec)) return list.ToArray();
            foreach (var partRaw in spec.Split(','))
            {
                var part = partRaw.Trim();
                if (part.Length == 0) continue;
                int dash = part.IndexOf('-', 1);
                if (dash > 0)
                {
                    int lo, hi;
                    if (int.TryParse(part.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out lo) &&
                        int.TryParse(part.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out hi))
                    {
                        if (hi < lo) { int t = lo; lo = hi; hi = t; }
                        for (int v = lo; v <= hi; v++) list.Add(v);
                    }
                }
                else
                {
                    int v;
                    if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) list.Add(v);
                }
            }
            return list.ToArray();
        }

        private static void Usage()
        {
            Console.WriteLine();
            Console.WriteLine("SyncLvK - LvK 状態同期ツール");
            Console.WriteLine();
            Console.WriteLine("  SyncLvK.exe [オプション]");
            Console.WriteLine();
            Console.WriteLine("  --host <ip>        接続先IP (既定 127.0.0.1)");
            Console.WriteLine("  --port <n>         ポート (既定 47801)");
            Console.WriteLine("  --bind <ip>        待ち受けアドレス (既定 0.0.0.0 / 同一PC内なら 127.0.0.1)");
            Console.WriteLine("  --listen           待ち受け側になる");
            Console.WriteLine("  --role <r>         leader | follower | peer");
            Console.WriteLine("  --pid <n>          対象プロセスID (省略時は自動検出)");
            Console.WriteLine("  --index <n>        RPG_RT が複数あるとき何番目か (既定 0)");
            Console.WriteLine("  --send-vars <s>    送信する変数 (例 10191-10211,22201-22241)");
            Console.WriteLine("  --recv-vars <s>    受信して書き込む変数");
            Console.WriteLine("  --config <path>    設定ファイル (既定 exe と同じ場所の SyncLvK.ini)");
            Console.WriteLine("  --list             起動中の RPG_RT を一覧表示して終了");
            Console.WriteLine("  --verbose          詳細ログ");
            Console.WriteLine("  --help             このヘルプ");
            Console.WriteLine();
            Console.WriteLine("  例) 同一PCで試す:");
            Console.WriteLine("      1つ目  SyncLvK.exe --role follower --listen --bind 127.0.0.1 --index 1");
            Console.WriteLine("      2つ目  SyncLvK.exe --role leader   --index 0");
            Console.WriteLine();
            Console.WriteLine("  例) 別PCと繋ぐ:");
            Console.WriteLine("      受け側  SyncLvK.exe --role follower --listen");
            Console.WriteLine("      送り側  SyncLvK.exe --role leader --host 192.168.1.20");
            Console.WriteLine();
        }

        private static List<Process> FindGames()
        {
            var list = new List<Process>();
            foreach (var p in Process.GetProcessesByName("RPG_RT"))
            {
                try { if (!p.HasExited) list.Add(p); }
                catch { }
            }
            list.Sort(delegate (Process a, Process b)
            {
                try { return a.StartTime.CompareTo(b.StartTime); }
                catch { return a.Id.CompareTo(b.Id); }
            });
            return list;
        }

        private static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string cfgPath = Path.Combine(exeDir, "SyncLvK.ini");

            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "--config" && i + 1 < argv.Length) cfgPath = argv[i + 1];

            if (!File.Exists(cfgPath))
            {
                Config.WriteDefault(cfgPath);
                Console.WriteLine("設定ファイルを作成しました: " + cfgPath);
            }
            var cfg = Config.Load(cfgPath);

            bool listOnly = false;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string next = (i + 1 < argv.Length) ? argv[i + 1] : null;
                switch (a)
                {
                    case "--help": case "-h": case "/?": Usage(); return 0;
                    case "--list": listOnly = true; break;
                    case "--listen": cfg.Listen = true; break;
                    case "--verbose": cfg.Verbose = true; break;
                    case "--host": if (next != null) { cfg.Host = next; i++; } break;
                    case "--bind": if (next != null) { cfg.Bind = next; i++; } break;
                    case "--port": if (next != null) { int.TryParse(next, out cfg.Port); i++; } break;
                    case "--role": if (next != null) { cfg.Role = next.ToLowerInvariant(); i++; } break;
                    case "--pid": if (next != null) { int.TryParse(next, out cfg.Pid); i++; } break;
                    case "--index": if (next != null) { int.TryParse(next, out cfg.Index); i++; } break;
                    case "--send-vars": if (next != null) { cfg.SendVars = next; i++; } break;
                    case "--recv-vars": if (next != null) { cfg.RecvVars = next; i++; } break;
                    case "--config": i++; break;
                }
            }

            var games = FindGames();
            if (listOnly)
            {
                Console.WriteLine("起動中の RPG_RT: " + games.Count + " 個");
                for (int i = 0; i < games.Count; i++)
                {
                    string title = "";
                    try { title = games[i].MainWindowTitle; } catch { }
                    Console.WriteLine("  index={0}  pid={1}  {2}", i, games[i].Id, title);
                }
                return 0;
            }

            bool send = (cfg.Role == "leader" || cfg.Role == "peer");
            bool recv = (cfg.Role == "follower" || cfg.Role == "peer");
            if (!send && !recv)
            {
                Console.WriteLine("role が不正です: " + cfg.Role + " (leader / follower / peer)");
                return 2;
            }

            int[] sendVars = send ? ParseVars(cfg.SendVars) : new int[0];
            int[] recvVars = recv ? ParseVars(cfg.RecvVars) : new int[0];

            Console.WriteLine("=== SyncLvK ===");
            Console.WriteLine("  role     : {0}", cfg.Role);
            Console.WriteLine("  network  : {0}  {1}:{2}", cfg.Listen ? "listen" : "connect",
                cfg.Listen ? cfg.Bind : cfg.Host, cfg.Port);
            Console.WriteLine("  vars     : send {0} / recv {1}", sendVars.Length, recvVars.Length);
            Console.WriteLine("  config   : {0}", cfgPath);
            Console.WriteLine();

            // --- 対象プロセスを決める ---
            int pid = cfg.Pid;
            if (pid == 0)
            {
                Console.Write("RPG_RT を待っています");
                while (true)
                {
                    games = FindGames();
                    if (games.Count > cfg.Index) { pid = games[cfg.Index].Id; break; }
                    Console.Write(".");
                    Thread.Sleep(700);
                }
                Console.WriteLine();
            }
            Console.WriteLine("対象プロセス: pid={0} (index={1}, 検出 {2} 個)", pid, cfg.Index, games.Count);

            GameMemory mem;
            try { mem = new GameMemory(pid); }
            catch (Exception ex) { Console.WriteLine(ex.Message); return 3; }

            // --- 変数配列のベースを探す（対戦画面に入るまで見つからない） ---
            Console.WriteLine("変数配列を探しています… ゲームを対戦画面まで進めてください。");
            long vb = 0;
            string method = null;
            while (vb == 0)
            {
                vb = VarBaseFinder.Find(mem, out method);
                if (vb == 0) { Console.Write("."); Thread.Sleep(600); }
            }
            Console.WriteLine();
            mem.SetVarBase(vb);
            Console.WriteLine("変数配列ベース: 0x{0:X}  ({1})", vb, method);
            Console.WriteLine("現在のフレーム: V[654] = {0}", mem.ReadVar(VarBaseFinder.TickVar));
            Console.WriteLine();

            // --- 接続 ---
            TcpClient client = null;
            TcpListener listener = null;
            try
            {
                if (cfg.Listen)
                {
                    IPAddress bindAddr;
                    if (!IPAddress.TryParse(cfg.Bind, out bindAddr)) bindAddr = IPAddress.Any;
                    listener = new TcpListener(bindAddr, cfg.Port);
                    listener.Start();
                    Console.WriteLine("待ち受け中 {0}:{1} … 相手の接続を待っています", bindAddr, cfg.Port);
                    client = listener.AcceptTcpClient();
                }
                else
                {
                    Console.Write("接続しています {0}:{1} ", cfg.Host, cfg.Port);
                    for (int i = 0; i < 120 && client == null; i++)
                    {
                        try { var c = new TcpClient(); c.Connect(cfg.Host, cfg.Port); client = c; }
                        catch { Console.Write("."); Thread.Sleep(500); }
                    }
                    Console.WriteLine();
                    if (client == null) { Console.WriteLine("接続できませんでした。"); return 4; }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ネットワークエラー: " + ex.Message);
                return 4;
            }

            client.NoDelay = true;
            var stream = client.GetStream();
            Console.WriteLine("接続しました: {0}", client.Client.RemoteEndPoint);
            Console.WriteLine("Ctrl+C で終了します。");
            Console.WriteLine();

            var session = new Session(mem, stream, sendVars, recvVars, cfg);
            session.Run();

            try { if (client != null) client.Close(); } catch { }
            try { if (listener != null) listener.Stop(); } catch { }
            mem.Dispose();
            return 0;
        }

        /// <summary>送受信ループ。</summary>
        private sealed class Session
        {
            private readonly GameMemory _mem;
            private readonly NetworkStream _stream;
            private readonly int[] _sendVars, _recvVars;
            private readonly Config _cfg;

            private volatile int[] _latest;
            private volatile int _latestTick = -1;
            private long _recvCount, _sendCount, _applyCount;
            private volatile bool _stop;

            public Session(GameMemory mem, NetworkStream stream, int[] sendVars, int[] recvVars, Config cfg)
            {
                _mem = mem; _stream = stream; _sendVars = sendVars; _recvVars = recvVars; _cfg = cfg;
            }

            public void Run()
            {
                Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { _stop = true; e.Cancel = true; };

                Thread rx = null;
                if (_recvVars.Length > 0)
                {
                    rx = new Thread(ReceiveLoop);
                    rx.IsBackground = true;
                    rx.Start();
                }

                var sendBuf = new byte[12 + _sendVars.Length * 4];
                var vals = new int[_sendVars.Length];
                var sw = Stopwatch.StartNew();
                long nextStatus = _cfg.StatusMs;
                int lastTick = -1;
                long lastSend = 0, lastRecv = 0, lastApply = 0;

                while (!_stop)
                {
                    int tick = _mem.ReadVar(VarBaseFinder.TickVar);

                    // 送信は1フレームにつき1回
                    if (_sendVars.Length > 0 && tick != lastTick)
                    {
                        lastTick = tick;
                        _mem.ReadVars(_sendVars, vals);
                        Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, sendBuf, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(tick), 0, sendBuf, 4, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(_sendVars.Length), 0, sendBuf, 8, 4);
                        for (int i = 0; i < vals.Length; i++)
                            Buffer.BlockCopy(BitConverter.GetBytes(vals[i]), 0, sendBuf, 12 + i * 4, 4);
                        try { _stream.Write(sendBuf, 0, sendBuf.Length); _sendCount++; }
                        catch { Console.WriteLine("送信が切断されました。"); break; }
                    }

                    // 受信した値は届き次第、毎周回で書き戻す
                    if (_recvVars.Length > 0)
                    {
                        var v = _latest;
                        if (v != null && v.Length == _recvVars.Length)
                        {
                            _mem.WriteVars(_recvVars, v);
                            _applyCount++;
                        }
                    }

                    if (sw.ElapsedMilliseconds >= nextStatus)
                    {
                        double sec = _cfg.StatusMs / 1000.0;
                        long ds = _sendCount - lastSend, dr = _recvCount - lastRecv, da = _applyCount - lastApply;
                        lastSend = _sendCount; lastRecv = _recvCount; lastApply = _applyCount;
                        string extra = "";
                        if (_recvVars.Length > 0 && _latest != null && _latest.Length > 0)
                            extra = string.Format("  peer V[{0}]={1}", _recvVars[0], _latest[0]);
                        else if (_sendVars.Length > 0)
                            extra = string.Format("  V[{0}]={1}", _sendVars[0], _mem.ReadVar(_sendVars[0]));
                        Console.WriteLine("frame {0,-7} tx {1,5:F1}/s  rx {2,5:F1}/s  apply {3,7:F0}/s{4}",
                            tick, ds / sec, dr / sec, da / sec, extra);
                        nextStatus += _cfg.StatusMs;
                    }
                }

                _stop = true;
                if (rx != null) { try { _stream.Close(); } catch { } rx.Join(500); }
                Console.WriteLine();
                Console.WriteLine("終了: 送信 {0} / 受信 {1} / 適用 {2}", _sendCount, _recvCount, _applyCount);
            }

            private void ReceiveLoop()
            {
                int pkt = 12 + _recvVars.Length * 4;
                var buf = new byte[pkt];
                while (!_stop)
                {
                    int off = 0;
                    while (off < pkt)
                    {
                        int r;
                        try { r = _stream.Read(buf, off, pkt - off); }
                        catch { return; }
                        if (r <= 0) return;
                        off += r;
                    }
                    if (BitConverter.ToUInt32(buf, 0) != Magic)
                    {
                        Console.WriteLine("パケットの同期がずれました。相手と変数設定が一致しているか確認してください。");
                        return;
                    }
                    int count = BitConverter.ToInt32(buf, 8);
                    if (count != _recvVars.Length)
                    {
                        Console.WriteLine("変数の数が一致しません (相手 {0} / 自分 {1})。設定を合わせてください。", count, _recvVars.Length);
                        return;
                    }
                    var v = new int[count];
                    for (int i = 0; i < count; i++) v[i] = BitConverter.ToInt32(buf, 12 + i * 4);
                    _latestTick = BitConverter.ToInt32(buf, 4);
                    _latest = v;
                    _recvCount++;
                }
            }
        }
    }
}
