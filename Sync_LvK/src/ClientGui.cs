// LvKSyncClientGui - プレイヤーが使う画面つきクライアント
//
// 名前を入れてサーバーへ参加すると、自分が何P になったかが表示される。
// 自分の入力をサーバーへ送り、他プレイヤーの入力をゲームのメモリへ書き込む。
//
// ゲーム側との取り決め:
//   V[netbase + (slot-1)*6 + 0..5] = 左, 上, 下, 右, A, B   (各 0 か 1)

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace LvKSync
{
    /// <summary>接続からメモリ書き込みまでを受け持つ。GUI からは設定を入れて Start するだけ。</summary>
    internal sealed class ClientEngine
    {
        private const int Buttons = Proto.ButtonsPerPlayer;
        private const int RingSize = 256;

        /// <summary>試合のフレームカウンタとして妥当な上限。これを超えたら別の配列を見ている。</summary>
        private const int MaxSaneTick = 1000000;

        /// <summary>他より何フレーム進んだら待つか。</summary>
        public int AheadLimit = 3;

        /// <summary>送り先の先読み量の上限。これ以上は伸ばさない。</summary>
        private const int MaxSendDelay = 30;

        /// <summary>
        /// 何フレーム先ぶんを前もって書いておくか。
        /// ゲームはフレームの頭で入力を読むので、tick が変わってから書くと
        /// 読み終えた後になることがある。先に置いておけば競争にならない。
        /// </summary>
        public int WriteAhead = 1;

        // --- ずれ検知 ---
        // 見る範囲。入力まわりの低い番号も含めて、非決定性がどこから入るかを見る。
        private static readonly int[][] CheckRanges =
        {
            // 4窓デモで実測した「同期しないと壊れる」範囲。
            // 低い番号のテンポラリや、プレイヤー間で使い回す入力領域は入れない。
            new[] { 10001, 85 }, new[] { 10101, 38 }, new[] { 10173, 154 },
            new[] { 10340, 11 }, new[] { 10373, 87 }, new[] { 10492, 13 },
            new[] { 10523, 27 }, new[] { 10570, 90 }, new[] { 10680, 24 },
            new[] { 10770, 30 }, new[] { 22601, 64 }, new[] { 22690, 12 },
        };
        /// <summary>何フレームごとに突き合わせるか。0 で無効。</summary>
        public int CheckEvery = 30;
        /// <summary>1ブロックあたりの変数の数。</summary>
        private const int CheckBlock = 100;
        private int[][] _chkBuf;
        private int[] _chkFirsts, _chkCounts;
        private uint[] _chkHashes;

        /// <summary>
        /// 進みすぎたときにゲームのプロセスを一瞬止めるか。
        /// 止めると確実に揃うが、ゲーム側の時間が乱れるので既定では使わない。
        /// ずれは入力遅延 (--delay) で吸収するのが基本。
        /// </summary>
        public bool ThrottleAhead;

        // 進みすぎたときにゲームを一瞬止めるために使う
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int a, bool b, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        // 既定では Sleep(1) が約15ms眠ってしまい、ゲームのフレームを見逃す
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
        private IntPtr _susp = IntPtr.Zero;

        // --- 設定 ---
        public string Host = "127.0.0.1";
        public int Port = 47801;
        public string Name = "";
        public int WantSlot;
        public int NetBase = 9001;
        public int GameIndex;
        public int DelayFrames = 2;
        public bool ApplyOwn;
        public int[] Keys = new int[Buttons];

        /// <summary>ゲームのフレーム番号で並べた入力。_ringFrame はその枠が何フレーム用か。</summary>
        private readonly ushort[][] _ring = new ushort[RingSize][];
        private readonly int[][] _ringFrame = new int[RingSize][];
        private volatile int _serverFrame = -1;

        // --- 状態 (GUI が読む) ---
        public volatile int MySlot;
        public volatile int AppliedFrame;      // いまゲームに書いているフレーム
        private volatile int _matchBase = int.MinValue;   // 未使用 (旧方式の名残)
        private volatile int _minGameFrame;                // 全員のうち一番遅れているフレーム
        private volatile int _maxGameFrame;                // 全員のうち一番進んでいるフレーム
        private volatile int _sendDelay;                   // 実際に使っている送り先の先読み量
        public volatile int AheadBy;                       // 自分が何フレーム進んでいるか
        public long WaitCount;                             // 進みすぎて待った回数
        public long MissedTicks;                           // 見逃したゲームフレーム数
        private volatile int _checkWanted;                 // 突き合わせたいフレーム
        private volatile int _gameFrame;       // サーバーへ知らせる自分のゲームframe
        public volatile int FrameLag;          // サーバーより何フレーム後ろか
        public volatile bool InMatch;
        public long StallCount;                // 入力が間に合わなかった回数
        public volatile int MaxPlayers = Proto.MaxPlayers;
        public volatile int RttMs = -1;
        public volatile int ConnectedBits;
        public volatile ushort LocalMask;
        public volatile string[] Roster = new string[Proto.MaxPlayers + 1];
        public volatile ushort[] RemoteMasks = new ushort[Proto.MaxPlayers];
        public long TxCount, RxCount;
        public long VarBase;
        public int Pid;
        public volatile string Phase = "停止中";

        public event Action<string> Log;
        public event Action Changed;

        private volatile bool _stop = true;
        private Thread _worker;
        private GameMemory _mem;
        private TcpClient _tcp;
        private NetworkStream _st;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public bool Running { get { return !_stop; } }

        private void Say(string s) { var h = Log; if (h != null) h(s); }
        private void Bump() { var h = Changed; if (h != null) h(); }

        public void Start()
        {
            _stop = false;
            MySlot = 0; RttMs = -1; TxCount = 0; RxCount = 0; VarBase = 0; Pid = 0;
            _serverFrame = -1; AppliedFrame = 0; FrameLag = 0; InMatch = false; StallCount = 0;
            _matchBase = int.MinValue; _gameFrame = 0;
            _minGameFrame = 0; _maxGameFrame = 0; _sendDelay = 0;
            for (int i = 0; i < RingSize; i++)
            {
                _ring[i] = new ushort[Proto.MaxPlayers];
                _ringFrame[i] = new int[Proto.MaxPlayers];
                for (int k = 0; k < Proto.MaxPlayers; k++) _ringFrame[i][k] = -1;
            }
            Roster = new string[Proto.MaxPlayers + 1];
            RemoteMasks = new ushort[Proto.MaxPlayers];
            _worker = new Thread(Work) { IsBackground = true };
            // ゲームのフレームを1つでも見逃すと同期が崩れるので、優先して回す
            try { _worker.Priority = ThreadPriority.Highest; } catch { }
            _worker.Start();
        }

        public void Stop()
        {
            if (_stop) return;
            _stop = true;
            try { if (_st != null) { var bye = Proto.Build(Proto.MsgBye, null); lock (_st) _st.Write(bye, 0, bye.Length); } }
            catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            Phase = "停止中";
            Bump();
        }

        /// <summary>確認用。ゲームのメモリ上の入力ブロックをそのまま読む。</summary>
        public int[] ReadNetBlock()
        {
            var mem = _mem;
            if (mem == null || VarBase == 0) return null;
            var v = new int[Proto.MaxPlayers * Buttons];
            // ReadVar は入力スレッドと同じバッファを使うので、ここでは使わない。
            // 同時に呼ぶと読み取りが混ざる。
            try { if (!mem.ReadSpan(NetBase, v.Length, v)) return null; }
            catch { return null; }
            return v;
        }

        /// <summary>RTT から入力遅延フレーム数の目安を出す。片道ぶんを 60fps に換算し少し余裕をみる。</summary>
        public static int SuggestDelay(int rttMs)
        {
            int f = (int)Math.Ceiling(rttMs / 2.0 / (1000.0 / 60.0)) + 1;
            if (f < 1) f = 1;
            if (f > 15) f = 15;
            return f;
        }

        private void Work()
        {
            try { timeBeginPeriod(1); } catch { }
            try { WorkCore(); }
            catch (Exception ex) { Say("エラー: " + ex.Message); }
            finally
            {
                _stop = true;
                Phase = "停止中";
                try { if (_tcp != null) _tcp.Close(); } catch { }
                _tcp = null; _st = null;
                try { timeEndPeriod(1); } catch { }
                if (_susp != IntPtr.Zero)
                {
                    try { NtResumeProcess(_susp); } catch { }
                    try { CloseHandle(_susp); } catch { }
                    _susp = IntPtr.Zero;
                }
                var m = _mem; _mem = null;
                if (m != null) m.Dispose();
                Bump();
            }
        }

        private void WorkCore()
        {
            // --- ゲームを待つ ---
            Phase = "ゲームの起動を待っています";
            Say("ゲーム (RPG_RT.exe) の起動を待っています…");
            Bump();
            int pid = 0;
            while (!_stop)
            {
                var games = Util.FindGames();
                if (games.Count > GameIndex) { pid = games[GameIndex].Id; break; }
                Thread.Sleep(600);
            }
            if (_stop) return;
            Pid = pid;
            Say(string.Format("ゲームを見つけました  pid={0}", pid));

            try { _mem = new GameMemory(pid); }
            catch (Exception ex) { Say(ex.Message); return; }
            // 0x0800 = PROCESS_SUSPEND_RESUME  (進みすぎたときに一瞬止めるため)
            try { _susp = OpenProcess(0x0800, false, pid); } catch { }

            // --- 変数配列を探す ---
            Phase = "ゲームのデータを探しています";
            Bump();
            int needIndex = NetBase + Proto.MaxPlayers * Buttons - 1;
            if (needIndex < VarBaseFinder.TickVar) needIndex = VarBaseFinder.TickVar;
            long vb = 0; string method = null;
            for (int tries = 0; vb == 0 && !_stop; tries++)
            {
                vb = VarBaseFinder.Find(_mem, needIndex, out method);
                if (vb == 0)
                {
                    if (tries == 5) Say("見つかりません。ゲームをタイトル画面まで進めてみてください。");
                    Thread.Sleep(600);
                }
            }
            if (_stop) return;
            _mem.VarBase = vb;
            VarBase = vb;
            // パッチが「試合の頭で入力を無効にする」ために読むゼロの並び。
            // ここが 0 でないと最初の数フレームに変な入力が入る。
            for (int z = 0; z < Buttons; z++) _mem.WriteVar(NetBase + 30 + z, 0);
            Say(string.Format("ゲームのデータを見つけました  0x{0:X}  ({1})", vb, method));

            // --- 接続 ---
            Phase = "サーバーへ接続しています";
            Say(string.Format("サーバーへ接続しています  {0}:{1}", Host, Port));
            Bump();
            for (int i = 0; i < 120 && _tcp == null && !_stop; i++)
            {
                try { var c = new TcpClient(); c.Connect(Host, Port); _tcp = c; }
                catch { Thread.Sleep(500); }
            }
            if (_stop) return;
            if (_tcp == null) { Say("接続できませんでした。IP とポート、ポート開放を確認してください。"); return; }
            _tcp.NoDelay = true;
            _st = _tcp.GetStream();

            var hello = Proto.Build(Proto.MsgHello,
                Proto.HelloPayload(Math.Max(0, Math.Min(Proto.MaxPlayers, WantSlot)), Name));
            _st.Write(hello, 0, hello.Length);

            byte type; byte[] payload;
            if (!Proto.Read(_st, out type, out payload)) { Say("サーバーからの応答がありません。"); return; }
            if (type == Proto.MsgFull) { Say("サーバーが満員です。"); return; }
            if (type != Proto.MsgWelcome || payload.Length < 2) { Say("想定外の応答です。"); return; }

            MySlot = payload[0];
            MaxPlayers = payload[1];
            Phase = "対戦中";
            Say(string.Format("参加しました。あなたは {0}P です  (最大 {1} 人)", MySlot, MaxPlayers));
            Bump();

            new Thread(ReceiveLoop) { IsBackground = true }.Start();
            new Thread(CheckLoop) { IsBackground = true }.Start();
            new Thread(PingLoop) { IsBackground = true }.Start();

            MainLoop(needIndex);
        }

        /// <summary>
        /// 突き合わせ用の値を計算して送る。
        /// 入力を扱うループを遅らせないよう、別スレッドでゆっくり回す。
        /// </summary>
        private void CheckLoop()
        {
            var st = _st;
            int done = 0;
            while (!_stop)
            {
                int want = _checkWanted;
                // 1ms ごとに見る。回し続けると入力を扱うループを圧迫して
                // フレームを見逃すようになる (実測で見逃しが 0 -> 3000 に増えた)。
                if (want == done || want <= 0) { Thread.Sleep(1); continue; }
                done = want;
                var mem = _mem;
                if (mem == null) { Thread.Sleep(5); continue; }
                try
                {
                    if (!StateHash(mem, want)) continue;
                    var cp = Proto.Build(Proto.MsgCheck,
                        Proto.CheckPayload(MySlot, want, _chkFirsts, _chkCounts, _chkHashes));
                    lock (st) st.Write(cp, 0, cp.Length);
                }
                catch { }
            }
        }

        private void ReceiveLoop()
        {
            var st = _st;
            while (!_stop)
            {
                byte type; byte[] p;
                if (!Proto.Read(st, out type, out p)) { _stop = true; Bump(); return; }
                if (type == Proto.MsgFrame && p.Length >= 4 + Proto.MaxPlayers * 2 + 1)
                {
                    int frame = BitConverter.ToInt32(p, 0);
                    var m = new ushort[Proto.MaxPlayers];
                    for (int i = 0; i < Proto.MaxPlayers; i++) m[i] = BitConverter.ToUInt16(p, 4 + i * 2);
                    RemoteMasks = m;
                    ConnectedBits = p[4 + Proto.MaxPlayers * 2];
                    RxCount++;

                    _serverFrame = frame;
                    // 末尾に「全員のうち一番遅れているフレーム」が付いている
                    if (p.Length >= 4 + Proto.MaxPlayers * 2 + 1 + 4)
                        _minGameFrame = BitConverter.ToInt32(p, 4 + Proto.MaxPlayers * 2 + 1);
                    if (p.Length >= 4 + Proto.MaxPlayers * 2 + 1 + 8)
                        _maxGameFrame = BitConverter.ToInt32(p, 4 + Proto.MaxPlayers * 2 + 5);
                }
                else if (type == Proto.MsgInput && p.Length >= 7)
                {
                    // フレーム番号付きの入力。そのフレーム用の枠に入れておく。
                    int slot = p[0];
                    int frame = BitConverter.ToInt32(p, 1);
                    ushort mask = BitConverter.ToUInt16(p, 5);
                    if (slot >= 1 && slot <= Proto.MaxPlayers && frame > 0)
                    {
                        int m = Mod(frame);
                        _ring[m][slot - 1] = mask;
                        _ringFrame[m][slot - 1] = frame;
                    }
                }
                else if (type == Proto.MsgRoster)
                {
                    Roster = Proto.ParseRoster(p);
                    Bump();
                }
                else if (type == Proto.MsgPong && p.Length >= 8)
                {
                    long sent = BitConverter.ToInt64(p, 0);
                    long rtt = _clock.ElapsedMilliseconds - sent;
                    if (rtt >= 0 && rtt < 10000)
                        RttMs = (RttMs < 0) ? (int)rtt : (int)((RttMs * 3 + rtt) / 4);
                }
                else if (type == Proto.MsgBye) { _stop = true; Bump(); return; }
            }
        }

        private void PingLoop()
        {
            var st = _st;
            while (!_stop)
            {
                try
                {
                    var pkt = Proto.Build(Proto.MsgPing, Proto.StampPayload(_clock.ElapsedMilliseconds));
                    lock (st) st.Write(pkt, 0, pkt.Length);
                }
                catch { return; }
                Thread.Sleep(500);
            }
        }

        private void EnsureCheckBuffers()
        {
            if (_chkBuf != null) return;
            _chkBuf = new int[CheckRanges.Length][];
            int nb = 0;
            for (int r = 0; r < CheckRanges.Length; r++)
            {
                _chkBuf[r] = new int[CheckRanges[r][1]];
                nb += (CheckRanges[r][1] + CheckBlock - 1) / CheckBlock;
            }
            _chkHashes = new uint[nb];
            _chkFirsts = new int[nb];
            _chkCounts = new int[nb];
        }

        /// <summary>見る範囲をブロックごとにまとめる (FNV-1a)。</summary>
        private readonly int[] _tick1 = new int[1];

        /// <summary>
        /// 見る範囲をブロックごとにまとめる。
        /// 読んでいる途中でゲームが次のフレームへ進むと、前半と後半で
        /// 別のフレームの値が混ざる。そうなった回は捨てる (false を返す)。
        /// </summary>
        private bool StateHash(GameMemory mem, int wantTick)
        {
            if (!mem.ReadSpan(VarBaseFinder.TickVar, 1, _tick1)) return false;
            if (_tick1[0] != wantTick) return false;
            int b = 0;
            for (int r = 0; r < CheckRanges.Length; r++)
            {
                int first = CheckRanges[r][0], count = CheckRanges[r][1];
                if (!mem.ReadSpan(first, count, _chkBuf[r])) return false;
                b = HashBlocks(_chkBuf[r], first, b);
            }
            if (!mem.ReadSpan(VarBaseFinder.TickVar, 1, _tick1)) return false;
            return _tick1[0] == wantTick;
        }

        private int HashBlocks(int[] src, int firstVar, int outIndex)
        {
            unchecked
            {
                for (int off = 0; off < src.Length; off += CheckBlock)
                {
                    uint h = 2166136261;
                    int end = Math.Min(off + CheckBlock, src.Length);
                    for (int i = off; i < end; i++)
                    {
                        uint v = (uint)src[i];
                        h = (h ^ (v & 0xFF)) * 16777619;
                        h = (h ^ ((v >> 8) & 0xFF)) * 16777619;
                        h = (h ^ ((v >> 16) & 0xFF)) * 16777619;
                        h = (h ^ (v >> 24)) * 16777619;
                    }
                    _chkFirsts[outIndex] = firstVar + off;
                    _chkCounts[outIndex] = end - off;
                    _chkHashes[outIndex] = h;
                    outIndex++;
                }
            }
            return outIndex;
        }

        private static int Mod(int frame)
        {
            return ((frame % RingSize) + RingSize) % RingSize;
        }

        private void MainLoop(int trackIndex)
        {
            var st = _st;
            var mem = _mem;
            int refreshCounter = 0;
            int aliveCounter = 0;
            long localFrame = 0;
            var pacer = Stopwatch.StartNew();
            double frameMs = 1000.0 / 60.0;
            double nextDue = frameMs;

            int lastTick = -1;          // ゲームのフレームカウンタ V[654]
            EnsureCheckBuffers();
            int checkNextTick = 0;
            int statNextTick = 0;
            long statStall = 0;
            var lastWritten = new ushort[Proto.MaxPlayers];
            bool haveWritten = false;

            while (!_stop)
            {
                // 生存確認も毎回やると重い。たまにでよい。
                if (++aliveCounter >= 500)
                {
                    aliveCounter = 0;
                    if (!mem.Alive) { Say("ゲームが終了しました。"); break; }
                }

                // 対戦の開始・終了で変数配列は作り直される。追従する。
                // 遅れると古い配列を読んでしまうので、こまめに見る。
                if (++refreshCounter >= 300)
                {
                    refreshCounter = 0;
                    if (VarBaseFinder.Refresh(mem, trackIndex))
                    {
                        VarBase = mem.VarBase;
                        lastTick = -1;
                        Say(string.Format("ゲームのデータが作り直されました。追従します  0x{0:X}", mem.VarBase));
                    }
                }

                // --- 適用 ---
                // 試合中は V[654] が毎フレーム進む。これを合図にして、
                // サーバーのフレーム番号で並べた入力を 1フレームぶんずつ消費する。
                // こうすると「押している長さ」がどのインスタンスでも同じ数のフレームになる。
                if (_serverFrame < 0) { Thread.Sleep(1); continue; }

                int tick = mem.ReadVar(VarBaseFinder.TickVar);

                // 作り直し前の配列を読むと、ありえない値になる。
                // その場合はすぐ配列を取り直し、この回は何もしない。
                if (tick < 0 || tick > MaxSaneTick)
                {
                    if (VarBaseFinder.Refresh(mem, trackIndex))
                    {
                        VarBase = mem.VarBase;
                        lastTick = -1;
                        InMatch = false;
                        Say(string.Format("[INFO] ゲームのデータを取り直しました  0x{0:X}", mem.VarBase));
                    }
                    _gameFrame = 0;
                    Thread.Sleep(1);
                    continue;
                }
                _gameFrame = tick;

                if (tick > 0)
                {
                    // --- 試合中 ---
                    // ゲームが1フレーム進むごとに、自分の入力を DelayFrames 先のフレーム宛に送り、
                    // このフレーム宛に届いている入力を当てる。実時間は一切使わないのでずれない。
                    // フレームの変わり目を逃さないよう、譲らずに短く回して待つ
                    if (tick == lastTick) { Thread.SpinWait(60); continue; }

                    if (!InMatch || lastTick < 0 || tick < lastTick)
                    {
                        InMatch = true;
                        for (int i = 0; i < RingSize; i++)
                            for (int k = 0; k < Proto.MaxPlayers; k++) _ringFrame[i][k] = -1;
                        for (int i = 0; i < Proto.MaxPlayers; i++) lastWritten[i] = 0;
                        haveWritten = false;
                        Say(string.Format("[INFO] 試合の同期を開始しました  ゲームframe={0}  遅延={1}",
                            tick, DelayFrames));
                    }
                    if (lastTick > 0 && tick > lastTick + 1) MissedTicks += tick - lastTick - 1;
                    lastTick = tick;
                    AppliedFrame = tick;

                    ushort mask = 0;
                    for (int i = 0; i < Buttons; i++)
                        if (Util.KeyDown(Keys[i])) mask |= (ushort)(1 << i);
                    LocalMask = mask;

                    // 自分が遅れているぶんだけ、先のフレーム宛に送る。
                    // そうしないと、先行している相手が自分の入力を待てずに取りこぼす。
                    // 送り先は増やす方向にしか変えない。減らすと同じフレームへ
                    // 二重に書いてしまい、どちらが残るかが受け手ごとに変わる。
                    int lag = _maxGameFrame - tick;
                    if (lag < 0) lag = 0;
                    int want = DelayFrames + lag + 2;
                    if (want > MaxSendDelay) want = MaxSendDelay;     // 暴走よけ
                    if (want > _sendDelay) _sendDelay = want;
                    if (_sendDelay < DelayFrames) _sendDelay = DelayFrames;

                    var pkt = Proto.Build(Proto.MsgInput,
                        Proto.InputPayloadWithTick(MySlot, tick + _sendDelay, mask, tick));
                    try { lock (st) st.Write(pkt, 0, pkt.Length); TxCount++; }
                    catch { Say("サーバーとの接続が切れました。"); break; }

                    // いま見えているフレームの「次」のぶんを置いておく
                    int target = tick + WriteAhead;
                    int mi = Mod(target);
                    var stampRow = _ringFrame[mi];
                    var maskRow = _ring[mi];
                    for (int s = 1; s <= Proto.MaxPlayers; s++)
                    {
                        if (s == MySlot && !ApplyOwn) continue;
                        bool have = (stampRow[s - 1] == target);
                        if (!have) StallCount++;
                        // 届いていないフレームは直前の入力を保つ
                        ushort mk = have ? maskRow[s - 1] : lastWritten[s - 1];
                        if (haveWritten && mk == lastWritten[s - 1]) continue;
                        int b = NetBase + (s - 1) * Buttons;
                        for (int i = 0; i < Buttons; i++)
                            mem.WriteVar(b + i, ((mk >> i) & 1));
                        lastWritten[s - 1] = mk;
                    }
                    haveWritten = true;
                    FrameLag = 0;

                    // 突き合わせは 600 変数ほど読むので重い。ここでやると
                    // ゲームのフレームを見逃すため、番号を渡すだけにして別スレッドに任せる。
                    if (CheckEvery > 0 && tick % CheckEvery == 0 && tick != checkNextTick)
                    {
                        checkNextTick = tick;
                        _checkWanted = tick;
                    }

                    // 進みすぎていたら、ゲームを一瞬止めて他を待つ。
                    // ここを止めないと、まだ届いていない入力のフレームに突っ込んでしまう。
                    int minf = _minGameFrame;
                    if (minf > 0)
                    {
                        AheadBy = (tick + DelayFrames) - minf;
                        if (ThrottleAhead && AheadBy > AheadLimit && _susp != IntPtr.Zero)
                        {
                            WaitCount++;
                            int ms = (AheadBy - AheadLimit) * 8;
                            if (ms > 40) ms = 40;
                            try
                            {
                                NtSuspendProcess(_susp);
                                Thread.Sleep(ms);
                                NtResumeProcess(_susp);
                            }
                            catch { }
                        }
                    }

                    // 毎秒、取りこぼしの数を残す
                    if (tick >= statNextTick)
                    {
                        if (statNextTick > 0)
                            Say(string.Format("[INFO] frame={0}  入力待ち {1}  見逃し {2}  進み {3}  先読み {4}",
                                tick, StallCount - statStall, MissedTicks, AheadBy, _sendDelay));
                        statStall = StallCount;
                        statNextTick = tick + 60;
                    }
                }
                else
                {
                    // --- 試合外 (タイトルやキャラ選択) ---
                    statNextTick = 0;
                    // フレームカウンタが動かないので、自前の 60Hz で送り、
                    // 届いている最新の入力をそのまま反映する。
                    if (InMatch) { InMatch = false; lastTick = -1; haveWritten = false; }
                    if (pacer.Elapsed.TotalMilliseconds < nextDue) { Thread.Sleep(0); continue; }
                    nextDue += frameMs;
                    double behind = pacer.Elapsed.TotalMilliseconds - nextDue;
                    if (behind > frameMs * 4) nextDue = pacer.Elapsed.TotalMilliseconds + frameMs;
                    localFrame++;

                    ushort mask = 0;
                    for (int i = 0; i < Buttons; i++)
                        if (Util.KeyDown(Keys[i])) mask |= (ushort)(1 << i);
                    LocalMask = mask;
                    var pkt = Proto.Build(Proto.MsgInput, Proto.InputPayload(MySlot, 0, mask));
                    try { lock (st) st.Write(pkt, 0, pkt.Length); TxCount++; }
                    catch { Say("サーバーとの接続が切れました。"); break; }

                    var cur = RemoteMasks;
                    for (int s = 1; s <= Proto.MaxPlayers; s++)
                    {
                        if (s == MySlot && !ApplyOwn) continue;
                        ushort mk = cur[s - 1];
                        if (haveWritten && mk == lastWritten[s - 1]) continue;
                        int b = NetBase + (s - 1) * Buttons;
                        for (int i = 0; i < Buttons; i++)
                            mem.WriteVar(b + i, ((mk >> i) & 1));
                        lastWritten[s - 1] = mk;
                    }
                    haveWritten = true;
                    AppliedFrame = 0;
                    FrameLag = 0;
                }
            }
        }
    }

    internal sealed class ClientForm : Form
    {
        private const int Buttons = Proto.ButtonsPerPlayer;

        // 左, 上, 下, 右, A, B の順。プレイヤーごとのキー配列。
        private static readonly string[] Clusters =
        {
            "Left,Up,Down,Right,Z,X",   // 1P
            "E,W,R,Q,T,Y",              // 2P
            "D,S,F,A,G,H",              // 3P
            "O,I,P,U,J,K"               // 4P
        };

        private readonly ClientEngine _engine = new ClientEngine();
        private readonly string _iniPath;

        private readonly TextBox _name = new TextBox();
        private readonly TextBox _host = new TextBox();
        private readonly TextBox _port = new TextBox();
        private readonly ComboBox _slot = new ComboBox();
        private readonly NumericUpDown _delay = new NumericUpDown();
        private readonly Button _btn = new Button();
        private readonly CheckBox _advanced = new CheckBox();
        private readonly Panel _advPanel = new Panel();
        private readonly TextBox _netbase = new TextBox();
        private readonly TextBox _index = new TextBox();
        private readonly ComboBox _keyMode = new ComboBox();
        private readonly TextBox _keys = new TextBox();
        private readonly CheckBox _applyOwn = new CheckBox();

        private readonly Label _big = new Label();
        private readonly Label _stat = new Label();
        private readonly ListView _list = new ListView();
        private readonly Label _netLine = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly WinTimer _timer = new WinTimer();
        private FileLogger _file;

        private long _lastTx, _lastRx;

        public ClientForm()
        {
            _iniPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "LvKSyncClient.ini");

            Text = "LvKSync プレイヤー";
            ClientSize = new Size(680, 640);
            MinimumSize = new Size(620, 560);
            Font = new Font("Yu Gothic UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();
            LoadSettings();

            _file = new FileLogger("LvKSyncClient");
            _engine.Log += delegate (string s) { Post(delegate { AppendLog(s); }); };
            _engine.Changed += delegate { Post(RefreshAll); };

            _timer.Interval = 500;
            _timer.Tick += delegate { RefreshAll(); };
            _timer.Start();

            RefreshAll();
        }

        #region 画面づくり

        private void BuildLayout()
        {
            // 下から積む (Dock=Top の順序が逆になるため)
            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.FromArgb(30, 32, 38);
            _log.ForeColor = Color.Gainsboro;
            _log.Font = new Font("Consolas", 9F);
            Controls.Add(_log);
            Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "  ログ",
                TextAlign = ContentAlignment.MiddleLeft
            });

            _netLine.Dock = DockStyle.Top;
            _netLine.Height = 22;
            _netLine.Font = new Font("Consolas", 8.5F);
            _netLine.ForeColor = Color.FromArgb(90, 90, 90);
            _netLine.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_netLine);

            _list.Dock = DockStyle.Top;
            _list.Height = 128;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.Columns.Add("プレイヤー", 80);
            _list.Columns.Add("名前", 230);
            _list.Columns.Add("入力", 90);
            _list.Columns.Add("状態", 150);
            Controls.Add(_list);

            _stat.Dock = DockStyle.Top;
            _stat.Height = 26;
            _stat.TextAlign = ContentAlignment.MiddleLeft;
            _stat.ForeColor = Color.FromArgb(60, 60, 60);
            Controls.Add(_stat);

            _big.Dock = DockStyle.Top;
            _big.Height = 44;
            _big.Font = new Font("Yu Gothic UI", 15F, FontStyle.Bold);
            _big.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_big);

            // 詳細設定
            _advPanel.Dock = DockStyle.Top;
            _advPanel.Height = 88;
            _advPanel.Visible = false;
            _advPanel.Controls.Add(Lab("操作キー", 10, 8));
            _keyMode.SetBounds(80, 4, 210, 24);
            _keyMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _keyMode.Items.AddRange(new object[]
            {
                "自動 (プレイヤー番号に合わせる)", "1P の配列", "2P の配列", "3P の配列", "4P の配列", "手動で入力"
            });
            _keyMode.SelectedIndex = 0;
            _keyMode.SelectedIndexChanged += delegate { SyncKeyBox(); };
            _advPanel.Controls.Add(_keyMode);
            _keys.SetBounds(300, 4, 250, 24);
            _keys.ReadOnly = true;
            _advPanel.Controls.Add(_keys);
            _advPanel.Controls.Add(Lab("左, 上, 下, 右, A, B の順", 556, 8));

            _advPanel.Controls.Add(Lab("入力ブロック先頭  V[", 10, 40));
            _netbase.SetBounds(140, 36, 60, 24);
            _netbase.Text = "9001";
            _advPanel.Controls.Add(_netbase);
            _advPanel.Controls.Add(Lab("]", 202, 40));
            _advPanel.Controls.Add(Lab("このPCの何番目のゲームか", 230, 40));
            _index.SetBounds(392, 36, 40, 24);
            _index.Text = "0";
            _advPanel.Controls.Add(_index);
            _applyOwn.SetBounds(446, 38, 210, 22);
            _applyOwn.Text = "自分の入力もゲームへ書く";
            _applyOwn.Checked = true;
            _advPanel.Controls.Add(_applyOwn);
            Controls.Add(_advPanel);

            // 接続設定
            var top = new Panel { Dock = DockStyle.Top, Height = 82 };
            top.Controls.Add(Lab("名前", 10, 12));
            _name.SetBounds(56, 8, 180, 24);
            _name.MaxLength = 12;
            top.Controls.Add(_name);

            top.Controls.Add(Lab("サーバー", 250, 12));
            _host.SetBounds(310, 8, 130, 24);
            _host.Text = "127.0.0.1";
            top.Controls.Add(_host);
            top.Controls.Add(Lab("ポート", 450, 12));
            _port.SetBounds(498, 8, 60, 24);
            _port.Text = "47801";
            top.Controls.Add(_port);

            top.Controls.Add(Lab("プレイヤー番号", 10, 48));
            _slot.SetBounds(112, 44, 124, 24);
            _slot.DropDownStyle = ComboBoxStyle.DropDownList;
            _slot.Items.AddRange(new object[] { "おまかせ", "1P", "2P", "3P", "4P" });
            _slot.SelectedIndex = 0;
            _slot.SelectedIndexChanged += delegate { SyncKeyBox(); };
            top.Controls.Add(_slot);

            top.Controls.Add(Lab("入力遅延", 250, 48));
            _delay.SetBounds(310, 44, 50, 24);
            _delay.Minimum = 0; _delay.Maximum = 15; _delay.Value = 2;
            top.Controls.Add(_delay);
            top.Controls.Add(Lab("フレーム", 364, 48));

            _advanced.SetBounds(430, 46, 76, 22);
            _advanced.Text = "詳細設定";
            _advanced.CheckedChanged += delegate { _advPanel.Visible = _advanced.Checked; };
            top.Controls.Add(_advanced);

            _btn.SetBounds(560, 43, 100, 26);
            _btn.Text = "接続";
            _btn.Click += OnToggle;
            top.Controls.Add(_btn);
            Controls.Add(top);

            SyncKeyBox();
        }

        private static Label Lab(string t, int x, int y)
        {
            return new Label { Text = t, AutoSize = true, Left = x, Top = y };
        }

        #endregion

        #region 設定の読み書き

        private void LoadSettings()
        {
            var ini = Ini.Load(_iniPath);
            _name.Text = ini.Get("name", Environment.UserName);
            _host.Text = ini.Get("host", "127.0.0.1");
            _port.Text = ini.GetInt("port", 47801).ToString();
            int s = ini.GetInt("slot", 0);
            _slot.SelectedIndex = (s >= 1 && s <= 4) ? s : 0;
            int d = ini.GetInt("delay", 2);
            _delay.Value = Math.Max(0, Math.Min(15, d));
            _netbase.Text = ini.GetInt("netbase", 9001).ToString();
            _index.Text = ini.GetInt("index", 0).ToString();
            _applyOwn.Checked = ini.GetBool("applyown", true);
            string lk = ini.Get("localkeys", "");
            if (!string.IsNullOrEmpty(lk))
            {
                _keyMode.SelectedIndex = 5;
                _keys.Text = lk;
            }
            SyncKeyBox();
        }

        private void SaveSettings()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# LvKSync プレイヤー設定 (画面で変えると自動で保存されます)");
                sb.AppendLine("name = " + _name.Text.Trim());
                sb.AppendLine("host = " + _host.Text.Trim());
                sb.AppendLine("port = " + _port.Text.Trim());
                sb.AppendLine("slot = " + _slot.SelectedIndex);
                sb.AppendLine("delay = " + (int)_delay.Value);
                sb.AppendLine("netbase = " + _netbase.Text.Trim());
                sb.AppendLine("index = " + _index.Text.Trim());
                sb.AppendLine("applyown = " + (_applyOwn.Checked ? "1" : "0"));
                sb.AppendLine("localkeys = " + (_keyMode.SelectedIndex == 5 ? _keys.Text.Trim() : ""));
                File.WriteAllText(_iniPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        /// <summary>操作キーの表示を、選んだモードと希望プレイヤー番号から決める。</summary>
        private void SyncKeyBox()
        {
            int mode = _keyMode.SelectedIndex;
            if (mode == 5) { _keys.ReadOnly = false; return; }
            _keys.ReadOnly = true;
            int cluster;
            if (mode >= 1 && mode <= 4) cluster = mode - 1;
            else
            {
                // 自動: 参加済みならその番号、まだなら希望番号 (おまかせなら 1P)
                int s = _engine.MySlot > 0 ? _engine.MySlot : _slot.SelectedIndex;
                cluster = (s >= 1 && s <= 4) ? s - 1 : 0;
            }
            _keys.Text = Clusters[cluster];
        }

        #endregion

        private void Post(Action a)
        {
            if (IsHandleCreated) { try { BeginInvoke(a); } catch { } }
        }

        private void AppendLog(string s)
        {
            if (_log.TextLength > 60000) _log.Clear();
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            if (_file != null) _file.Write(s);
        }

        private void OnToggle(object sender, EventArgs e)
        {
            if (_engine.Running)
            {
                _engine.Stop();
                AppendLog("切断しました。");
                _btn.Text = "接続";
                SetInputs(true);
                RefreshAll();
                return;
            }

            var keys = Util.ParseKeys(_keys.Text);
            if (keys.Length != Buttons)
            {
                MessageBox.Show("操作キーは " + Buttons + " 個を カンマ区切りで指定してください。\n順番は 左, 上, 下, 右, A, B です。",
                    "LvKSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_name.Text.Trim().Length == 0)
            {
                MessageBox.Show("名前を入れてください。サーバーの一覧に表示されます。",
                    "LvKSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _name.Focus();
                return;
            }

            int port, netbase, index;
            if (!int.TryParse(_port.Text.Trim(), out port)) port = 47801;
            if (!int.TryParse(_netbase.Text.Trim(), out netbase)) netbase = 9001;
            if (!int.TryParse(_index.Text.Trim(), out index)) index = 0;

            _engine.Name = _name.Text.Trim();
            _engine.Host = _host.Text.Trim();
            _engine.Port = port;
            _engine.WantSlot = _slot.SelectedIndex;
            _engine.DelayFrames = (int)_delay.Value;
            _engine.NetBase = netbase;
            _engine.GameIndex = index;
            _engine.ApplyOwn = _applyOwn.Checked;
            _engine.Keys = keys;

            SaveSettings();
            _engine.Start();
            _btn.Text = "切断";
            SetInputs(false);
            RefreshAll();
        }

        private void SetInputs(bool on)
        {
            _name.Enabled = on; _host.Enabled = on; _port.Enabled = on;
            _slot.Enabled = on; _keyMode.Enabled = on; _netbase.Enabled = on;
            _index.Enabled = on; _applyOwn.Enabled = on;
            _keys.Enabled = on;
            // 入力遅延だけは接続中でも変えられるようにしてある
        }

        private static string MaskText(ushort m)
        {
            const string names = "LUDRAB";
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = ((m >> i) & 1) != 0 ? names[i] : '.';
            return new string(c);
        }

        private void RefreshAll()
        {
            if (_engine.Running) _engine.DelayFrames = (int)_delay.Value;

            int my = _engine.MySlot;
            if (!_engine.Running && _btn.Text == "切断") { _btn.Text = "接続"; SetInputs(true); }

            if (my > 0 && _engine.Running)
            {
                _big.Text = "  あなたは " + my + "P です";
                _big.ForeColor = Color.FromArgb(20, 110, 60);
            }
            else
            {
                _big.Text = "  " + _engine.Phase;
                _big.ForeColor = Color.FromArgb(90, 90, 90);
            }
            if (_keyMode.SelectedIndex == 0) SyncKeyBox();

            var sb = new StringBuilder("  ");
            long tx = _engine.TxCount, rx = _engine.RxCount;
            sb.AppendFormat("送信 {0}/秒   受信 {1}/秒   ", (tx - _lastTx) * 2, (rx - _lastRx) * 2);
            _lastTx = tx; _lastRx = rx;
            if (_engine.RttMs >= 0)
                sb.AppendFormat("往復 {0}ms (推奨遅延 {1}f)   ", _engine.RttMs, ClientEngine.SuggestDelay(_engine.RttMs));
            else sb.Append("往復 --   ");
            if (_engine.Pid != 0) sb.AppendFormat("ゲーム pid {0}   ", _engine.Pid);
            if (_engine.VarBase != 0) sb.AppendFormat("データ 0x{0:X}", _engine.VarBase);
            _stat.Text = sb.ToString();

            var roster = _engine.Roster;
            var masks = _engine.RemoteMasks;
            int bits = _engine.ConnectedBits;
            _list.BeginUpdate();
            while (_list.Items.Count < Proto.MaxPlayers)
                _list.Items.Add(new ListViewItem(new string[] { "", "", "", "" }));
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                var it = _list.Items[i - 1];
                bool on = ((bits >> (i - 1)) & 1) != 0;
                bool me = (i == my);
                it.SubItems[0].Text = i + "P";
                it.SubItems[1].Text = roster[i] == null ? (on ? "(名前なし)" : "空き") : roster[i];
                ushort mk = me ? _engine.LocalMask : masks[i - 1];
                it.SubItems[2].Text = on ? MaskText(mk) : "";
                it.SubItems[3].Text = me ? "あなた" : (on ? "接続中" : "");
                it.ForeColor = me ? Color.FromArgb(20, 110, 60) : (on ? Color.Black : Color.Gray);
                it.Font = me ? new Font(_list.Font, FontStyle.Bold) : _list.Font;
            }
            _list.EndUpdate();

            var v = _engine.ReadNetBlock();
            if (v == null) _netLine.Text = "  ゲームのメモリ: まだ読めません";
            else
            {
                var nb = new StringBuilder("  ゲームのメモリ V[");
                nb.Append(_engine.NetBase).Append("..").Append(_engine.NetBase + v.Length - 1).Append("] = ");
                for (int s = 0; s < Proto.MaxPlayers; s++)
                {
                    nb.Append(s + 1).Append("P:");
                    for (int b = 0; b < Buttons; b++) nb.Append(v[s * Buttons + b] != 0 ? "1" : "0");
                    nb.Append("  ");
                }
                _netLine.Text = nb.ToString();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            SaveSettings();
            if (_engine.Running) _engine.Stop();
            if (_file != null) { _file.Dispose(); _file = null; }
            base.OnFormClosing(e);
        }

        /// <summary>起動引数で画面の初期値を埋める。--start があればそのまま接続する。</summary>
        private void ApplyArgs(string[] args)
        {
            bool auto = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string nx = (i + 1 < args.Length) ? args[i + 1] : null;
                switch (a)
                {
                    case "--start": auto = true; break;
                    case "--name": if (nx != null) { _name.Text = nx; i++; } break;
                    case "--host": if (nx != null) { _host.Text = nx; i++; } break;
                    case "--port": if (nx != null) { _port.Text = nx; i++; } break;
                    case "--slot":
                        if (nx != null)
                        {
                            int n;
                            if (int.TryParse(nx, out n) && n >= 0 && n <= Proto.MaxPlayers)
                                _slot.SelectedIndex = n;
                            i++;
                        }
                        break;
                    case "--delay":
                        if (nx != null)
                        {
                            int n;
                            if (int.TryParse(nx, out n) && n >= 0 && n <= 15) _delay.Value = n;
                            i++;
                        }
                        break;
                    case "--netbase": if (nx != null) { _netbase.Text = nx; i++; } break;
                    case "--index": if (nx != null) { _index.Text = nx; i++; } break;
                    case "--local-keys":
                        if (nx != null) { _keyMode.SelectedIndex = 5; _keys.Text = nx; i++; }
                        break;
                    case "--apply-own": _applyOwn.Checked = true; break;
                    case "--throttle": _engine.ThrottleAhead = true; break;
                }
            }
            SyncKeyBox();
            if (auto) Shown += delegate { OnToggle(this, EventArgs.Empty); };
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new ClientForm();
            f.ApplyArgs(args);
            Application.Run(f);
        }
    }
}
