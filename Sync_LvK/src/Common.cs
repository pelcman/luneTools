// LvKSync 共通コード
//   - RPG_RT のプロセスメモリ読み書き
//   - 変数配列ベースの自動特定
//   - ネットワークプロトコル
//   - 設定ファイル
//
// C# 5 / .NET Framework 4.x。外部依存なし。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LvKSync
{
    #region Win32

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
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);

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

    #endregion

    #region プロセスメモリ

    public sealed class GameMemory : IDisposable
    {
        private IntPtr _h;
        private readonly byte[] _s4 = new byte[4];

        public int Pid { get; private set; }
        public long VarBase { get; set; }

        public GameMemory(int pid)
        {
            Pid = pid;
            _h = Native.OpenProcess(
                Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE |
                Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION, false, pid);
            if (_h == IntPtr.Zero)
                throw new InvalidOperationException("OpenProcess に失敗しました (pid=" + pid +
                    ", err=" + Marshal.GetLastWin32Error() + ")。管理者権限が必要な場合があります。");
        }

        public int ReadVar(int index)
        {
            int got;
            if (!Native.ReadProcessMemory(_h, (IntPtr)(VarBase + (long)index * 4), _s4, 4, out got)) return 0;
            return BitConverter.ToInt32(_s4, 0);
        }

        public void WriteVar(int index, int value)
        {
            int wrote;
            Native.WriteProcessMemory(_h, (IntPtr)(VarBase + (long)index * 4),
                BitConverter.GetBytes(value), 4, out wrote);
        }

        public void ReadVars(int[] idx, int[] dst)
        {
            for (int i = 0; i < idx.Length; i++) dst[i] = ReadVar(idx[i]);
        }

        public void WriteVars(int[] idx, int[] src)
        {
            for (int i = 0; i < idx.Length; i++) WriteVar(idx[i], src[i]);
        }

        /// <summary>そのアドレスが書き込み可能なコミット済み領域か、何バイト連続しているかを返す。</summary>
        public bool QueryRegion(long addr, out long regionEnd)
        {
            regionEnd = 0;
            Native.MEMORY_BASIC_INFORMATION mbi;
            if (Native.VirtualQueryEx(_h, (IntPtr)addr, out mbi,
                    Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION))) == 0) return false;
            uint pr = mbi.Protect & 0xFF;
            bool writable = (pr == 0x04 || pr == 0x40 || pr == 0x08 || pr == 0x80);
            if (mbi.State != Native.MEM_COMMIT || !writable || (mbi.Protect & Native.PAGE_GUARD) != 0) return false;
            regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            return true;
        }

        public long ModuleBase(string name, out int size)
        {
            size = 0;
            try
            {
                var p = Process.GetProcessById(Pid);
                foreach (ProcessModule m in p.Modules)
                    if (m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    { size = m.ModuleMemorySize; return (long)m.BaseAddress; }
            }
            catch { }
            return 0;
        }

        /// <summary>続いた範囲の変数をまとめて読む。1個ずつ読むより桁違いに速い。</summary>
        public bool ReadSpan(int firstIndex, int count, int[] dst)
        {
            var buf = new byte[count * 4];
            if (!ReadRaw(VarBase + (long)firstIndex * 4, buf, buf.Length)) return false;
            for (int i = 0; i < count; i++) dst[i] = BitConverter.ToInt32(buf, i * 4);
            return true;
        }

        public bool ReadRaw(long addr, byte[] buf, int size)
        {
            int got;
            return Native.ReadProcessMemory(_h, (IntPtr)addr, buf, size, out got) && got == size;
        }

        public bool Alive
        {
            get
            {
                try { var p = Process.GetProcessById(Pid); return !p.HasExited; }
                catch { return false; }
            }
        }

        internal sealed class Region { public long Base; public int Size; public byte[] Data; }

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
                if (mbi.State == Native.MEM_COMMIT && writable && !guarded
                    && rs >= 0x1000 && rs <= 512L * 1024 * 1024 && total < 3000L * 1024 * 1024)
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

    public static class VarBaseFinder
    {
        public const int TickVar = 654;

        /// <summary>
        /// RPG_RT.exe 内の、変数配列を指すポインタの位置。
        /// Maniacs v250823 / 64bit で実測した値。ランタイムが変わるとずれるので、
        /// 検証に失敗したらシグネチャ走査に切り替える。
        /// </summary>
        public const long ModulePtrOffset = 0x275528;

        /// <summary>オフセットを実測したランタイムの大きさ。違う版ならポインタ方式は使わない。</summary>
        public const int ExpectedModuleSize = 0x292000;

        /// <summary>
        /// モジュール内のポインタから変数配列を得る。
        /// シグネチャ走査と違い、対戦中でなくても（キャラクター選択画面でも）成立する。
        /// needIndex は触る予定の最大の変数番号。領域がそこまで届くかを検証する。
        /// </summary>
        public static long FromModulePointer(GameMemory mem, int needIndex)
        {
            int modSize;
            long modBase = mem.ModuleBase("RPG_RT.exe", out modSize);
            if (modBase == 0) return 0;
            if (modSize != ExpectedModuleSize) return 0;   // 別バージョンのランタイム
            if (ModulePtrOffset + 8 > modSize) return 0;

            var buf = new byte[8];
            if (!mem.ReadRaw(modBase + ModulePtrOffset, buf, 8)) return 0;
            long vb = BitConverter.ToInt64(buf, 0);

            // ユーザーモードのヒープらしいアドレスか
            if (vb < 0x10000 || vb > 0x7FFFFFFFFFFF) return 0;
            if ((vb & 3) != 0) return 0;

            // 触る範囲が書き込み可能な領域に収まっているか
            long need = vb + ((long)needIndex + 1) * 4;
            long end;
            if (!mem.QueryRegion(vb, out end)) return 0;
            if (end < need) return 0;

            var b4 = new byte[4];
            if (!mem.ReadRaw(vb, b4, 4)) return 0;
            return vb;
        }
        private const int Sentinel = -999999999;
        private static readonly int[] SentinelIdx = { 20001, 20002, 20201, 20202, 20401, 20402, 20601, 20602 };
        private static readonly int[] AntiIdx = { 20000, 20003, 20200, 20203 };

        /// <summary>
        /// ポインタ方式を先に試し、駄目ならシグネチャ走査に落とす。
        /// ポインタ方式は対戦前の画面でも成立する。
        /// </summary>
        public static long Find(GameMemory mem, int needIndex, out string method)
        {
            long p = FromModulePointer(mem, needIndex);
            if (p != 0) { method = "module-pointer"; return p; }
            return FindBySignature(mem, out method);
        }

        /// <summary>
        /// ポインタを読み直して、変数配列が作り直されていたら追従する。
        /// 対戦の開始・終了で配列は再確保されるため、掴みっぱなしにはできない。
        /// </summary>
        public static bool Refresh(GameMemory mem, int needIndex)
        {
            long p = FromModulePointer(mem, needIndex);
            if (p == 0 || p == mem.VarBase) return false;
            mem.VarBase = p;
            return true;
        }

        public static long FindBySignature(GameMemory mem, out string method)
        {
            method = null;
            var regs = mem.Snapshot(true);
            var buf = new byte[4];

            foreach (var r in regs)
            {
                for (int off = 0; off + 4 <= r.Size; off += 4)
                {
                    if (BitConverter.ToInt32(r.Data, off) != Sentinel) continue;
                    long b0 = r.Base + off - 20001L * 4;
                    bool ok = true;
                    foreach (int k in SentinelIdx)
                        if (!mem.ReadRaw(b0 + (long)k * 4, buf, 4) || BitConverter.ToInt32(buf, 0) != Sentinel) { ok = false; break; }
                    if (ok)
                        foreach (int k in AntiIdx)
                            if (mem.ReadRaw(b0 + (long)k * 4, buf, 4) && BitConverter.ToInt32(buf, 0) == Sentinel) { ok = false; break; }
                    if (ok && TickAdvancing(mem, b0)) { method = "sentinel"; return b0; }
                }
            }

            foreach (var r in regs)
            {
                for (int off = 0; off + 80 <= r.Size; off += 4)
                {
                    if (BitConverter.ToInt32(r.Data, off) != 10201) continue;
                    bool run = true;
                    for (int k = 1; k < 20; k++)
                        if (BitConverter.ToInt32(r.Data, off + k * 4) != 10201 + k) { run = false; break; }
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

    #region プロトコル

    /// <summary>
    /// メッセージ形式: [magic 4][type 1][len 1][payload len]
    /// すべてリトルエンディアン。
    /// </summary>
    public static class Proto
    {
        public const uint Magic = 0x324B564C;   // "LVK2"
        public const int MaxPlayers = 4;
        public const int ButtonsPerPlayer = 6;

        public const byte MsgHello = 1;    // C->S  [slotRequest 1]  0=おまかせ
        public const byte MsgWelcome = 2;  // S->C  [slot 1][maxPlayers 1]
        public const byte MsgInput = 3;    // C->S  [slot 1][frame 4][mask 2]
        public const byte MsgFrame = 4;    // S->C  [frame 4][mask 2 x4][connected 1]
        public const byte MsgBye = 5;      // 双方向
        public const byte MsgFull = 6;     // S->C  空きスロットなし
        public const byte MsgPing = 7;     // C->S  [stamp 8]  往復時間の測定
        public const byte MsgPong = 8;     // S->C  [stamp 8]  そのまま返す
        public const byte MsgRoster = 9;   // S->C  誰が何Pに座っているか
        public const byte MsgCheck = 10;   // C->S  [スロット 1][フレーム 4][チェックサム 4]

        public const int MaxNameBytes = 24;

        public static byte[] Build(byte type, byte[] payload)
        {
            int n = payload == null ? 0 : payload.Length;
            var b = new byte[6 + n];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, b, 0, 4);
            b[4] = type;
            b[5] = (byte)n;
            if (n > 0) Buffer.BlockCopy(payload, 0, b, 6, n);
            return b;
        }

        /// <summary>1メッセージを読み切る。切断時は null。</summary>
        public static bool Read(NetworkStream s, out byte type, out byte[] payload)
        {
            type = 0; payload = null;
            var head = new byte[6];
            if (!ReadFull(s, head, 6)) return false;
            if (BitConverter.ToUInt32(head, 0) != Magic) return false;
            type = head[4];
            int n = head[5];
            payload = new byte[n];
            if (n > 0 && !ReadFull(s, payload, n)) return false;
            return true;
        }

        private static bool ReadFull(NetworkStream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r;
                try { r = s.Read(buf, off, n - off); }
                catch { return false; }
                if (r <= 0) return false;
                off += r;
            }
            return true;
        }

        public static byte[] InputPayload(int slot, int frame, ushort mask)
        {
            var p = new byte[7];
            p[0] = (byte)slot;
            Buffer.BlockCopy(BitConverter.GetBytes(frame), 0, p, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(mask), 0, p, 5, 2);
            return p;
        }

        /// <summary>ずれ検知用。ブロックごとの値を並べる。どこから分かれたか分かる。</summary>
        public static byte[] CheckPayload(int slot, int frame, uint[] hashes)
        {
            var p = new byte[6 + hashes.Length * 4];
            p[0] = (byte)slot;
            Buffer.BlockCopy(BitConverter.GetBytes(frame), 0, p, 1, 4);
            p[5] = (byte)hashes.Length;
            for (int i = 0; i < hashes.Length; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(hashes[i]), 0, p, 6 + i * 4, 4);
            return p;
        }

        public static byte[] StampPayload(long stamp)
        {
            return BitConverter.GetBytes(stamp);
        }

        /// <summary>参加要求。希望スロットと表示名を送る。</summary>
        public static byte[] HelloPayload(int slot, string name)
        {
            var nb = TrimName(name);
            var p = new byte[2 + nb.Length];
            p[0] = (byte)slot;
            p[1] = (byte)nb.Length;
            Buffer.BlockCopy(nb, 0, p, 2, nb.Length);
            return p;
        }

        public static void ParseHello(byte[] p, out int slot, out string name)
        {
            slot = p.Length > 0 ? p[0] : 0;
            name = "";
            if (p.Length > 1)
            {
                int n = p[1];
                if (n > 0 && p.Length >= 2 + n)
                    name = Encoding.UTF8.GetString(p, 2, n);
            }
        }

        /// <summary>座席表。空きスロットは名前なしで含めない。</summary>
        public static byte[] RosterPayload(string[] namesBySlot)
        {
            var parts = new System.Collections.Generic.List<byte[]>();
            int count = 0;
            for (int i = 1; i <= MaxPlayers; i++)
            {
                if (namesBySlot[i] == null) continue;
                var nb = TrimName(namesBySlot[i]);
                var e = new byte[2 + nb.Length];
                e[0] = (byte)i;
                e[1] = (byte)nb.Length;
                Buffer.BlockCopy(nb, 0, e, 2, nb.Length);
                parts.Add(e);
                count++;
            }
            int total = 1;
            foreach (var e in parts) total += e.Length;
            var p = new byte[total];
            p[0] = (byte)count;
            int off = 1;
            foreach (var e in parts) { Buffer.BlockCopy(e, 0, p, off, e.Length); off += e.Length; }
            return p;
        }

        public static string[] ParseRoster(byte[] p)
        {
            var names = new string[MaxPlayers + 1];
            if (p.Length < 1) return names;
            int count = p[0], off = 1;
            for (int i = 0; i < count && off + 2 <= p.Length; i++)
            {
                int slot = p[off], n = p[off + 1];
                off += 2;
                if (off + n > p.Length) break;
                if (slot >= 1 && slot <= MaxPlayers)
                    names[slot] = Encoding.UTF8.GetString(p, off, n);
                off += n;
            }
            return names;
        }

        private static byte[] TrimName(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "";
            var b = Encoding.UTF8.GetBytes(name);
            if (b.Length <= MaxNameBytes) return b;
            // UTF8 の途中で切らないように縮める
            int len = MaxNameBytes;
            while (len > 0 && (b[len] & 0xC0) == 0x80) len--;
            var t = new byte[len];
            Buffer.BlockCopy(b, 0, t, 0, len);
            return t;
        }

        /// <summary>
        /// 配信フレーム。末尾に「ゲームフレーム0 に対応するサーバーフレーム」を付ける。
        /// 古い形 (13バイト) も読めるように、後ろに足すだけにしてある。
        /// </summary>
        public static byte[] FramePayloadWithBase(int frame, ushort[] masks, byte connected, int matchBase)
        {
            var head = FramePayload(frame, masks, connected);
            var p = new byte[head.Length + 4];
            Buffer.BlockCopy(head, 0, p, 0, head.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(matchBase), 0, p, head.Length, 4);
            return p;
        }

        public static byte[] FramePayload(int frame, ushort[] masks, byte connected)
        {
            var p = new byte[4 + MaxPlayers * 2 + 1];
            Buffer.BlockCopy(BitConverter.GetBytes(frame), 0, p, 0, 4);
            for (int i = 0; i < MaxPlayers; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(masks[i]), 0, p, 4 + i * 2, 2);
            p[4 + MaxPlayers * 2] = connected;
            return p;
        }
    }

    #endregion

    #region 設定

    /// <summary>
    /// 動いている間のできごとをファイルに残す。
    /// 対戦中に何が起きたかを後から追えるようにするためのもの。
    /// 開いたままでも他のソフトから読めるように共有指定で開く。
    /// </summary>
    public sealed class FileLogger : IDisposable
    {
        private readonly object _gate = new object();
        private StreamWriter _w;

        public string Path { get; private set; }
        public string Dir { get; private set; }

        /// <summary>exe と同じ場所の logs\ に、開始時刻の名前で作る。</summary>
        public FileLogger(string prefix)
        {
            try
            {
                string baseDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                Dir = System.IO.Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(Dir);
                Path = System.IO.Path.Combine(Dir,
                    prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                var fs = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _w = new StreamWriter(fs, new UTF8Encoding(true));
                _w.AutoFlush = true;
                _w.WriteLine("# " + prefix + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { _w = null; }
        }

        public void Write(string line)
        {
            var w = _w;
            if (w == null) return;
            lock (_gate)
            {
                try { w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line); }
                catch { }
            }
        }

        public void Dispose()
        {
            var w = _w;
            _w = null;
            if (w == null) return;
            lock (_gate)
            {
                try { w.WriteLine("# 終了  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); w.Dispose(); }
                catch { }
            }
        }
    }

    public sealed class Ini
    {
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Ini Load(string path)
        {
            var ini = new Ini();
            if (!File.Exists(path)) return ini;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                ini._map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return ini;
        }

        public string Get(string key, string def)
        {
            string v;
            return _map.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        public int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public bool GetBool(string key, bool def)
        {
            string v = Get(key, null);
            if (v == null) return def;
            v = v.ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }
    }

    #endregion

    #region ユーティリティ

    public static class Util
    {
        public static int[] ParseVars(string spec)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(spec)) return list.ToArray();
            foreach (var raw in spec.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                int dash = part.IndexOf('-', 1);
                if (dash > 0)
                {
                    int lo, hi;
                    if (int.TryParse(part.Substring(0, dash), out lo) && int.TryParse(part.Substring(dash + 1), out hi))
                    {
                        if (hi < lo) { int t = lo; lo = hi; hi = t; }
                        for (int v = lo; v <= hi; v++) list.Add(v);
                    }
                }
                else
                {
                    int v;
                    if (int.TryParse(part, out v)) list.Add(v);
                }
            }
            return list.ToArray();
        }

        public static List<Process> FindGames()
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

        /// <summary>"W,S,A,D,F,G" や "0x57,0x53,..." を仮想キーコード配列にする。</summary>
        public static int[] ParseKeys(string spec)
        {
            var list = new List<int>();
            foreach (var raw in spec.Split(','))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) list.Add(v);
                }
                else if (t.Length == 1)
                {
                    list.Add(char.ToUpperInvariant(t[0]));
                }
                else
                {
                    switch (t.ToLowerInvariant())
                    {
                        case "up": list.Add(0x26); break;
                        case "down": list.Add(0x28); break;
                        case "left": list.Add(0x25); break;
                        case "right": list.Add(0x27); break;
                        case "space": list.Add(0x20); break;
                        case "enter": list.Add(0x0D); break;
                        case "shift": list.Add(0x10); break;
                        case "ctrl": list.Add(0x11); break;
                        default:
                            int v;
                            if (int.TryParse(t, out v)) list.Add(v);
                            break;
                    }
                }
            }
            return list.ToArray();
        }

        public static bool KeyDown(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }
    }

    #endregion
}
