// LvKSyncServerGui - 中継サーバーの画面つき版
//
// 誰が何Pに座っているかを一覧で確認できる。ゲームには一切触らない。

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace LvKSync
{
    internal sealed class Peer
    {
        public TcpClient Tcp;
        public NetworkStream Stream;
        public int Slot;
        public string Name = "";
        public string Remote = "";
        public ushort Mask;
        public long RxCount;
        public long RxAtLastSample;
        public int GameFrame;
        public int PrevGameFrame;
        public int RxPerSec;
        public DateTime JoinedAt = DateTime.Now;
        public long LastInputMs;
        public bool InputStalled;

        // キャラ選択の照合用
        public uint SelHash;
        public int SelStable;
        public long SelAtMs = -1;
    }

    /// <summary>入力を受けて全員へ配るだけの中継。GUI から使う。</summary>
    internal sealed class RelayEngine
    {
        private readonly object _gate = new object();
        private readonly Peer[] _slots = new Peer[Proto.MaxPlayers + 1];
        private TcpListener _listener;
        private volatile bool _stop;

        public int MaxPlayers = Proto.MaxPlayers;
        public int Hz = 60;
        public long TxCount;

        /// <summary>配信フレームの通し番号。クライアントはこれで入力を並べる。</summary>
        private int _frame;

        /// <summary>入力の履歴。絵で見せるために配信のたびに書き足す。</summary>
        public const int HistorySize = InputView.HistorySize;
        public readonly ushort[,] History = new ushort[Proto.MaxPlayers + 1, HistorySize];
        public volatile int HistoryPos;

        /// <summary>ログの詳しさ。0=標準 1=詳しい 2=全部</summary>
        public int Verbosity;

        /// <summary>ゲームフレーム0 が配信フレームのいくつに当たるか。全員がこれを使う。</summary>
        private int _matchBase = int.MinValue;      // MinValue = まだ決まっていない
        private bool _matchBaseSet;

        // 配信の遅れの記録 (「全部」のときに毎秒まとめて出す)
        private double _lateSum, _lateMax;
        private int _lateCount;

        /// <summary>この値より時間がかかった送信を警告に出す (ミリ秒)</summary>
        public double SendWarnMs = 10;

        // 既定では Thread.Sleep(1) が約15ms眠ってしまい 60Hz が保てない。
        // winmm でタイマーの粒度を上げる。
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        /// <summary>入力がこの時間途切れたら警告に出す (ミリ秒)</summary>
        public long InputGapWarnMs = 250;

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // --- ずれ検知 ---
        private const int CheckRing = 64;
        private readonly int[] _chkFrame = new int[CheckRing];
        private readonly uint[,][] _chkHash = new uint[CheckRing, Proto.MaxPlayers + 1][];
        private readonly int[,][] _chkFirst = new int[CheckRing, Proto.MaxPlayers + 1][];
        private readonly int[,][] _chkCount = new int[CheckRing, Proto.MaxPlayers + 1][];
        private readonly bool[,] _chkHave = new bool[CheckRing, Proto.MaxPlayers + 1];
        private readonly object _chkGate = new object();

        /// <summary>最後にずれを見つけたフレーム。0 ならまだ無事。</summary>
        public int DesyncFrame;
        public long DesyncCount;
        public int CheckedFrames;


        public event Action<string> Log;
        public event Action RosterChanged;

        private void Say(string s)
        {
            var h = Log;
            if (h != null) h(s);
        }

        /// <summary>「詳しい」以上のときだけ出す。</summary>
        private void Detail(string s)
        {
            if (Verbosity >= 1) Say(s);
        }

        /// <summary>「全部」のときだけ出す。</summary>
        private void Trace(string s)
        {
            if (Verbosity >= 2) Say(s);
        }

        /// <summary>直近の配信の遅れをまとめて返し、集計を初期化する。</summary>
        public string TakeLateSummary()
        {
            int n = _lateCount;
            if (n == 0) return null;
            double avg = _lateSum / n, max = _lateMax;
            _lateSum = 0; _lateMax = 0; _lateCount = 0;
            return string.Format("配信の遅れ 平均{0:F1}ms 最大{1:F1}ms ({2}回)", avg, max, n);
        }

        /// <summary>押されているボタンを読める形に。</summary>
        public static string ButtonNames(ushort m)
        {
            string[] names = { "左", "上", "下", "右", "A", "B" };
            var sb = new StringBuilder();
            for (int i = 0; i < 6; i++)
                if (((m >> i) & 1) != 0)
                {
                    if (sb.Length > 0) sb.Append('+');
                    sb.Append(names[i]);
                }
            return sb.Length == 0 ? "(なし)" : sb.ToString();
        }

        public static string MaskBits(ushort m)
        {
            const string names = "LUDRAB";
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = ((m >> i) & 1) != 0 ? names[i] : '.';
            return new string(c);
        }

        private void Changed()
        {
            var h = RosterChanged;
            if (h != null) h();
        }

        public bool Running { get { return _listener != null; } }

        public Peer[] Snapshot()
        {
            var a = new Peer[Proto.MaxPlayers + 1];
            lock (_gate)
                for (int i = 1; i <= Proto.MaxPlayers; i++) a[i] = _slots[i];
            return a;
        }

        public void Start(IPAddress bind, int port)
        {
            _stop = false;
            var l = new TcpListener(bind, port);
            l.Start();
            _listener = l;
            try { timeBeginPeriod(1); } catch { }
            Say(string.Format("[INFO] 待ち受け開始  {0}:{1}   最大 {2} 人", bind, port, MaxPlayers));
            new Thread(AcceptLoop) { IsBackground = true }.Start();
            new Thread(BroadcastLoop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            _stop = true;
            var l = _listener;
            _listener = null;
            try { if (l != null) l.Stop(); } catch { }
            lock (_gate)
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    if (_slots[i] == null) continue;
                    try { _slots[i].Tcp.Close(); } catch { }
                    _slots[i] = null;
                }
            try { timeEndPeriod(1); } catch { }
            Say("[INFO] 停止しました");
            Changed();
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                var l = _listener;
                if (l == null) return;
                TcpClient tcp;
                try { tcp = l.AcceptTcpClient(); }
                catch { return; }
                var t = tcp;
                new Thread(delegate () { HandlePeer(t); }) { IsBackground = true }.Start();
            }
        }

        private void HandlePeer(TcpClient tcp)
        {
            tcp.NoDelay = true;
            NetworkStream st;
            string remote;
            try { st = tcp.GetStream(); remote = tcp.Client.RemoteEndPoint.ToString(); }
            catch { return; }

            byte type; byte[] payload;
            if (!Proto.Read(st, out type, out payload) || type != Proto.MsgHello)
            {
                try { tcp.Close(); } catch { }
                return;
            }

            int want; string pname;
            Proto.ParseHello(payload, out want, out pname);
            if (string.IsNullOrEmpty(pname)) pname = "(名前なし)";

            var peer = new Peer { Tcp = tcp, Stream = st, Remote = remote, Name = pname };

            int assigned = 0;
            lock (_gate)
            {
                if (want >= 1 && want <= MaxPlayers && _slots[want] == null) assigned = want;
                else
                    for (int i = 1; i <= MaxPlayers && assigned == 0; i++)
                        if (_slots[i] == null) assigned = i;
                if (assigned != 0) { peer.Slot = assigned; _slots[assigned] = peer; }
            }

            if (assigned == 0)
            {
                try { var f = Proto.Build(Proto.MsgFull, null); st.Write(f, 0, f.Length); } catch { }
                try { tcp.Close(); } catch { }
                Say(string.Format("[WARN] 満員のため {0} ({1}) を拒否しました", pname, remote));
                return;
            }

            try
            {
                var w = Proto.Build(Proto.MsgWelcome, new byte[] { (byte)assigned, (byte)MaxPlayers });
                lock (st) st.Write(w, 0, w.Length);
            }
            catch { }
            Say(string.Format("[INFO] {0}P に {1} が参加しました  ({2})", assigned, pname, remote));
            BroadcastRoster();

            while (!_stop)
            {
                if (!Proto.Read(st, out type, out payload)) break;
                if (type == Proto.MsgInput && payload.Length >= 7)
                {
                    peer.PrevGameFrame = peer.GameFrame;
                    // 7バイト目から先があれば、それが送り主の実際のゲームフレーム。
                    // 無ければ従来どおり宛先フレームで代用する。
                    peer.GameFrame = (payload.Length >= 11)
                        ? BitConverter.ToInt32(payload, 7)
                        : BitConverter.ToInt32(payload, 1);
                    ushort nm = BitConverter.ToUInt16(payload, 5);
                    if (nm != peer.Mask)
                        Detail(string.Format("[INFO] {0}P {1} が {2} を操作しました  ({3})",
                            peer.Slot, peer.Name, ButtonNames(nm), MaskBits(nm)));
                    peer.Mask = nm;
                    peer.RxCount++;
                    peer.LastInputMs = _clock.ElapsedMilliseconds;

                    // 試合中の入力は、フレーム番号を付けたまま全員へ回す。
                    // 受け取った側は「そのフレームに来たら当てる」ので実時間に左右されない。
                    if (peer.GameFrame > 0) RelayInput(payload);
                    if (peer.InputStalled)
                    {
                        peer.InputStalled = false;
                        Trace(string.Format("[INFO] {0}P {1} からの入力が戻りました", peer.Slot, peer.Name));
                    }
                }
                else if (type == Proto.MsgSelCheck && payload.Length >= 6)
                {
                    peer.SelHash = BitConverter.ToUInt32(payload, 1);
                    peer.SelStable = payload[5];
                    peer.SelAtMs = _clock.ElapsedMilliseconds;
                    JudgeSelect();
                }
                else if (type == Proto.MsgReset)
                {
                    Say(string.Format("{0}P {1} がカーソルの初期化を送りました。全員へ回します。",
                        peer.Slot, peer.Name));
                    RelayExcept(Proto.MsgReset, payload, peer.Slot);
                }
                else if (type == Proto.MsgMenu)
                {
                    // 設定メニューの状態。1P が配り、ほかは合わせる。
                    int mslot, mkey, mopen;
                    var vals = new int[Proto.MenuValueCount];
                    if (Proto.ReadMenu(payload, out mslot, out mkey, out mopen, vals) && mslot == 1)
                    {
                        MenuValues = vals;
                        HaveMenu = true;
                        if (mopen != 0) { MenuOpen = true; Say("1P が設定メニューを開きました。"); }
                        if (mkey >= 5) { MenuOpen = false; Say("1P が設定メニューを閉じました。"); }
                        RelayExcept(Proto.MsgMenu, payload, peer.Slot);
                        Changed();
                    }
                }
                else if (type == Proto.MsgPing && payload.Length >= 8)
                {
                    var pong = Proto.Build(Proto.MsgPong, payload);
                    try { lock (st) st.Write(pong, 0, pong.Length); } catch { break; }
                }
                else if (type == Proto.MsgCheck && payload.Length >= 6)
                {
                    int cf = BitConverter.ToInt32(payload, 1);
                    int nb = payload[5];
                    if (nb > 0 && payload.Length >= 6 + nb * 10)
                    {
                        var fs = new int[nb];
                        var cs = new int[nb];
                        var hs = new uint[nb];
                        for (int k = 0; k < nb; k++)
                        {
                            int bo = 6 + k * 10;
                            fs[k] = BitConverter.ToInt32(payload, bo);
                            cs[k] = BitConverter.ToUInt16(payload, bo + 4);
                            hs[k] = BitConverter.ToUInt32(payload, bo + 6);
                        }
                        CheckState(peer, cf, fs, cs, hs);
                    }
                }
                else if (type == Proto.MsgBye) break;
            }

            lock (_gate) { if (_slots[peer.Slot] == peer) _slots[peer.Slot] = null; }
            try { tcp.Close(); } catch { }
            Say(string.Format("[INFO] {0}P の {1} が退出しました", peer.Slot, peer.Name));
            BroadcastRoster();
        }

        /// <summary>
        /// 同じフレームのチェックサムを突き合わせる。
        /// 食い違ったら、そのフレームでゲームの中身が分かれたということ。
        /// </summary>
        private void CheckState(Peer peer, int frame, int[] firsts, int[] counts, uint[] hashes)
        {
            if (frame <= 0) return;
            int i = ((frame % CheckRing) + CheckRing) % CheckRing;
            string bad = null;
            lock (_chkGate)
            {
                if (_chkFrame[i] != frame)
                {
                    _chkFrame[i] = frame;
                    for (int k = 0; k <= Proto.MaxPlayers; k++) _chkHave[i, k] = false;
                }
                _chkHash[i, peer.Slot] = hashes;
                _chkFirst[i, peer.Slot] = firsts;
                _chkCount[i, peer.Slot] = counts;
                _chkHave[i, peer.Slot] = true;

                for (int k = 1; k <= Proto.MaxPlayers; k++)
                {
                    if (k == peer.Slot || !_chkHave[i, k]) continue;
                    var other = _chkHash[i, k];
                    if (other == null || other.Length != hashes.Length) break;
                    CheckedFrames++;
                    for (int b = 0; b < hashes.Length; b++)
                    {
                        if (hashes[b] == other[b]) continue;
                        int first = firsts[b];
                        int last = first + counts[b] - 1;
                        bad = string.Format(
                            "[WARN] frame {0} で状態が分かれました  V[{1}..{2}]  {3}P={4:X8} {5}P={6:X8}",
                            frame, first, last, peer.Slot, hashes[b], k, other[b]);
                        DesyncFrame = frame;
                        DesyncCount++;
                        break;
                    }
                    break;
                }
            }
            if (bad != null) Say(bad);
        }

        /// <summary>キャラ選択が揃っているか。</summary>
        public volatile int SelState = Proto.SelUnknown;
        public volatile int SelPeers;
        private int _lastSelState = -1;

        /// <summary>
        /// キャラ選択の状態を突き合わせる。
        ///
        /// 試合中とちがってフレーム番号という共通の物差しが無いので、
        /// 「値が変わってから何回同じだったか」を各自に送ってもらい、
        /// 全員が落ち着いてから食い違いを見る。こうしないと、
        /// 誰かのカーソルが動いた直後の当たり前の食い違いで
        /// 「ずれています」と言ってしまう。
        /// </summary>
        private void JudgeSelect()
        {
            const int StableEnough = 3;
            const long FreshMs = 3000;
            var peers = Snapshot();
            long now = _clock.ElapsedMilliseconds;
            int n = 0; bool allStable = true; bool same = true;
            uint first = 0; bool haveFirst = false;
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                var pr = peers[i];
                if (pr == null || pr.SelAtMs < 0) continue;
                if (now - pr.SelAtMs > FreshMs) continue;   // 古い報告は使わない
                n++;
                if (pr.SelStable < StableEnough) allStable = false;
                if (!haveFirst) { first = pr.SelHash; haveFirst = true; }
                else if (pr.SelHash != first) same = false;
            }
            int st;
            if (n < 2) st = Proto.SelUnknown;
            else if (same) st = Proto.SelSame;
            else if (!allStable) st = Proto.SelChecking;
            else st = Proto.SelDiffer;
            SelState = st;
            SelPeers = n;
            if (st != _lastSelState)
            {
                _lastSelState = st;
                if (st == Proto.SelDiffer)
                    Say("[注意] キャラ選択がずれています。1 キーで全員のカーソルを初期化できます。");
                else if (st == Proto.SelSame)
                    Say(string.Format("キャラ選択が揃いました ({0}人)。", n));
                var pkt = Proto.Build(Proto.MsgSelState,
                    new byte[] { (byte)st, (byte)n });
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    if (peers[i] == null) continue;
                    try { lock (peers[i].Stream) peers[i].Stream.Write(pkt, 0, pkt.Length); }
                    catch { }
                }
                Changed();
            }
        }

        /// <summary>設定メニューの値。1P が配ってきたもの。</summary>
        public volatile int[] MenuValues = new int[Proto.MenuValueCount];
        public volatile bool HaveMenu;
        public volatile bool MenuOpen;

        /// <summary>送り主以外へ回す。</summary>
        private void RelayExcept(byte type, byte[] payload, int fromSlot)
        {
            var pkt = Proto.Build(type, payload);
            var peers = Snapshot();
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                var pr = peers[i];
                if (pr == null || pr.Slot == fromSlot) continue;
                try { lock (pr.Stream) pr.Stream.Write(pkt, 0, pkt.Length); } catch { }
            }
        }

        /// <summary>
        /// 対戦オプション画面から設定を変える。
        ///
        /// 全員へ流すが、従うのは 1P のクライアントだけ。1P のゲームの変数を
        /// 書き換えると、あとは 1P がふだんどおり値を配るので全員に行き渡る。
        /// 正を1つに保つため、こちらから直接みんなの値をいじることはしない。
        /// </summary>
        public void SendSetting(int which, int value)
        {
            var pkt = Proto.Build(Proto.MsgSetting, Proto.SettingPayload(which, value));
            var peers = Snapshot();
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                if (peers[i] == null) continue;
                try { lock (peers[i].Stream) peers[i].Stream.Write(pkt, 0, pkt.Length); } catch { }
            }
            Say(string.Format("設定を変えるよう 1P へ伝えました: {0} = {1}",
                Proto.MenuVarNames[which], value));
        }

        /// <summary>フレーム番号付きの入力をそのまま全員へ回す。</summary>
        private void RelayInput(byte[] payload)
        {
            var pkt = Proto.Build(Proto.MsgInput, payload);
            var peers = Snapshot();
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                var pr = peers[i];
                if (pr == null) continue;
                try { lock (pr.Stream) pr.Stream.Write(pkt, 0, pkt.Length); } catch { }
            }
        }

        /// <summary>座席表を全員へ配る。誰が何Pに座っているかを共有する。</summary>
        private void BroadcastRoster()
        {
            var names = new string[Proto.MaxPlayers + 1];
            lock (_gate)
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                    if (_slots[i] != null) names[i] = _slots[i].Name;
            var pkt = Proto.Build(Proto.MsgRoster, Proto.RosterPayload(names));
            var peers = Snapshot();
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                if (peers[i] == null) continue;
                try { lock (peers[i].Stream) peers[i].Stream.Write(pkt, 0, pkt.Length); } catch { }
            }
            Changed();
        }

        private void BroadcastLoop()
        {
            var masks = new ushort[Proto.MaxPlayers];
            double interval = 1000.0 / Math.Max(1, Hz);
            var clock = Stopwatch.StartNew();
            double next = interval;
            while (!_stop)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (now < next)
                {
                    // 残りが多いときだけ眠り、直前は短く回して待つ
                    double remain = next - now;
                    if (remain > 2) Thread.Sleep(1);
                    else Thread.SpinWait(200);
                    continue;
                }
                next += interval;
                if (next < now) next = now + interval;

                var peers = Snapshot();
                byte connected = 0;
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    masks[i - 1] = peers[i] == null ? (ushort)0 : peers[i].Mask;
                    if (peers[i] != null) connected |= (byte)(1 << (i - 1));
                }

                _frame++;

                // 誰かの試合が始まったら、そこを基準にして全員へ配る。
                // 最初に試合に入ったクライアントの対応関係を使う。
                if (!_matchBaseSet)
                {
                    // 試合が始まった直後だけを基準にする。
                    // 途中の大きなフレーム番号や、作り直し前のでたらめな値は使わない。
                    for (int i = 1; i <= Proto.MaxPlayers; i++)
                    {
                        var q = peers[i];
                        if (q == null) continue;
                        if (q.GameFrame <= 0 || q.GameFrame > 300) continue;
                        if (q.PrevGameFrame > q.GameFrame) continue;   // 巻き戻りは無視
                        _matchBase = _frame - q.GameFrame;
                        _matchBaseSet = true;
                        Say(string.Format("[INFO] 試合の基準を決めました  ゲームframe0 = 配信frame{0}  ({1}P のframe{2}から)",
                            _matchBase, i, q.GameFrame));
                        break;
                    }
                }
                else
                {
                    bool any = false;
                    for (int i = 1; i <= Proto.MaxPlayers; i++)
                        if (peers[i] != null && peers[i].GameFrame > 0) { any = true; break; }
                    // 試合が終わったら次の試合で取り直す
                    if (!any) { _matchBaseSet = false; _matchBase = int.MinValue; }
                }

                // 全員のうち一番遅れているフレーム。進みすぎた人はこれを見て待つ。
                int minFrame = int.MaxValue;
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    var q = peers[i];
                    if (q == null || q.GameFrame <= 0) continue;
                    if (q.GameFrame < minFrame) minFrame = q.GameFrame;
                }
                if (minFrame == int.MaxValue) minFrame = 0;

                // 一番進んでいる人。遅れている人はここに合わせて先まで送る。
                int maxFrame = 0;
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    var q = peers[i];
                    if (q == null || q.GameFrame <= 0) continue;
                    if (q.GameFrame > maxFrame) maxFrame = q.GameFrame;
                }

                // 絵で見せるための履歴。書き込みはここだけ、読むのは画面だけ。
                int hp = HistoryPos;
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                    History[i, hp] = masks[i - 1];
                HistoryPos = (hp + 1) % HistorySize;

                var pkt = Proto.Build(Proto.MsgFrame,
                    Proto.FramePayloadWithBase(_frame, masks, connected, minFrame, maxFrame));

                // 予定時刻からどれだけ遅れて配信したか
                double late = now - (next - interval);
                if (late < 0) late = 0;
                _lateSum += late; _lateCount++;
                if (late > _lateMax) _lateMax = late;
                if (Verbosity >= 2 && late > SendWarnMs)
                    Trace(string.Format("[WARN] 配信が {0:F1}ms 遅れました  (frame {1})", late, _frame));

                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    var pr = peers[i];
                    if (pr == null) continue;
                    double t0 = _clock.Elapsed.TotalMilliseconds;
                    try { lock (pr.Stream) pr.Stream.Write(pkt, 0, pkt.Length); }
                    catch { continue; }
                    double dt = _clock.Elapsed.TotalMilliseconds - t0;
                    if (Verbosity >= 2 && dt > SendWarnMs)
                        Trace(string.Format("[WARN] {0}P {1} へ操作パケットを送りましたが {2:F1}ms かかりました",
                            pr.Slot, pr.Name, dt));

                    // 入力が途切れていないか
                    if (Verbosity >= 2 && pr.LastInputMs > 0 && !pr.InputStalled)
                    {
                        long gap = _clock.ElapsedMilliseconds - pr.LastInputMs;
                        if (gap > InputGapWarnMs)
                        {
                            pr.InputStalled = true;
                            Trace(string.Format("[WARN] {0}P {1} からの入力が {2}ms 途切れています",
                                pr.Slot, pr.Name, gap));
                        }
                    }
                }
                TxCount++;
            }
        }
    }

    internal sealed class ServerForm : Form
    {
        private readonly RelayEngine _engine = new RelayEngine();
        private readonly TextBox _bind = new TextBox();
        private readonly TextBox _port = new TextBox();
        private readonly ComboBox _players = new ComboBox();
        private readonly Button _btn = new Button();
        private readonly ListView _list = new ListView();
        private readonly TextBox _logBox = new TextBox();
        private readonly Label _hint = new Label();
        private readonly ComboBox _level = new ComboBox();
        private readonly Label _sync = new Label();
        private readonly Label _sel = new Label();
        private readonly Button _openLog = new Button();
        private readonly Label _logPath = new Label();
        private readonly WinTimer _timer = new WinTimer();
        private readonly WinTimer _viewTimer = new WinTimer();
        private readonly InputView _view = new InputView();
        private readonly NumericUpDown[] _opt = new NumericUpDown[5];
        private readonly Label _optNote = new Label();
        private bool _optQuiet;
        private FileLogger _log;
        private readonly long[] _lastLoggedRx = new long[Proto.MaxPlayers + 1];
        private int _statTick;

        public ServerForm()
        {
            Text = "LvKSync サーバー";
            ClientSize = new Size(760, 800);
            MinimumSize = new Size(700, 660);
            Font = new Font("Yu Gothic UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            var logLab = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "  ログ",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Color.FromArgb(30, 32, 38);
            _logBox.ForeColor = Color.Gainsboro;
            _logBox.Font = new Font("Consolas", 9F);
            Controls.Add(_logBox);
            Controls.Add(logLab);

            _view.Dock = DockStyle.Top;
            _view.Height = InputView.PreferredHeight;
            _view.History = _engine.History;
            _view.HistoryPos = delegate { return _engine.HistoryPos; };
            _view.SlotNames = delegate
            {
                var ps = _engine.Snapshot();
                var ns = new string[Proto.MaxPlayers + 1];
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                    if (ps[i] != null) ns[i] = ps[i].Name;
                return ns;
            };
            _view.SlotMasks = delegate
            {
                var ps = _engine.Snapshot();
                var ms = new ushort[Proto.MaxPlayers];
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                    if (ps[i] != null) ms[i - 1] = ps[i].Mask;
                return ms;
            };
            Controls.Add(_view);
            Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = "  入力の様子  (左が古く、右が今)",
                TextAlign = ContentAlignment.MiddleLeft
            });

            Controls.Add(BuildOptions());

            _list.Dock = DockStyle.Top;
            _list.Height = 152;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.Columns.Add("プレイヤー", 80);
            _list.Columns.Add("名前", 170);
            _list.Columns.Add("アドレス", 165);
            _list.Columns.Add("入力", 80);
            _list.Columns.Add("受信/秒", 70);
            _list.Columns.Add("接続時刻", 80);
            Controls.Add(_list);

            var top = new Panel { Dock = DockStyle.Top, Height = 110 };
            top.Controls.Add(Lab("待ち受け", 10, 12));
            _bind.SetBounds(76, 8, 112, 24);
            _bind.Text = "0.0.0.0";
            top.Controls.Add(_bind);
            top.Controls.Add(Lab("ポート", 198, 12));
            _port.SetBounds(246, 8, 62, 24);
            _port.Text = "47801";
            top.Controls.Add(_port);
            top.Controls.Add(Lab("最大人数", 320, 12));
            _players.SetBounds(384, 8, 54, 24);
            _players.DropDownStyle = ComboBoxStyle.DropDownList;
            _players.Items.AddRange(new object[] { 1, 2, 3, 4 });
            _players.SelectedIndex = 3;
            top.Controls.Add(_players);
            _btn.SetBounds(452, 7, 104, 26);
            _btn.Text = "開始";
            _btn.Click += OnToggle;
            top.Controls.Add(_btn);
            _sel.SetBounds(330, 42, 360, 22);
            _sel.ForeColor = Color.FromArgb(90, 90, 90);
            _sel.Text = "キャラ選択: まだ確認していません";
            top.Controls.Add(_sel);
            _sync.SetBounds(330, 73, 360, 20);
            _sync.ForeColor = Color.FromArgb(20, 110, 60);
            _sync.Text = "同期: まだ確認していません";
            top.Controls.Add(_sync);
            _hint.SetBounds(10, 42, 670, 22);
            _hint.ForeColor = Color.FromArgb(70, 70, 70);
            _hint.Text = "参加者に伝えるアドレス: (開始すると表示されます)";
            top.Controls.Add(_hint);

            top.Controls.Add(Lab("ログの詳しさ", 10, 73));
            _level.SetBounds(92, 69, 110, 24);
            _level.DropDownStyle = ComboBoxStyle.DropDownList;
            _level.Items.AddRange(new object[] { "標準", "詳しい", "全部" });
            _level.SelectedIndex = 0;
            _level.SelectedIndexChanged += delegate { _engine.Verbosity = _level.SelectedIndex; };
            top.Controls.Add(_level);
            _openLog.SetBounds(210, 68, 104, 26);
            _openLog.Text = "ログを開く";
            _openLog.Click += OnOpenLog;
            top.Controls.Add(_openLog);
            _logPath.SetBounds(322, 73, 370, 20);
            _logPath.ForeColor = Color.FromArgb(110, 110, 110);
            _logPath.AutoEllipsis = true;
            top.Controls.Add(_logPath);
            Controls.Add(top);

            _log = new FileLogger("LvKSyncServer");
            _logPath.Text = _log.Path == null ? "(ログを作れませんでした)" : _log.Path;
            _log.Write("画面を開きました");

            _engine.Log += delegate (string s) { Post(delegate { AppendLog(s); }); };
            _engine.RosterChanged += delegate { Post(RefreshList); };

            _timer.Interval = 500;
            _timer.Tick += delegate { RefreshList(); RefreshOptions(); RefreshSel(); };
            _timer.Start();

            // 入力の絵はなめらかに動かしたいので、こちらは細かく描き直す
            _viewTimer.Interval = 50;
            _viewTimer.Tick += delegate { if (_engine.Running) _view.Invalidate(); };
            _viewTimer.Start();

            RefreshList();
        }

        /// <summary>
        /// 対戦オプション。ゲームの中の設定メニュー (キャラ選択で Shift) と
        /// 同じものを、こちらからも変えられるようにしたもの。
        /// ここで変えると 1P のゲームの変数が書き換わり、そこから全員へ配られる。
        /// </summary>
        private Panel BuildOptions()
        {
            var pan = new Panel { Dock = DockStyle.Top, Height = 74 };
            pan.Controls.Add(new Label
            {
                Text = "  対戦オプション  (ゲーム内の Shift メニューと同じもの)",
                AutoSize = true, Left = 4, Top = 4,
                ForeColor = Color.FromArgb(70, 70, 70)
            });

            // 名前と、取りうる範囲。実測した値の意味に合わせてある。
            var names = new string[] { "プレイヤー", "ライフ", "カブ", "チーム", "ステージ" };
            var mins = new int[] { 1, 1, 0, 0, 0 };
            var maxs = new int[] { 4, 5, 1, 4, 22 };
            int x = 8;
            for (int i = 0; i < _opt.Length; i++)
            {
                pan.Controls.Add(new Label
                { Text = names[i], AutoSize = true, Left = x, Top = 30 });
                var nud = new NumericUpDown();
                nud.SetBounds(x, 46, 62, 24);
                nud.Minimum = mins[i];
                nud.Maximum = maxs[i];
                int which = i + 1;             // Proto.MenuVars の 1..5
                nud.ValueChanged += delegate
                {
                    if (_optQuiet || !_engine.Running) return;
                    _engine.SendSetting(which, (int)((NumericUpDown)_opt[which - 1]).Value);
                };
                _opt[i] = nud;
                pan.Controls.Add(nud);
                x += Math.Max(76, TextRenderer.MeasureText(names[i], Font).Width + 14);
            }
            _optNote.SetBounds(x + 8, 46, 320, 22);
            _optNote.ForeColor = Color.FromArgb(110, 110, 110);
            _optNote.Text = "1P がつながると使えます";
            pan.Controls.Add(_optNote);
            return pan;
        }

        /// <summary>1P から届いた値を画面に映す。編集中の欄は触らない。</summary>
        private void RefreshOptions()
        {
            bool have = _engine.Running && _engine.HaveMenu;
            var v = _engine.MenuValues;
            _optQuiet = true;
            try
            {
                for (int i = 0; i < _opt.Length; i++)
                {
                    _opt[i].Enabled = have;
                    // NumericUpDown 自身は Focused が false のまま。中の入力欄が
                    // フォーカスを持つので ContainsFocus で見る。ここを間違えて、
                    // 変えた値を 0.5 秒ごとに書き戻していた。
                    if (!have || _opt[i].ContainsFocus) continue;
                    int val = v[i + 1];
                    if (val < _opt[i].Minimum) val = (int)_opt[i].Minimum;
                    if (val > _opt[i].Maximum) val = (int)_opt[i].Maximum;
                    if ((int)_opt[i].Value != val) _opt[i].Value = val;
                }
            }
            finally { _optQuiet = false; }
            _optNote.Text = !_engine.Running ? "サーバーを開始すると使えます"
                : !have ? "1P がつながると使えます"
                : _engine.MenuOpen ? "1P がゲーム内のメニューを開いています"
                : "変えると 1P のゲームに反映され、全員に配られます";
        }

        /// <summary>キャラ選択が揃っているかを出す。</summary>
        private void RefreshSel()
        {
            int st = _engine.Running ? _engine.SelState : Proto.SelUnknown;
            _sel.Text = "キャラ選択: " + Proto.SelStateText(st, _engine.SelPeers);
            _sel.ForeColor =
                st == Proto.SelSame ? Color.FromArgb(20, 110, 60) :
                st == Proto.SelDiffer ? Color.FromArgb(180, 40, 40) :
                Color.FromArgb(90, 90, 90);
        }

        private void Post(Action a)
        {
            if (IsHandleCreated) { try { BeginInvoke(a); } catch { } }
        }

        private static Label Lab(string t, int x, int y)
        {
            return new Label { Text = t, AutoSize = true, Left = x, Top = y };
        }

        private void AppendLog(string s)
        {
            if (_logBox.TextLength > 60000) _logBox.Clear();
            _logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            if (_log != null) _log.Write(s);
        }

        private void OnOpenLog(object sender, EventArgs e)
        {
            if (_log == null || _log.Path == null) return;
            try { Process.Start("notepad.exe", _log.Path); }
            catch
            {
                try { Process.Start("explorer.exe", "/select,\"" + _log.Path + "\""); }
                catch { }
            }
        }

        /// <summary>毎秒、誰がどのボタンを押しているかと受信数をログに残す。</summary>
        private void LogStats(Peer[] peers)
        {
            if (_log == null || _level.SelectedIndex < 2 || !_engine.Running) return;
            if (++_statTick < 2) return;      // 0.5秒刻みなので2回に1回 = 毎秒
            _statTick = 0;
            var sb = new StringBuilder();
            bool any = false;
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                var p = peers[i];
                if (p == null) continue;
                any = true;
                sb.AppendFormat("{0}P[{1}] {2} 受信{3}  ", i, p.Name, MaskText(p.Mask),
                    p.RxCount - _lastLoggedRx[i]);
                _lastLoggedRx[i] = p.RxCount;
            }
            var late = _engine.TakeLateSummary();
            if (late != null) sb.Append(late);
            if (any || late != null) _log.Write("[INFO] " + sb.ToString());
        }

        private void OnToggle(object sender, EventArgs e)
        {
            if (_engine.Running)
            {
                _engine.Stop();
                _btn.Text = "開始";
                SetInputs(true);
                _hint.Text = "参加者に伝えるアドレス: (開始すると表示されます)";
                return;
            }
            IPAddress addr;
            if (!IPAddress.TryParse(_bind.Text.Trim(), out addr)) addr = IPAddress.Any;
            int port;
            if (!int.TryParse(_port.Text.Trim(), out port)) port = 47801;
            _engine.MaxPlayers = (int)_players.SelectedItem;
            try { _engine.Start(addr, port); }
            catch (Exception ex)
            {
                MessageBox.Show("待ち受けに失敗しました。\n\n" + ex.Message +
                    "\n\nポートが他のソフトに使われていないか確認してください。",
                    "LvKSync サーバー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _btn.Text = "停止";
            SetInputs(false);
            _hint.Text = "参加者に伝えるアドレス:  " + LocalAddresses() + "    ポート " + port;
        }

        private void SetInputs(bool on)
        {
            _bind.Enabled = on; _port.Enabled = on; _players.Enabled = on;
        }

        private static string LocalAddresses()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (sb.Length > 0) sb.Append("  /  ");
                    sb.Append(ip);
                }
            }
            catch { }
            return sb.Length == 0 ? "(取得できません)" : sb.ToString();
        }

        private static string MaskText(ushort m)
        {
            const string names = "LUDRAB";
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = ((m >> i) & 1) != 0 ? names[i] : '.';
            return new string(c);
        }

        private void RefreshList()
        {
            var peers = _engine.Snapshot();
            _list.BeginUpdate();
            while (_list.Items.Count < Proto.MaxPlayers)
                _list.Items.Add(new ListViewItem(new string[] { "", "", "", "", "", "" }));
            for (int i = 1; i <= Proto.MaxPlayers; i++)
            {
                var it = _list.Items[i - 1];
                it.SubItems[0].Text = i + "P";
                var p = peers[i];
                if (p == null)
                {
                    it.SubItems[1].Text = i <= _engine.MaxPlayers ? "空き" : "―";
                    for (int k = 2; k <= 5; k++) it.SubItems[k].Text = "";
                    it.ForeColor = Color.Gray;
                }
                else
                {
                    long d = p.RxCount - p.RxAtLastSample;
                    p.RxAtLastSample = p.RxCount;
                    p.RxPerSec = (int)(d * 1000 / Math.Max(1, _timer.Interval));
                    it.SubItems[1].Text = p.Name;
                    it.SubItems[2].Text = p.Remote;
                    it.SubItems[3].Text = MaskText(p.Mask);
                    it.SubItems[4].Text = p.RxPerSec.ToString();
                    it.SubItems[5].Text = p.JoinedAt.ToString("HH:mm:ss");
                    it.ForeColor = Color.Black;
                }
            }
            _list.EndUpdate();

            if (!_engine.Running) _sync.Text = "";
            else if (_engine.DesyncFrame > 0)
            {
                _sync.ForeColor = Color.FromArgb(180, 40, 40);
                _sync.Text = string.Format("同期: frame {0} でずれました ({1}回)",
                    _engine.DesyncFrame, _engine.DesyncCount);
            }
            else if (_engine.CheckedFrames > 0)
            {
                _sync.ForeColor = Color.FromArgb(20, 110, 60);
                _sync.Text = string.Format("同期: 一致 ({0}回 確認)", _engine.CheckedFrames);
            }
            else _sync.Text = "同期: まだ確認していません";

            LogStats(peers);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            _viewTimer.Stop();
            if (_engine.Running) _engine.Stop();
            if (_log != null) { _log.Dispose(); _log = null; }
            base.OnFormClosing(e);
        }

        /// <summary>--start が付いていれば開いた時点で待ち受けを始める。</summary>
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
                    case "--verbose": _level.SelectedIndex = 1; break;
                    case "--log-all": _level.SelectedIndex = 2; break;
                    case "--bind": if (nx != null) { _bind.Text = nx; i++; } break;
                    case "--port": if (nx != null) { _port.Text = nx; i++; } break;
                    case "--players":
                        if (nx != null)
                        {
                            int n;
                            if (int.TryParse(nx, out n) && n >= 1 && n <= Proto.MaxPlayers)
                                _players.SelectedIndex = n - 1;
                            i++;
                        }
                        break;
                }
            }
            if (auto) Shown += delegate { OnToggle(this, EventArgs.Empty); };
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new ServerForm();
            f.ApplyArgs(args);
            Application.Run(f);
        }
    }
}
