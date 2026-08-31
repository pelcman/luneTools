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
                    peer.GameFrame = BitConverter.ToInt32(payload, 1);
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
                else if (type == Proto.MsgPing && payload.Length >= 8)
                {
                    var pong = Proto.Build(Proto.MsgPong, payload);
                    try { lock (st) st.Write(pong, 0, pong.Length); } catch { break; }
                }
                else if (type == Proto.MsgBye) break;
            }

            lock (_gate) { if (_slots[peer.Slot] == peer) _slots[peer.Slot] = null; }
            try { tcp.Close(); } catch { }
            Say(string.Format("[INFO] {0}P の {1} が退出しました", peer.Slot, peer.Name));
            BroadcastRoster();
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

                var pkt = Proto.Build(Proto.MsgFrame,
                    Proto.FramePayloadWithBase(_frame, masks, connected, minFrame));

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
        private readonly Button _openLog = new Button();
        private readonly Label _logPath = new Label();
        private readonly WinTimer _timer = new WinTimer();
        private FileLogger _log;
        private readonly long[] _lastLoggedRx = new long[Proto.MaxPlayers + 1];
        private int _statTick;

        public ServerForm()
        {
            Text = "LvKSync サーバー";
            ClientSize = new Size(700, 560);
            MinimumSize = new Size(620, 470);
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
            _timer.Tick += delegate { RefreshList(); };
            _timer.Start();

            RefreshList();
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
                    it.SubItems[2].Text = "";
                    it.SubItems[3].Text = "";
                    it.SubItems[4].Text = "";
                    it.SubItems[5].Text = "";
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
            LogStats(peers);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
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
