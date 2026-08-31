// LvKSyncServerGui - 中継サーバーの画面つき版
//
// 誰が何Pに座っているかを一覧で確認できる。ゲームには一切触らない。

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
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
        public int RxPerSec;
        public DateTime JoinedAt = DateTime.Now;
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

        public event Action<string> Log;
        public event Action RosterChanged;

        private void Say(string s)
        {
            var h = Log;
            if (h != null) h(s);
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
            Say(string.Format("待ち受け開始  {0}:{1}   最大 {2} 人", bind, port, MaxPlayers));
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
            Say("停止しました");
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
                Say(string.Format("満員のため {0} ({1}) を拒否しました", pname, remote));
                return;
            }

            try
            {
                var w = Proto.Build(Proto.MsgWelcome, new byte[] { (byte)assigned, (byte)MaxPlayers });
                lock (st) st.Write(w, 0, w.Length);
            }
            catch { }
            Say(string.Format("P{0} に {1} が参加  ({2})", assigned, pname, remote));
            BroadcastRoster();

            while (!_stop)
            {
                if (!Proto.Read(st, out type, out payload)) break;
                if (type == Proto.MsgInput && payload.Length >= 7)
                {
                    peer.Mask = BitConverter.ToUInt16(payload, 5);
                    peer.RxCount++;
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
            Say(string.Format("P{0} の {1} が退出しました", peer.Slot, peer.Name));
            BroadcastRoster();
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
                if (now < next) { Thread.Sleep(1); continue; }
                next += interval;
                if (next < now) next = now + interval;

                var peers = Snapshot();
                byte connected = 0;
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    masks[i - 1] = peers[i] == null ? (ushort)0 : peers[i].Mask;
                    if (peers[i] != null) connected |= (byte)(1 << (i - 1));
                }

                var pkt = Proto.Build(Proto.MsgFrame, Proto.FramePayload(0, masks, connected));
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    if (peers[i] == null) continue;
                    try { lock (peers[i].Stream) peers[i].Stream.Write(pkt, 0, pkt.Length); } catch { }
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
        private readonly TextBox _log = new TextBox();
        private readonly Label _hint = new Label();
        private readonly WinTimer _timer = new WinTimer();

        public ServerForm()
        {
            Text = "LvKSync サーバー";
            ClientSize = new Size(700, 520);
            MinimumSize = new Size(600, 440);
            Font = new Font("Yu Gothic UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            var logLab = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "  ログ",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.FromArgb(30, 32, 38);
            _log.ForeColor = Color.Gainsboro;
            _log.Font = new Font("Consolas", 9F);
            Controls.Add(_log);
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

            var top = new Panel { Dock = DockStyle.Top, Height = 78 };
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
            _hint.SetBounds(10, 44, 670, 30);
            _hint.ForeColor = Color.FromArgb(70, 70, 70);
            _hint.Text = "参加者に伝えるアドレス: (開始すると表示されます)";
            top.Controls.Add(_hint);
            Controls.Add(top);

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
            if (_log.TextLength > 60000) _log.Clear();
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
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
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            if (_engine.Running) _engine.Stop();
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
