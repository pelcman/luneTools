// ゲームにネットワーク入力の受け取りを入れる処理。
//
// パッチャー (LvKPatch) と同期クライアント (LvKSyncClientGui) の両方から使う。
// 二重管理にしないよう、ここ1か所にまとめてある。
//
// ゲームは毎フレーム「キー入力の処理 (3014)」を6回続けて呼び、
// パッドのコードを V[321..326] へ読み込んでいる。ここを
//
//   変数配列の操作 (3013)  [Copy, 変数, netbase+(N-1)*6, 6, 321]
//
// に置き換えると、そのプレイヤーの入力は外から書いた変数から来るようになる。
// あわせて、試合が始まった直後の数フレームは入力を無効にする。開始ボタンの
// 押しっぱなしが1フレーム目に残ると、インスタンスごとに拾うか拾わないかが
// 変わってしまうため。
//
// キャラ選択の「Shift の設定メニュー」も同じ考え方で外から動かす。
// こちらは入力ブロックを通らず、標準の「キー入力の処理 (11610)」で
// 直接キーを読んでいた (実測)。しかも 待つ=1 なので、開いている間は
// そのインスタンスだけが完全に停止する。放っておくと、誰か1人が Shift を
// 押した時点で他とずれる。そこで2箇所を置き換える。
//
//   Shift の判定             11610 V[1]<-Shift    -> 3013 Copy V[netbase+41] -> V[1]
//   1キーの判定              11610 V[1]<-数字      -> 3013 Copy V[netbase+43] -> V[1]
//   メニューのキー (LDB側)    11610 V[1]<-キー(待つ) -> 11410 ウェイト 0.0秒
//                                                    3013 Copy V[netbase+40] -> V[1]
//
// ウェイトを入れるのは、待つ=1 を捨てるとループが1フレームに何千回も
// 回ってしまうため。1フレームに1回だけ進むようにしている。
//
// 置き換えは総バイト長を保つ。余りは注釈コマンド (12410) で埋めるので、
// LCF のチャンク長には一切触らない。

using System;
using System.Collections.Generic;
using System.IO;

namespace LvKSync
{
    #region LCF の読み書き

    public struct Cmd
    {
        public int Code;
        public int Indent;
        public int[] Params;
        public int Start;
        public int Next;
    }

    /// <summary>LCF (ツクール2000/2003) のイベントコマンド1個ぶんの読み書き。</summary>
    public static class Lcf
    {
        /// <summary>可変長整数。7ビットずつ、最後以外は継続ビットを立てる。</summary>
        public static byte[] Enc(int value)
        {
            uint v = unchecked((uint)value);
            if (v == 0) return new byte[] { 0 };
            var tmp = new List<byte>();
            while (v > 0) { tmp.Add((byte)(v & 0x7F)); v >>= 7; }
            tmp.Reverse();
            var outb = new byte[tmp.Count];
            for (int i = 0; i < tmp.Count; i++)
                outb[i] = (byte)(i == tmp.Count - 1 ? tmp[i] : (tmp[i] | 0x80));
            return outb;
        }

        public static bool Rd(byte[] b, ref int p, out uint v)
        {
            v = 0;
            int n = 0;
            while (true)
            {
                if (p >= b.Length) return false;
                byte c = b[p++];
                n++;
                v = (v << 7) | (uint)(c & 0x7F);
                if ((c & 0x80) == 0) break;
                if (n > 5) return false;
            }
            return true;
        }

        public static bool ParseCmd(byte[] b, int p, out Cmd c)
        {
            c = new Cmd();
            c.Start = p;
            uint code;
            if (!Rd(b, ref p, out code)) return false;
            if (code != 0 && (code < 1000 || code > 30000)) return false;
            uint ind;
            if (!Rd(b, ref p, out ind) || ind > 60) return false;
            uint sl;
            if (!Rd(b, ref p, out sl) || sl > 8192 || p + (long)sl > b.Length) return false;
            p += (int)sl;
            uint asz;
            if (!Rd(b, ref p, out asz) || asz > 4096) return false;
            var prm = new int[asz];
            for (int i = 0; i < asz; i++)
            {
                uint v;
                if (!Rd(b, ref p, out v)) return false;
                prm[i] = unchecked((int)v);
            }
            c.Code = (int)code;
            c.Indent = (int)ind;
            c.Params = prm;
            c.Next = p;
            return true;
        }

