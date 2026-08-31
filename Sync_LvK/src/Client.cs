// LvKSyncClient - ゲームと同じPCで動かす送受信クライアント
//
// ローカルプレイヤーの入力をサーバーへ送り、他プレイヤーの入力を受け取って
// ゲームのネットワーク入力ブロックへ書き込む。
//
// ゲーム側との取り決め:
//   V[netbase + (slot-1)*6 + 0..5] = 上, 下, 左, 右, A, B   (各 0 か 1)
//   リモートのプレイヤー N については、ゲーム側で 3014 の代わりに
//   V[321..326] へこの6個をコピーする。変数配列の操作(03013)なら1コマンドで済む。

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LvKSync
{
    internal static class ClientProgram
    {
        private const int Buttons = Proto.ButtonsPerPlayer;

        private static volatile bool _stop;
        private static volatile ushort[] _remoteMasks = new ushort[Proto.MaxPlayers];
        private static volatile int _connectedBits;
        private static long _rxCount, _txCount;
        private static int _mySlot;
        private static volatile int _rttMs = -1;
        private static int _mySlotWanted;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        private static void Usage()
        {
            Console.WriteLine();
            Console.WriteLine("LvKSyncClient - ゲームと同じPCで動かす入力同期クライアント");
            Console.WriteLine();
            Console.WriteLine("  LvKSyncClient.exe [オプション]");
            Console.WriteLine();
            Console.WriteLine("  --host <ip>        サーバーのIP (既定 127.0.0.1)");
            Console.WriteLine("  --port <n>         ポート (既定 47801)");
            Console.WriteLine("  --slot <1-4>       希望するプレイヤー番号 (0 = おまかせ)");
            Console.WriteLine("  --netbase <n>      ネットワーク入力ブロックの先頭変数 (既定 9001)");
            Console.WriteLine("  --source <s>       ローカル入力の取得元  keys | netvar  (既定 keys)");
            Console.WriteLine("  --local-keys <s>   source=keys のときの割り当て 上,下,左,右,A,B");
            Console.WriteLine("                     (既定 W,S,A,D,F,G)");
            Console.WriteLine("  --pid <n>          対象プロセスID (省略時は自動検出)");
            Console.WriteLine("  --index <n>        RPG_RT が複数あるとき何番目か (既定 0)");
            Console.WriteLine("  --delay <n>        入力遅延フレーム数 (既定 2)。回線が悪いほど大きく");
            Console.WriteLine("  --no-write         受信しても書き込まない (動作確認用)");
            Console.WriteLine("  --no-ptr           ポインタ方式を使わず走査で探す (保険)");
            Console.WriteLine("  --varbase <hex>    変数配列の位置を手動指定する");
            Console.WriteLine("  --apply-own        自分のスロットにも書き込む (通常は不要)");
            Console.WriteLine("  --config <path>    設定ファイル (既定 LvKSyncClient.ini)");
            Console.WriteLine("  --list             起動中の RPG_RT を一覧表示して終了");
            Console.WriteLine("  --help             このヘルプ");
            Console.WriteLine();
            Console.WriteLine("  例) 同一PCで2人ぶん動かす:");
            Console.WriteLine("      LvKSyncClient.exe --slot 1 --index 0");
            Console.WriteLine("      LvKSyncClient.exe --slot 2 --index 1 --local-keys Up,Down,Left,Right,K,L");
            Console.WriteLine();
        }

        private static void WriteDefaultIni(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# LvKSyncClient 設定ファイル");
            sb.AppendLine("# コマンドライン引数のほうが優先されます。");
            sb.AppendLine();
            sb.AppendLine("[network]");
            sb.AppendLine("host = 127.0.0.1");
            sb.AppendLine("port = 47801");
            sb.AppendLine("# 希望するプレイヤー番号。0 ならサーバーにおまかせ。");
            sb.AppendLine("slot = 0");
            sb.AppendLine();
            sb.AppendLine("[game]");
            sb.AppendLine("# ネットワーク入力ブロックの先頭変数番号。");
            sb.AppendLine("# V[netbase + (slot-1)*6 + 0..5] = 上,下,左,右,A,B");
            sb.AppendLine("# プロジェクトで未使用の連続24変数を割り当ててください。");
            sb.AppendLine("netbase = 9001");
            sb.AppendLine();
            sb.AppendLine("# ローカル入力の取得元");
            sb.AppendLine("#   keys   … OSのキーボード状態を直接読む (ゲーム側の改造なしで動く)");
            sb.AppendLine("#   netvar … ゲームが自分のスロットの入力を netbase へ書く前提で読む");
            sb.AppendLine("source = keys");
            sb.AppendLine("localkeys = W,S,A,D,F,G");
            sb.AppendLine();
            sb.AppendLine("# 0 なら RPG_RT.exe を自動検出。複数あるときは index で選ぶ。");
            sb.AppendLine("pid = 0");
            sb.AppendLine("index = 0");
            sb.AppendLine();
            sb.AppendLine("# 入力遅延フレーム数。相手の入力を何フレーム遅らせて適用するか。");
            sb.AppendLine("# 往復時間(RTT)の半分をフレームに直した値が目安。");
            sb.AppendLine("#   LAN      0〜2 / 国内 3〜5 / 遠距離 6以上");
            sb.AppendLine("delay = 2");
            sb.AppendLine();
            sb.AppendLine("[misc]");
            sb.AppendLine("nowrite = false");
            sb.AppendLine("applyown = false");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string cfgPath = Path.Combine(exeDir, "LvKSyncClient.ini");
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "--config" && i + 1 < argv.Length) cfgPath = argv[i + 1];
            if (!File.Exists(cfgPath)) { WriteDefaultIni(cfgPath); Console.WriteLine("設定ファイルを作成しました: " + cfgPath); }

            var ini = Ini.Load(cfgPath);
            string host = ini.Get("host", "127.0.0.1");
            int port = ini.GetInt("port", 47801);
            int slot = ini.GetInt("slot", 0);
            int netbase = ini.GetInt("netbase", 9001);
            string source = ini.Get("source", "keys").ToLowerInvariant();
            string localKeys = ini.Get("localkeys", "W,S,A,D,F,G");
            int pid = ini.GetInt("pid", 0);
            int index = ini.GetInt("index", 0);
            bool noWrite = ini.GetBool("nowrite", false);
            bool applyOwn = ini.GetBool("applyown", false);
            int delayFrames = ini.GetInt("delay", 2);
            bool noPtr = ini.GetBool("noptr", false);
            long varbaseOverride = 0;
            string vbs = ini.Get("varbase", null);
            if (!string.IsNullOrEmpty(vbs)) { try { varbaseOverride = Convert.ToInt64(vbs, 16); } catch { } }
            bool listOnly = false;

            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string nx = (i + 1 < argv.Length) ? argv[i + 1] : null;
                switch (a)
                {
                    case "--help": case "-h": case "/?": Usage(); return 0;
                    case "--list": listOnly = true; break;
                    case "--host": if (nx != null) { host = nx; i++; } break;
                    case "--port": if (nx != null) { int.TryParse(nx, out port); i++; } break;
                    case "--slot": if (nx != null) { int.TryParse(nx, out slot); i++; } break;
                    case "--netbase": if (nx != null) { int.TryParse(nx, out netbase); i++; } break;
                    case "--source": if (nx != null) { source = nx.ToLowerInvariant(); i++; } break;
                    case "--local-keys": if (nx != null) { localKeys = nx; i++; } break;
                    case "--pid": if (nx != null) { int.TryParse(nx, out pid); i++; } break;
                    case "--index": if (nx != null) { int.TryParse(nx, out index); i++; } break;
                    case "--no-write": noWrite = true; break;
                    case "--delay": if (nx != null) { int.TryParse(nx, out delayFrames); i++; } break;
                    case "--no-ptr": noPtr = true; break;
                    case "--varbase": if (nx != null) { try { varbaseOverride = Convert.ToInt64(nx, 16); } catch { } i++; } break;
                    case "--apply-own": applyOwn = true; break;
                    case "--config": i++; break;
                }
            }

            _mySlotWanted = slot;
            var games = Util.FindGames();
            if (listOnly)
            {
                Console.WriteLine("起動中の RPG_RT: {0} 個", games.Count);
                for (int i = 0; i < games.Count; i++)
                {
                    string t = "";
                    try { t = games[i].MainWindowTitle; } catch { }
                    Console.WriteLine("  index={0}  pid={1}  {2}", i, games[i].Id, t);
                }
                return 0;
            }

            if (delayFrames < 0) delayFrames = 0;
            if (delayFrames > 30) delayFrames = 30;
            int[] keys = Util.ParseKeys(localKeys);
            if (source == "keys" && keys.Length != Buttons)
            {
                Console.WriteLine("localkeys は6個指定してください (上,下,左,右,A,B)。現在 {0} 個。", keys.Length);
                return 2;
            }

            Console.WriteLine("=== LvKSyncClient ===");
            Console.WriteLine("  サーバー   : {0}:{1}", host, port);
            Console.WriteLine("  希望スロット: {0}", slot == 0 ? "おまかせ" : slot.ToString());
            Console.WriteLine("  入力ブロック: V[{0}..{1}]  (1人6変数 x 4人)", netbase, netbase + Proto.MaxPlayers * Buttons - 1);
            Console.WriteLine("  ローカル入力: {0}{1}", source, source == "keys" ? " (" + localKeys + ")" : "");
            Console.WriteLine("  入力遅延   : {0} フレーム ({1:F1} ms 相当)", delayFrames, delayFrames * 1000.0 / 60.0);
            Console.WriteLine("  設定       : {0}", cfgPath);
            if (noWrite) Console.WriteLine("  ** --no-write: 受信しても書き込みません **");
            Console.WriteLine();

            // --- ゲームプロセス ---
            if (pid == 0)
            {
                if (index > 0)
                    Console.WriteLine("このPCの {0} 番目のゲームを待っています (--index {1})", index + 1, index);
                else
                    Console.Write("ゲームの起動を待っています");
                bool warned = false;
                for (int waited = 0; !_stop; waited++)
                {
                    games = Util.FindGames();
                    if (games.Count > index) { pid = games[index].Id; break; }
                    if (index > 0 && games.Count > 0 && !warned && waited > 6)
                    {
                        warned = true;
                        Console.WriteLine();
                        Console.WriteLine("  ゲームは {0} つ動いていますが、{1} 番目を指定しています。",
                            games.Count, index + 1);
                        Console.WriteLine("  1台でゲームを1つだけ動かす場合は --index 0 にしてください。");
                        Console.WriteLine("  ({0}P で遊ぶことと、このPCの何番目のゲームかは別の話です)", _mySlotWanted);
                        Console.Write("  待機中");
                    }
                    Console.Write(".");
                    Thread.Sleep(700);
                }
                Console.WriteLine();
            }
            Console.WriteLine("対象プロセス: pid={0} (index={1})", pid, index);

            GameMemory mem;
            try { mem = new GameMemory(pid); }
            catch (Exception ex) { Console.WriteLine(ex.Message); return 3; }

            int needIndex = netbase + Proto.MaxPlayers * Buttons - 1;
            if (needIndex < VarBaseFinder.TickVar) needIndex = VarBaseFinder.TickVar;
            long vb = 0; string method = null;
            if (varbaseOverride != 0) { vb = varbaseOverride; method = "手動指定"; }
            else
            {
                Console.WriteLine("ゲームのデータを探しています…");
                for (int tries = 0; vb == 0 && !_stop; tries++)
                {
                    vb = noPtr ? VarBaseFinder.FindBySignature(mem, out method)
                               : VarBaseFinder.Find(mem, needIndex, out method);
                    if (vb == 0)
                    {
                        if (tries == 3) Console.WriteLine("  見つかりません。ゲームを対戦画面まで進めてみてください。");
                        Console.Write("."); Thread.Sleep(600);
                    }
                }
                Console.WriteLine();
            }
            Console.WriteLine();
            mem.VarBase = vb;
            Console.WriteLine("変数配列ベース: 0x{0:X}  ({1})", vb, method);
            Console.WriteLine();

            // --- 接続 ---
            TcpClient tcp = null;
            Console.Write("サーバーへ接続しています {0}:{1} ", host, port);
            for (int i = 0; i < 120 && tcp == null && !_stop; i++)
            {
                try { var c = new TcpClient(); c.Connect(host, port); tcp = c; }
                catch { Console.Write("."); Thread.Sleep(500); }
            }
            Console.WriteLine();
            if (tcp == null) { Console.WriteLine("接続できませんでした。"); return 4; }
            tcp.NoDelay = true;
            var st = tcp.GetStream();

            var hello = Proto.Build(Proto.MsgHello, new byte[] { (byte)Math.Max(0, Math.Min(Proto.MaxPlayers, slot)) });
            st.Write(hello, 0, hello.Length);

            byte type; byte[] payload;
            if (!Proto.Read(st, out type, out payload))
            {
                Console.WriteLine("サーバーからの応答がありません。");
                return 5;
            }
            if (type == Proto.MsgFull)
            {
                Console.WriteLine("サーバーが満員です。");
                return 6;
            }
            if (type != Proto.MsgWelcome || payload.Length < 2)
            {
                Console.WriteLine("想定外の応答です。");
                return 5;
            }
            _mySlot = payload[0];
            int maxPlayers = payload[1];
            Console.WriteLine("参加しました: あなたは P{0} です (最大 {1} 人)", _mySlot, maxPlayers);
            Console.WriteLine("Ctrl+C で終了します。");
            Console.WriteLine();

            Console.CancelKeyPress += delegate (object s2, ConsoleCancelEventArgs e) { _stop = true; e.Cancel = true; };

            var rx = new Thread(delegate () { ReceiveLoop(st); });
            rx.IsBackground = true;
            rx.Start();

            var ping = new Thread(delegate () { PingLoop(st); });
            ping.IsBackground = true;
            ping.Start();

            MainLoop(mem, st, netbase, source, keys, noWrite, applyOwn,
                     (varbaseOverride == 0 && !noPtr) ? needIndex : -1, delayFrames);

            _stop = true;
            try { var bye = Proto.Build(Proto.MsgBye, null); st.Write(bye, 0, bye.Length); } catch { }
            try { tcp.Close(); } catch { }
            mem.Dispose();
            Console.WriteLine();
            Console.WriteLine("終了: 送信 {0} / 受信 {1}", _txCount, _rxCount);
            return 0;
        }

        private static void ReceiveLoop(NetworkStream st)
        {
            while (!_stop)
            {
                byte type; byte[] p;
                if (!Proto.Read(st, out type, out p)) { _stop = true; return; }
                if (type == Proto.MsgFrame && p.Length >= 4 + Proto.MaxPlayers * 2 + 1)
                {
                    var m = new ushort[Proto.MaxPlayers];
                    for (int i = 0; i < Proto.MaxPlayers; i++) m[i] = BitConverter.ToUInt16(p, 4 + i * 2);
                    _remoteMasks = m;
                    _connectedBits = p[4 + Proto.MaxPlayers * 2];
                    _rxCount++;
                }
                else if (type == Proto.MsgPong && p.Length >= 8)
                {
                    long sent = BitConverter.ToInt64(p, 0);
                    long rtt = _clock.ElapsedMilliseconds - sent;
                    if (rtt >= 0 && rtt < 10000)
                        _rttMs = (_rttMs < 0) ? (int)rtt : (int)((_rttMs * 3 + rtt) / 4);
                }
                else if (type == Proto.MsgBye) { _stop = true; return; }
            }
        }

        /// <summary>往復時間の測定。0.5秒ごとに時刻印を送り、返ってきた差で RTT を出す。</summary>
        private static void PingLoop(NetworkStream st)
        {
            while (!_stop)
            {
                try
                {
                    var pkt = Proto.Build(Proto.MsgPing, Proto.StampPayload(_clock.ElapsedMilliseconds));
                    lock (st) { st.Write(pkt, 0, pkt.Length); }
                }
                catch { return; }
                Thread.Sleep(500);
            }
        }

        /// <summary>RTT から入力遅延フレーム数の目安を出す。片道ぶんを 60fps に換算し少し余裕をみる。</summary>
        private static int SuggestDelay(int rttMs)
        {
            int f = (int)Math.Ceiling(rttMs / 2.0 / (1000.0 / 60.0)) + 1;
            if (f < 1) f = 1;
            if (f > 15) f = 15;
            return f;
        }

        private static void MainLoop(GameMemory mem, NetworkStream st, int netbase,
            string source, int[] keys, bool noWrite, bool applyOwn, int trackIndex, int delayFrames)
        {
            int refreshCounter = 0;
            long localFrame = 0;
            var pacer = Stopwatch.StartNew();
            double frameMs = 1000.0 / 60.0;
            double nextDue = frameMs;

            // 相手の入力を delayFrames だけ遅らせて適用するための輪バッファ。
            // 回線のゆらぎを吸収し、少し遅れて届いても間に合うようにする。
            const int RingSize = 64;
            var ring = new ushort[RingSize][];
            for (int i = 0; i < RingSize; i++) ring[i] = new ushort[Proto.MaxPlayers];
            bool ringPrimed = false;
            long statusNext = 1000;
            long lastTx = 0, lastRx = 0;
            var sw = Stopwatch.StartNew();
            ushort lastSent = 0xFFFF;

            while (!_stop)
            {
                if (!mem.Alive) { Console.WriteLine("ゲームが終了しました。"); break; }

                // 対戦の開始・終了で変数配列は作り直される。追従する。
                if (trackIndex >= 0 && ++refreshCounter >= 200)
                {
                    refreshCounter = 0;
                    if (VarBaseFinder.Refresh(mem, trackIndex))
                        Console.WriteLine("変数配列が作り直されました。追従します: 0x{0:X}", mem.VarBase);
                }

                int tick = mem.ReadVar(VarBaseFinder.TickVar);

                // --- 送信: 自前の 60Hz で1回。
                //     ゲームの V[654] は対戦外では動かないので基準に使わない ---
                if (pacer.Elapsed.TotalMilliseconds >= nextDue)
                {
                    nextDue += frameMs;
                    double behind = pacer.Elapsed.TotalMilliseconds - nextDue;
                    if (behind > frameMs * 4) nextDue = pacer.Elapsed.TotalMilliseconds + frameMs;
                    localFrame++;
                    ushort mask = 0;
                    if (source == "netvar")
                    {
                        int b = netbase + (_mySlot - 1) * Buttons;
                        for (int i = 0; i < Buttons; i++)
                            if (mem.ReadVar(b + i) != 0) mask |= (ushort)(1 << i);
                    }
                    else
                    {
                        for (int i = 0; i < Buttons; i++)
                            if (Util.KeyDown(keys[i])) mask |= (ushort)(1 << i);
                    }
                    var pkt = Proto.Build(Proto.MsgInput, Proto.InputPayload(_mySlot, (int)localFrame, mask));
                    try { lock (st) { st.Write(pkt, 0, pkt.Length); } _txCount++; lastSent = mask; }
                    catch { Console.WriteLine("送信が切断されました。"); break; }

                    // このフレーム時点で届いている相手の入力を輪バッファへ積む
                    var cur = _remoteMasks;
                    int slotIdx = (int)(localFrame % RingSize);
                    for (int i = 0; i < Proto.MaxPlayers; i++) ring[slotIdx][i] = cur[i];
                    if (localFrame >= delayFrames) ringPrimed = true;
                }

                // --- 受信した他プレイヤーの入力を書き込む ---
                if (!noWrite)
                {
                    // delayFrames 前に積んだものを適用する
                    ushort[] m;
                    if (delayFrames == 0 || !ringPrimed) m = _remoteMasks;
                    else m = ring[(int)(((localFrame - delayFrames) % RingSize + RingSize) % RingSize)];
                    for (int s = 1; s <= Proto.MaxPlayers; s++)
                    {
                        if (s == _mySlot && !applyOwn) continue;
                        int b = netbase + (s - 1) * Buttons;
                        ushort mk = m[s - 1];
                        for (int i = 0; i < Buttons; i++)
                            mem.WriteVar(b + i, ((mk >> i) & 1));
                    }
                }

                if (sw.ElapsedMilliseconds >= statusNext)
                {
                    var m = _remoteMasks;
                    var sb = new StringBuilder();
                    sb.AppendFormat("frame {0,-7} tx {1,3}/s rx {2,3}/s  ", tick, _txCount - lastTx, _rxCount - lastRx);
                    if (_rttMs >= 0) sb.AppendFormat("RTT {0,3}ms(推奨{1}f)  ", _rttMs, SuggestDelay(_rttMs));
                    else sb.Append("RTT  --        ");
                    for (int s = 1; s <= Proto.MaxPlayers; s++)
                    {
                        bool on = ((_connectedBits >> (s - 1)) & 1) != 0;
                        sb.AppendFormat("{0}{1}:{2} ", s == _mySlot ? "*" : " ", s, on ? MaskText(m[s - 1]) : "------");
                    }
                    Console.WriteLine(sb.ToString());
                    lastTx = _txCount; lastRx = _rxCount;
                    statusNext = sw.ElapsedMilliseconds + 1000;
                }
            }
        }

        private static string MaskText(ushort m)
        {
            const string names = "UDLRAB";
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = ((m >> i) & 1) != 0 ? names[i] : '.';
            return new string(c);
        }
    }
}
