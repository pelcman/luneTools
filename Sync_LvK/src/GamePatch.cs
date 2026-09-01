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

    public sealed class Group
    {
        public int Offset;
        public int Size;
        public int Indent;
        public int Player;      // 1..4
        public bool Patched;    // すでに 3013 に置き換わっている
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