        public static byte[] BuildCmd(int code, int indent, byte[] str, int[] prms)
        {
            if (str == null) str = new byte[0];
            var ms = new MemoryStream();
            Write(ms, Enc(code));
            Write(ms, Enc(indent));
            Write(ms, Enc(str.Length));
            ms.Write(str, 0, str.Length);
            Write(ms, Enc(prms.Length));
            foreach (int p in prms) Write(ms, Enc(p));
            return ms.ToArray();
        }

        private static void Write(MemoryStream ms, byte[] b) { ms.Write(b, 0, b.Length); }

        /// <summary>指定バイト数ちょうどの注釈コマンドを作る。1個につき 5 + 本文バイト。</summary>
        public static byte[] Filler(int nbytes, int indent)
        {
            if (nbytes == 0) return new byte[0];
            if (nbytes < 5) throw new InvalidOperationException(nbytes + " バイトの詰め物は作れません (最低5)");
            var ms = new MemoryStream();
            while (nbytes > 0)
            {
                int take = Math.Min(nbytes, 100);
                if (nbytes - take != 0 && nbytes - take < 5) take = nbytes - 5;
                var body = new byte[take - 5];
                for (int i = 0; i < body.Length; i++) body[i] = (byte)'.';
                var c = BuildCmd(12410, indent, body, new int[0]);
                if (c.Length != take)
                    throw new InvalidOperationException("詰め物の長さが合いません");
                ms.Write(c, 0, c.Length);
                nbytes -= take;
            }
            return ms.ToArray();
        }
    }

    #endregion

    #region パッチ本体

    /// <summary>置き換える場所の種類。</summary>
    public enum SiteKind
    {
        Input,        // 対戦・キャラ選択の入力読み取り (6連の 3014)
        MenuOpen,     // キャラ選択で Shift を押したかの判定
        MenuKey,      // 設定メニューの中のキー入力
        SelectReset,  // キャラ選択で 1 を押したかの判定 (カーソルの初期化)
    }

    public sealed class Group
    {
        public SiteKind Kind = SiteKind.Input;
        public int Offset;
        public int Size;
        public int Indent;
        public int Player;      // Input のときだけ 1..4
        public bool Patched;    // すでに置き換わっている

        public string Describe()
        {
            switch (Kind)
            {
                case SiteKind.MenuOpen:    return "設定メニューを開く判定";
                case SiteKind.MenuKey:     return "設定メニューのキー入力";
                case SiteKind.SelectReset: return "カーソル初期化 (1キー) の判定";
                default:                   return Player + "P の入力読み取り";
            }
        }
    }

    public static class Patcher
    {
        public const int DefaultNetBase = 9001;
        private const int Buttons = 6;

        /// <summary>入力先の変数。ゲーム全体でここしか使っていない。</summary>
        private const int DestFirst = 321;

        /// <summary>プレイヤーNのパッド拡張コード。821-826 / 827-832 / 833-838 / 839-844。</summary>
        private const int SrcFirst = 821;

        /// <summary>試合のフレームカウンタ。</summary>
        private const int TickVar = 654;

        /// <summary>この数フレームのあいだ入力を無効にする。</summary>
        public const int StartGuardFrames = 3;

        /// <summary>ゼロが並んでいる場所 (netbase からの位置)。同期ツールが0で埋める。</summary>
        public const int ZeroBlockOffset = 30;

        /// <summary>設定メニューの中のキーコード (netbase からの位置)。
        /// 1=下 2=左 3=右 4=上 5=決定 6=キャンセル。5以上でメニューを閉じる。</summary>
        public const int MenuKeyOffset = 40;

        /// <summary>キャラ選択で設定メニューを開く合図 (netbase からの位置)。7 で開く。</summary>
        public const int MenuOpenOffset = 41;

        /// <summary>キャラ選択でカーソルを初期化する合図 (netbase からの位置)。</summary>
        public const int ResetKeyOffset = 43;

        /// <summary>
        /// 「1」キーのコード。ツクールの数字キーは 10+数字 を返すので 1 は 11。
        /// ゲーム側はこのあと値を変換してから「0より大きいか」で見ているので、
        /// 本物のキーと同じ 11 を入れておけば同じ道を通る。
        /// </summary>
        public const int ResetKeyCode = 11;

