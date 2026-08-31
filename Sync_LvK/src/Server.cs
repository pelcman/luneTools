// LvKSyncServer - キー入力を仲介する中継サーバー
//
// 最大4人のクライアントを受け付け、各クライアントから届いた自分のスロットの
// 入力を、全クライアントへブロードキャストする。ゲームには一切触らない。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LvKSync
{
    internal static class ServerProgram
    {
        private sealed class Peer
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public int Slot;              // 1..4
            public ushort Mask;
            public int Frame;
            public long LastSeenMs;
            public string Remote;
            public string Name;
            public long RxCount;
        }

        private static readonly object Gate = new object();
        private static readonly Peer[] Slots = new Peer[Proto.MaxPlayers + 1]; // 1-origin
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static volatile bool _stop;
        private static long _txCount;
        private static int _tickHz = 60;
        private static bool _verbose;

        private static void Usage()
        {
            Console.WriteLine();
            Console.WriteLine("LvKSyncServer - キー入力中継サーバー (最大4人)");
            Console.WriteLine();
            Console.WriteLine("  LvKSyncServer.exe [オプション]");
            Console.WriteLine();
            Console.WriteLine("  --bind <ip>      待ち受けアドレス (既定 0.0.0.0)");
            Console.WriteLine("  --port <n>       ポート (既定 47801)");
            Console.WriteLine("  --hz <n>         ブロードキャスト周期 (既定 60)");
            Console.WriteLine("  --players <n>    受け付ける最大人数 1-4 (既定 4)");
            Console.WriteLine("  --config <path>  設定ファイル (既定 LvKSyncServer.ini)");
            Console.WriteLine("  --verbose        詳細ログ");
            Console.WriteLine("  --help           このヘルプ");
            Console.WriteLine();
            Console.WriteLine("  同一PCで試すときは --bind 127.0.0.1 にするとファイアウォール警告が出ません。");
            Console.WriteLine();
        }

        private static void WriteDefaultIni(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# LvKSyncServer 設定ファイル");
            sb.AppendLine("# コマンドライン引数のほうが優先されます。");
            sb.AppendLine();
            sb.AppendLine("[network]");
            sb.AppendLine("# 同一PC内だけで試すなら 127.0.0.1 にするとFW警告が出ません。");
            sb.AppendLine("bind = 0.0.0.0");
            sb.AppendLine("port = 47801");
            sb.AppendLine();
            sb.AppendLine("[relay]");
            sb.AppendLine("# ブロードキャスト周期(Hz)。ゲームは60fpsなので通常60。");
            sb.AppendLine("hz = 60");
            sb.AppendLine("# 受け付ける最大人数 (1-4)");
            sb.AppendLine("players = 4");
            sb.AppendLine();
            sb.AppendLine("[misc]");
            sb.AppendLine("verbose = false");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string cfgPath = Path.Combine(exeDir, "LvKSyncServer.ini");
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "--config" && i + 1 < argv.Length) cfgPath = argv[i + 1];
            if (!File.Exists(cfgPath)) { WriteDefaultIni(cfgPath); Console.WriteLine("設定ファイルを作成しました: " + cfgPath); }

            var ini = Ini.Load(cfgPath);
            string bind = ini.Get("bind", "0.0.0.0");
            int port = ini.GetInt("port", 47801);
            _tickHz = ini.GetInt("hz", 60);
            int maxPlayers = ini.GetInt("players", Proto.MaxPlayers);
            _verbose = ini.GetBool("verbose", false);

            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string nx = (i + 1 < argv.Length) ? argv[i + 1] : null;
                switch (a)
                {
                    case "--help": case "-h": case "/?": Usage(); return 0;
                    case "--bind": if (nx != null) { bind = nx; i++; } break;
                    case "--port": if (nx != null) { int.TryParse(nx, out port); i++; } break;
                    case "--hz": if (nx != null) { int.TryParse(nx, out _tickHz); i++; } break;
                    case "--players": if (nx != null) { int.TryParse(nx, out maxPlayers); i++; } break;
                    case "--verbose": _verbose = true; break;
                    case "--config": i++; break;
                }
            }
            if (maxPlayers < 1) maxPlayers = 1;
            if (maxPlayers > Proto.MaxPlayers) maxPlayers = Proto.MaxPlayers;
            if (_tickHz < 1) _tickHz = 60;

            IPAddress addr;
            if (!IPAddress.TryParse(bind, out addr)) addr = IPAddress.Any;

            TcpListener listener;
            try
            {
                listener = new TcpListener(addr, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("待ち受けに失敗しました: " + ex.Message);
                return 1;
            }

            Console.WriteLine("=== LvKSyncServer ===");
            Console.WriteLine("  待ち受け : {0}:{1}", addr, port);
            Console.WriteLine("  最大人数 : {0}", maxPlayers);
            Console.WriteLine("  中継周期 : {0} Hz", _tickHz);
            Console.WriteLine("  設定     : {0}", cfgPath);
            Console.WriteLine();
            Console.WriteLine("Ctrl+C で終了します。");
            Console.WriteLine();

            Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { _stop = true; e.Cancel = true; };

            var accept = new Thread(delegate () { AcceptLoop(listener, maxPlayers); });
            accept.IsBackground = true;
            accept.Start();

            BroadcastLoop();

            _stop = true;
            try { listener.Stop(); } catch { }
            lock (Gate)
            {
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                    if (Slots[i] != null) { try { Slots[i].Tcp.Close(); } catch { } }
            }
            Console.WriteLine();
            Console.WriteLine("終了しました。ブロードキャスト {0} 回。", _txCount);
            return 0;
        }

        private static void AcceptLoop(TcpListener listener, int maxPlayers)
        {
            while (!_stop)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch { return; }
                var t = new Thread(delegate () { HandlePeer(tcp, maxPlayers); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private static void HandlePeer(TcpClient tcp, int maxPlayers)
        {
            tcp.NoDelay = true;
            NetworkStream st;
            string remote = "?";
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
            var peer = new Peer();
            peer.Tcp = tcp; peer.Stream = st; peer.Remote = remote; peer.Name = pname;
            peer.LastSeenMs = Clock.ElapsedMilliseconds;

            int assigned = 0;
            lock (Gate)
            {
                if (want >= 1 && want <= maxPlayers && Slots[want] == null) assigned = want;
                else
                    for (int i = 1; i <= maxPlayers && assigned == 0; i++)
                        if (Slots[i] == null) assigned = i;
                if (assigned != 0) { peer.Slot = assigned; Slots[assigned] = peer; }
            }

            if (assigned == 0)
            {
                try { st.Write(Proto.Build(Proto.MsgFull, null), 0, 6); } catch { }
                try { tcp.Close(); } catch { }
                Console.WriteLine("[満員] {0} を拒否しました", remote);
                return;
            }

            try { st.Write(Proto.Build(Proto.MsgWelcome, new byte[] { (byte)assigned, (byte)maxPlayers }), 0, 8); }
            catch { }
            Console.WriteLine("[接続] P{0}  {1}  <- {2}", assigned, pname, remote);
            BroadcastRoster();

            while (!_stop)
            {
                if (!Proto.Read(st, out type, out payload)) break;
                peer.LastSeenMs = Clock.ElapsedMilliseconds;
                if (type == Proto.MsgInput && payload.Length >= 7)
                {
                    peer.Frame = BitConverter.ToInt32(payload, 1);
                    peer.Mask = BitConverter.ToUInt16(payload, 5);
                    peer.RxCount++;
                }
                else if (type == Proto.MsgPing && payload.Length >= 8)
                {
                    var pong = Proto.Build(Proto.MsgPong, payload);
                    try { st.Write(pong, 0, pong.Length); } catch { break; }
                }
                else if (type == Proto.MsgBye) break;
            }

            lock (Gate) { if (Slots[peer.Slot] == peer) Slots[peer.Slot] = null; }
            try { tcp.Close(); } catch { }
            Console.WriteLine("[切断] P{0}  {1}  ({2})", peer.Slot, peer.Name, remote);
            BroadcastRoster();
        }

        /// <summary>座席表を全員へ配る。誰が何Pに座っているかを共有する。</summary>
        private static void BroadcastRoster()
        {
            var names = new string[Proto.MaxPlayers + 1];
            lock (Gate)
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                    if (Slots[i] != null) names[i] = Slots[i].Name;
            var pkt = Proto.Build(Proto.MsgRoster, Proto.RosterPayload(names));
            lock (Gate)
                for (int i = 1; i <= Proto.MaxPlayers; i++)
                {
                    if (Slots[i] == null) continue;
                    try { Slots[i].Stream.Write(pkt, 0, pkt.Length); } catch { }
                }
        }

        private static void BroadcastLoop()
        {
            var masks = new ushort[Proto.MaxPlayers];
            double interval = 1000.0 / _tickHz;
            double next = Clock.ElapsedMilliseconds + interval;
            long statusNext = 1000;
            long lastTx = 0;
            var lastRx = new long[Proto.MaxPlayers + 1];

            while (!_stop)
            {
                long now = Clock.ElapsedMilliseconds;
                if (now < next) { Thread.Sleep(1); continue; }
                next += interval;
                if (next < now) next = now + interval;   // 遅れたら追いつく

                byte connected = 0;
                int frame = 0;
                lock (Gate)
                {
                    for (int i = 1; i <= Proto.MaxPlayers; i++)
                    {
                        var p = Slots[i];
                        if (p == null) { masks[i - 1] = 0; continue; }
                        masks[i - 1] = p.Mask;
                        connected |= (byte)(1 << (i - 1));
                        if (p.Frame > frame) frame = p.Frame;
                    }
                }

                var pkt = Proto.Build(Proto.MsgFrame, Proto.FramePayload(frame, masks, connected));
                lock (Gate)
                {
                    for (int i = 1; i <= Proto.MaxPlayers; i++)
                    {
                        var p = Slots[i];
                        if (p == null) continue;
                        try { p.Stream.Write(pkt, 0, pkt.Length); }
                        catch { }
                    }
                }
                _txCount++;

                if (now >= statusNext)
                {
                    var sb = new StringBuilder();
                    sb.AppendFormat("frame {0,-7} tx {1,4}/s  ", frame, _txCount - lastTx);
                    lock (Gate)
                    {
                        for (int i = 1; i <= Proto.MaxPlayers; i++)
                        {
                            var p = Slots[i];
                            if (p == null) { sb.AppendFormat("P{0}:--       ", i); continue; }
                            sb.AppendFormat("P{0}[{1}]:{2} {3,3}/s  ", i, p.Name, MaskText(p.Mask), p.RxCount - lastRx[i]);
                            lastRx[i] = p.RxCount;
                        }
                    }
                    Console.WriteLine(sb.ToString());
                    lastTx = _txCount;
                    statusNext = now + 1000;
                }
            }
        }

        /// <summary>ボタン状態を見やすい6文字にする。</summary>
        private static string MaskText(ushort m)
        {
            const string names = "LUDRAB";
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = ((m >> i) & 1) != 0 ? names[i] : '.';
            return new string(c);
        }
    }
}
