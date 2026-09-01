// 各プレイヤーの入力を絵で見せる表示部品。
//
// サーバー画面と、プレイヤー側クライアントの両方で使う。
//   左  いま押しているボタン (十字 + A/B)
//   右  直近数秒の履歴。押しっぱなしは横に伸びた線になる
//
// 中身を持たず、外から「名前」「いまのマスク」「履歴」をもらって描くだけ。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace LvKSync
{
    public sealed class InputView : Control
    {
        /// <summary>60fps で 5 秒ぶん。</summary>
        public const int HistorySize = 300;

        private static readonly string[] Names = { "左", "上", "下", "右", "A", "B" };

        // 6つを色で分ける。履歴を見たときにどのボタンか一目で分かるように。
        private static readonly Color[] Colors =
        {
            Color.FromArgb( 90, 170, 255),   // 左
            Color.FromArgb( 90, 230, 200),   // 上
            Color.FromArgb(140, 200, 120),   // 下
            Color.FromArgb(190, 150, 255),   // 右
            Color.FromArgb(255, 190,  70),   // A
            Color.FromArgb(255, 110, 110)    // B
        };

        private const int RowH = 34;
        private const int LabelW = 108;
        private const int PadW = 96;
        private const int LegendH = 18;

        /// <summary>スロットごとの表示名。null なら空席。添字は 1..4。</summary>
        public Func<string[]> SlotNames;

        /// <summary>スロットごとのいまのマスク。添字は 0..3。</summary>
        public Func<ushort[]> SlotMasks;

        /// <summary>履歴。[スロット 1..4, 位置]</summary>
        public ushort[,] History;

        /// <summary>履歴の書き込み位置。</summary>
        public Func<int> HistoryPos;

        /// <summary>自分のスロット。0 なら強調しない。</summary>
        public Func<int> MySlot;

        public InputView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(26, 28, 34);
        }

        /// <summary>4人ぶん並べるのに要る高さ。</summary>
        public static int PreferredHeight { get { return RowH * Proto.MaxPlayers + LegendH + 6; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (SlotNames == null || SlotMasks == null || History == null || HistoryPos == null) return;

            var names = SlotNames();
            var masks = SlotMasks();
            int mine = MySlot == null ? 0 : MySlot();

            using (var dim = new SolidBrush(Color.FromArgb(52, 56, 66)))
            using (var nameBrush = new SolidBrush(Color.Gainsboro))
            using (var meBrush = new SolidBrush(Color.FromArgb(120, 230, 150)))
            using (var f = new Font("Yu Gothic UI", 8.5F))
            {
                int hp = HistoryPos();
                int histX = LabelW + PadW + 8;
                int histW = Math.Max(40, Width - histX - 6);

                // 凡例
                int lx = histX;
                for (int b = 0; b < 6; b++)
                {
                    using (var br = new SolidBrush(Colors[b]))
                        g.FillRectangle(br, lx, 5, 9, 9);
                    g.DrawString(Names[b], f, nameBrush, lx + 11, 2);
                    lx += 38;
                }

                for (int p = 1; p <= Proto.MaxPlayers; p++)
                {
                    int y = LegendH + (p - 1) * RowH;
                    string nm = (names != null && p < names.Length) ? names[p] : null;
                    bool empty = string.IsNullOrEmpty(nm);
                    string label = p + "P  " + (empty ? "―" : nm);
                    g.DrawString(label, f, empty ? dim : (p == mine ? meBrush : nameBrush), 6, y + 9);
                    if (empty) continue;

                    ushort m = (masks != null && p - 1 < masks.Length) ? masks[p - 1] : (ushort)0;
                    DrawPad(g, LabelW, y + 3, m);

                    int lane = Math.Max(3, (RowH - 8) / 6);
                    for (int x = 0; x < histW; x++)
                    {
                        int idx = ((hp - histW + x) % HistorySize + HistorySize) % HistorySize;
                        ushort hm = History[p, idx];
                        for (int b = 0; b < 6; b++)
                        {
                            if (((hm >> b) & 1) == 0) continue;
                            using (var br = new SolidBrush(Colors[b]))
                                g.FillRectangle(br, histX + x, y + 4 + b * lane, 1, lane - 1);
                        }
                    }
                    g.DrawRectangle(Pens.DimGray, histX - 1, y + 3, histW + 1, lane * 6 + 1);
                }
            }
        }

        /// <summary>十字キーと A/B を描く。押しているところだけ光らせる。</summary>
        private void DrawPad(Graphics g, int x, int y, ushort m)
        {
            int c = 8;
            int cx = x + c, cy = y + c + 4;
            DrawCell(g, cx - c * 2, cy, c, m, 0);       // 左
            DrawCell(g, cx, cy - c - 2, c, m, 1);       // 上
            DrawCell(g, cx, cy + c + 2, c, m, 2);       // 下
            DrawCell(g, cx + c * 2, cy, c, m, 3);       // 右
            DrawRound(g, x + c * 5, cy - c, c + 2, m, 4, "A");
            DrawRound(g, x + c * 8, cy - c, c + 2, m, 5, "B");
        }

        private void DrawCell(Graphics g, int x, int y, int c, ushort m, int bit)
        {
            bool on = ((m >> bit) & 1) != 0;
            using (var br = new SolidBrush(on ? Colors[bit] : Color.FromArgb(48, 52, 62)))
                g.FillRectangle(br, x - c / 2, y - c / 2, c, c);
        }

        private void DrawRound(Graphics g, int x, int y, int d, ushort m, int bit, string text)
        {
            bool on = ((m >> bit) & 1) != 0;
            using (var br = new SolidBrush(on ? Colors[bit] : Color.FromArgb(48, 52, 62)))
                g.FillEllipse(br, x, y, d, d);
            using (var f = new Font("Yu Gothic UI", 7F))
            using (var tb = new SolidBrush(on ? Color.Black : Color.FromArgb(120, 124, 134)))
                g.DrawString(text, f, tb, x + 2, y + 1);
        }
    }
}