        /// <summary>標準のキー入力の処理。結果は 1=下 2=左 3=右 4=上 5=決定 6=キャンセル 11=Shift。</summary>
        private const int KeyInputProc = 11610;

        /// <summary>ウェイト。[0,0] で 0.0 秒 = 1フレーム。</summary>
        private const int WaitCmd = 11410;

        /// <summary>設定メニューのキー入力。V[1] に、待つ、方向と決定とキャンセル。</summary>
        private static readonly int[] MenuKeySig =
            { 1, 1, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 1, 1 };

        /// <summary>
        /// キャラ選択で Shift だけを見ている読み取り。V[1] に、待たない。
        ///
        /// 同じ形はゲーム中に何箇所もある。続く「V[1] > 0 なら」の分岐の中で
        /// コモンイベントを呼んでいるものだけが設定メニューで、
        /// これは全ファイル通して1箇所しかない。ただループを抜けるだけの
        /// ものや、別画面のものを巻き込んではいけない。
        ///
        /// 探し当てるまでに二度外した。注釈「Shift_menu」の箇所 (V[1]==19) は
        /// 別画面用で、キャラ選択では守り条件が成立していなかった。
        /// マップ側の V[1]==11 はチュートリアル画面だった。
        /// 最後は「そこだけを外から鳴らせるように差し替えて、
        /// ゲームが止まる (＝メニューが開く) か」で決めた。
        /// </summary>
        private static readonly int[] MenuOpenSig =
            { 1, 0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0 };

        /// <summary>Shift の判定に続く分岐。V[1] が 0 より大きいか。</summary>
        private static readonly int[] MenuOpenBranch = { 1, 1, 0, 0, 3, 0 };

        /// <summary>
        /// 数字キーを見る読み取り。V[1] に、待たない。
        ///
        /// キャラ選択の「1 を押すとカーソルが初期化される」がこれ。
        /// 中身は「キャラ選択のループを抜けて入り直す」で、
        /// 選んだキャラは残る。作者はここのコードを触っていないかもしれない
        /// (タイトル画面を廃した結果、戻り先がキャラ選択になった)。
        ///
        /// 同じ形は他にもあるので、続く「変換 → V[1] > 0 → ループ中断」まで
        /// 一致することを条件にする。全ファイル通して1箇所しかない。
        /// </summary>
        private static readonly int[] NumberKeySig =
            { 1, 0, 1, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0 };

        /// <summary>変数操作。数字キーの値を変換している。</summary>
        private const int ControlVars = 10220;

        /// <summary>ループ中断。</summary>
        private const int BreakLoop = 12120;

        /// <summary>コモンイベントの呼び出し。設定メニューかどうかの決め手。</summary>
        private const int CallCommon = 12330;

        /// <summary>
        /// 設定メニューを開け閉めするコード。ツクール標準の Shift。
        /// 閉じるほうは「5 以上」なので、同じ 7 で閉じられる。
        /// </summary>
        public const int MenuOpenCode = 7;

        /// <summary>
        /// 「3014 を6回続けて V[321..326] へ読み込む」群を形で探す。
        /// オフセット決め打ちにしないので、作者がイベントを足しても見つかる。
        /// </summary>
        public static List<Group> FindGroups(byte[] b)
        {
            var found = new List<Group>();
            int i = 0;
            while (i < b.Length - 10)
            {
                Cmd c;
                if (!Lcf.ParseCmd(b, i, out c) || c.Code != 3014) { i++; continue; }

                int q = i;
                bool ok = true;
                int indent = c.Indent;
                var srcs = new int[Buttons];
                for (int k = 0; k < Buttons; k++)
                {
                    Cmd cc;
                    if (!Lcf.ParseCmd(b, q, out cc) || cc.Code != 3014 ||
                        cc.Indent != indent || cc.Params.Length < 4 ||
                        cc.Params[1] != DestFirst + k)
                    { ok = false; break; }
                    srcs[k] = cc.Params[3];
                    q = cc.Next;
                }
                if (!ok) { i++; continue; }

                int player = PlayerOf(srcs);
                if (player == 0) { i++; continue; }

                found.Add(new Group { Offset = i, Size = q - i, Indent = indent, Player = player });
                i = q;
            }

            // すでに当ててある箇所も拾う (3013 一発 + 詰め物の注釈)
            i = 0;
            while (i < b.Length - 10)
            {
                Cmd c;
                if (!Lcf.ParseCmd(b, i, out c) || c.Code != 3013 || c.Params.Length < 5 ||
                    c.Params[0] != 0 || c.Params[3] != Buttons || c.Params[4] != DestFirst)
                { i++; continue; }
                found.Add(new Group
                {
                    Offset = i,
                    Size = c.Next - i,
                    Indent = c.Indent,
                    Player = 0,
                    Patched = true
                });
                i = c.Next;
            }

            found.Sort(delegate (Group a, Group bb) { return a.Offset.CompareTo(bb.Offset); });
            return found;
        }

        private static int PlayerOf(int[] srcs)
        {
            for (int n = 1; n <= 4; n++)
            {
                int lo = SrcFirst + (n - 1) * Buttons;
                bool all = true;
                foreach (int s in srcs)
                    if (s < lo || s > lo + Buttons - 1) { all = false; break; }
                if (all) return n;
            }
            return 0;
        }

        /// <summary>
        /// 群ひとつを置き換える。長さは変えない。
        ///   ネットワーク入力をコピー → 試合の頭の数フレームならゼロで上書き
        /// </summary>
        public static byte[] MakeReplacement(Group g, int netbase)
        {
            int src = netbase + (g.Player - 1) * Buttons;
            int zero = netbase + ZeroBlockOffset;

            var ms = new MemoryStream();
            // ネットワークから届いた入力を入れる
            var c1 = Lcf.BuildCmd(3013, g.Indent, null,
                new int[] { 0, 0, src, Buttons, DestFirst });
            ms.Write(c1, 0, c1.Length);

            // 試合が始まった直後の数フレームだけ入力を無効にする。
            // 開始ボタンの押しっぱなしが1フレーム目に残ると、インスタンスごとに
            // 拾うか拾わないかが変わってしまうため。
            //
            // V[654] は試合外では 0 なので、上限だけで見るとキャラ選択でも
            // 入力が消えてしまう。1 以上 かつ 数フレーム以下、の入れ子にする。
            //   12010 [型=変数, 変数, 相手=定数, 値, 比較, else無し]
            //   比較 0:= 1:以上 2:以下 3:超 4:未満 5:≠
            var c2 = Lcf.BuildCmd(12010, g.Indent, null,
                new int[] { 1, TickVar, 0, 1, 1, 0 });
            ms.Write(c2, 0, c2.Length);
            var c3 = Lcf.BuildCmd(12010, g.Indent + 1, null,
                new int[] { 1, TickVar, 0, StartGuardFrames, 2, 0 });
            ms.Write(c3, 0, c3.Length);
            var c4 = Lcf.BuildCmd(3013, g.Indent + 2, null,
                new int[] { 0, 0, zero, Buttons, DestFirst });
            ms.Write(c4, 0, c4.Length);
            var c5 = Lcf.BuildCmd(22011, g.Indent + 1, null, new int[0]);
            ms.Write(c5, 0, c5.Length);
            var c6 = Lcf.BuildCmd(22011, g.Indent, null, new int[0]);
            ms.Write(c6, 0, c6.Length);

            var body = ms.ToArray();
            if (body.Length > g.Size)
                throw new InvalidOperationException(
                    string.Format("置き換えが {0} バイトで、領域 {1} バイトに入りません",
                        body.Length, g.Size));
            var pad = Lcf.Filler(g.Size - body.Length, g.Indent);
            var blob = new byte[g.Size];
            Buffer.BlockCopy(body, 0, blob, 0, body.Length);
            Buffer.BlockCopy(pad, 0, blob, body.Length, pad.Length);
            return blob;
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < b.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>そこから n コマンドのうちにループ中断があるか。</summary>
        private static bool Breaks(byte[] b, int p, int n)
        {
            for (int k = 0; k < n; k++)
            {
                Cmd c;
                if (!Lcf.ParseCmd(b, p, out c)) return false;
                if (c.Code == BreakLoop) return true;
                p = c.Next;
            }
            return false;
        }

        /// <summary>そこから n コマンドのうちにコモンイベントの呼び出しがあるか。</summary>
        private static bool CallsCommon(byte[] b, int p, int n)
        {
            for (int k = 0; k < n; k++)
            {
                Cmd c;
                if (!Lcf.ParseCmd(b, p, out c)) return false;
                if (c.Code == CallCommon) return true;
                p = c.Next;
            }
            return false;
        }

        /// <summary>「変数1個を V[1] へコピーする 3013」かどうか。当て済みの目印に使う。</summary>
        private static bool IsMenuCopy(Cmd c, int src)
        {
            return c.Code == 3013 && c.Params.Length >= 5 &&
                   c.Params[0] == 0 && c.Params[2] == src &&
                   c.Params[3] == 1 && c.Params[4] == 1;
        }

        /// <summary>
        /// 設定メニューまわりの置き換え場所を、形で探す。
        ///
        /// どちらも「11610 でキーを読み、その結果 V[1] をすぐ判定する」形をしている。
        /// 後続の判定まで一致することを条件にしているので、同じ読み取りが
        /// 他の画面にあっても巻き込まない (全マップと LDB で確認済み)。
        /// </summary>
        public static List<Group> FindMenuSites(byte[] b, int netbase)
        {
            var found = new List<Group>();
            int i = 0;
            while (i < b.Length - 10)
            {
                Cmd c;
                if (!Lcf.ParseCmd(b, i, out c)) { i++; continue; }

                // 設定メニューのキー入力  11610 + 条件分岐(V[1]>=5) + 注釈
                if (c.Code == KeyInputProc && Same(c.Params, MenuKeySig))
                {
                    Cmd c2, c3;
                    if (Lcf.ParseCmd(b, c.Next, out c2) && c2.Code == 12010 &&
                        Same(c2.Params, new int[] { 1, 1, 0, 5, 1, 0 }) &&
                        Lcf.ParseCmd(b, c2.Next, out c3) && c3.Code == 12410)
                    {
                        found.Add(new Group
                        {
                            Kind = SiteKind.MenuKey,
                            Offset = i,
                            Size = c3.Next - i,
                            Indent = c.Indent
                        });
                        i = c3.Next;
                        continue;
                    }
                }

                // Shift の判定  11610 + 条件分岐(V[1]>0) + 分岐の中でコモン呼び出し
                if (c.Code == KeyInputProc && Same(c.Params, MenuOpenSig))
                {
                    Cmd c2;
                    if (Lcf.ParseCmd(b, c.Next, out c2) && c2.Code == 12010 &&
                        Same(c2.Params, MenuOpenBranch) && CallsCommon(b, c2.Next, 3))
                    {
                        found.Add(new Group
                        {
                            Kind = SiteKind.MenuOpen,
                            Offset = i,
                            Size = c.Next - i,
                            Indent = c.Indent
                        });
                        i = c2.Next;
                        continue;
                    }
                }

                // カーソル初期化 (1キー)  11610 + 変換 + 条件分岐(V[1]>0) + ループ中断
                if (c.Code == KeyInputProc && Same(c.Params, NumberKeySig))
                {
                    Cmd c2, c3;
                    if (Lcf.ParseCmd(b, c.Next, out c2) && c2.Code == ControlVars &&
                        Lcf.ParseCmd(b, c2.Next, out c3) && c3.Code == 12010 &&
                        Same(c3.Params, MenuOpenBranch) && Breaks(b, c3.Next, 3))
                    {
                        found.Add(new Group
                        {
                            Kind = SiteKind.SelectReset,
                            Offset = i,
                            Size = c.Next - i,
                            Indent = c.Indent
                        });
                        i = c2.Next;
                        continue;
                    }
                }

                // すでに当ててある箇所も拾う
                if (c.Code == WaitCmd && c.Params.Length == 2 &&
                    c.Params[0] == 0 && c.Params[1] == 0)
                {
                    Cmd c2;
                    if (Lcf.ParseCmd(b, c.Next, out c2) &&
                        IsMenuCopy(c2, netbase + MenuKeyOffset))
                    {
                        found.Add(new Group
                        {
                            Kind = SiteKind.MenuKey,
                            Offset = i,
                            Size = c2.Next - i,
                            Indent = c.Indent,
                            Patched = true
                        });
                        i = c2.Next;
                        continue;
                    }
                }
                if (IsMenuCopy(c, netbase + ResetKeyOffset))
                {
                    found.Add(new Group
                    {
                        Kind = SiteKind.SelectReset,
                        Offset = i,
                        Size = c.Next - i,
                        Indent = c.Indent,
                        Patched = true
                    });
                    i = c.Next;
                    continue;
                }
                if (IsMenuCopy(c, netbase + MenuOpenOffset))
                {
                    found.Add(new Group
                    {
                        Kind = SiteKind.MenuOpen,
                        Offset = i,
                        Size = c.Next - i,
                        Indent = c.Indent,
                        Patched = true
                    });
                    i = c.Next;
                    continue;
                }
                i++;
            }
            found.Sort(delegate (Group a, Group bb) { return a.Offset.CompareTo(bb.Offset); });
            return found;
        }

        /// <summary>設定メニューまわりの置き換えを作る。長さは変えない。</summary>
        public static byte[] MakeMenuReplacement(Group g, int netbase)
        {
            var ms = new MemoryStream();
            int padIndent = g.Indent;

            if (g.Kind == SiteKind.MenuOpen || g.Kind == SiteKind.SelectReset)
            {
                // キーを読む代わりに、ネットワークから来た合図を V[1] へ入れる。
                // すぐ後ろの判定はそのまま残すので、あとはゲーム本来の処理が動く。
                int src = netbase + (g.Kind == SiteKind.MenuOpen
                    ? MenuOpenOffset : ResetKeyOffset);
                var c1 = Lcf.BuildCmd(3013, g.Indent, null,
                    new int[] { 0, 0, src, 1, 1 });
                ms.Write(c1, 0, c1.Length);
            }
            else
            {
                // 待つ=1 を捨てるので、代わりに 1 フレームだけ待つ。
                // これが無いと、ループが1フレームに何千回も回る。
                var c1 = Lcf.BuildCmd(WaitCmd, g.Indent, null, new int[] { 0, 0 });
                ms.Write(c1, 0, c1.Length);
                var c2 = Lcf.BuildCmd(3013, g.Indent, null,
                    new int[] { 0, 0, netbase + MenuKeyOffset, 1, 1 });
                ms.Write(c2, 0, c2.Length);
                // 元の「5以上ならメニューを閉じる」判定を作り直す。
                var c3 = Lcf.BuildCmd(12010, g.Indent, null,
                    new int[] { 1, 1, 0, 5, 1, 0 });
                ms.Write(c3, 0, c3.Length);
                // 詰め物は、潰した注釈と同じ深さ (判定の中) に置く。
                padIndent = g.Indent + 1;
            }

            var body = ms.ToArray();
            if (body.Length > g.Size)
                throw new InvalidOperationException(
                    string.Format("置き換えが {0} バイトで、領域 {1} バイトに入りません",
                        body.Length, g.Size));
            var pad = Lcf.Filler(g.Size - body.Length, padIndent);
            var blob = new byte[g.Size];
            Buffer.BlockCopy(body, 0, blob, 0, body.Length);
            Buffer.BlockCopy(pad, 0, blob, body.Length, pad.Length);
            return blob;
        }

        /// <summary>フォルダの中のマップファイル。念のため探すが、いまは対象がない。</summary>
        public static string[] MapPaths(string folderOrFile)
        {
            string dir = Directory.Exists(folderOrFile)
                ? folderOrFile : Path.GetDirectoryName(Path.GetFullPath(folderOrFile));
            var list = Directory.GetFiles(dir, "Map*.lmu");
            Array.Sort(list, StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>置き換えたあと、その領域が正しく読み直せるかを確かめる。</summary>
        public static void Verify(byte[] b, Group g)
        {
            int q = g.Offset;
            while (q < g.Offset + g.Size)
            {
                Cmd c;
                if (!Lcf.ParseCmd(b, q, out c))
                    throw new InvalidOperationException(
                        string.Format("置換後のパースに失敗しました 0x{0:X6}", q));
                q = c.Next;
            }
            if (q != g.Offset + g.Size)
                throw new InvalidOperationException("境界がずれました");
        }

        public static string LdbPath(string folderOrFile)
        {
            if (File.Exists(folderOrFile) &&
                folderOrFile.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase))
                return folderOrFile;
            return Path.Combine(folderOrFile, "RPG_RT.ldb");
        }
    }

    #endregion
}
