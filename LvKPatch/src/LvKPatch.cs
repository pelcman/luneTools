// LvKPatch - ゲーム側にネットワーク入力の受け取り処理を入れるツール
//
// ゲームは毎フレーム「キー入力の処理 (3014)」を6回続けて呼び、
// パッドのコードを V[321..326] へ読み込んでいる。ここを
//
//   変数配列の操作 (3013)  [Copy, 変数, netbase+(N-1)*6, 6個, 321]
//
// に置き換えると、そのプレイヤーの入力は外から書いた変数から来るようになる。
// これが入らないとゲームが毎フレーム自分で上書きしてしまい、入力同期は成立しない。
//
// 置き換えは総バイト長を保つ。余りは注釈コマンド (12410) で埋めるので、
// LCF のチャンク長には一切触らない。
//
// 注意: パッチ済みのゲームは、入力が全部ネットワーク経由になる。
// 同期クライアントが動いていないとキャラ選択以降キーを受け付けない。
// 66バイトの枠に「オフラインなら元の読み取り」という分岐を入れる余地がない
// (必要102バイト) ため、既定では元のフォルダを残してコピーに当てる。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

using LvKSync;

namespace LvKPatch
{


    internal sealed class PatchForm : Form
    {
        private readonly TextBox _folder = new TextBox();
        private readonly Button _browse = new Button();
        private readonly CheckBox _makeCopy = new CheckBox();
        private readonly TextBox _dest = new TextBox();
        private readonly TextBox _netbase = new TextBox();
        private readonly Button _check = new Button();
        private readonly Button _apply = new Button();
        private readonly Button _restore = new Button();
        private readonly TextBox _log = new TextBox();

        public PatchForm()
        {
            Text = "LvKPatch - ゲームにネットワーク入力を入れる";
            ClientSize = new Size(720, 540);
            MinimumSize = new Size(640, 460);
            Font = new Font("Yu Gothic UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Both;
            _log.WordWrap = false;
            _log.BackColor = Color.FromArgb(30, 32, 38);
            _log.ForeColor = Color.Gainsboro;
            _log.Font = new Font("Consolas", 9F);
            Controls.Add(_log);

            var top = new Panel { Dock = DockStyle.Top, Height = 156 };
            top.Controls.Add(Lab("ゲームのフォルダ", 10, 14));
            _folder.SetBounds(120, 10, 490, 24);
            top.Controls.Add(_folder);
            _browse.SetBounds(618, 9, 80, 26);
            _browse.Text = "参照…";
            _browse.Click += OnBrowse;
            top.Controls.Add(_browse);

            top.Controls.Add(Lab("RPG_RT.exe と RPG_RT.ldb があるフォルダを選んでください", 120, 40));

            _makeCopy.SetBounds(10, 66, 330, 22);
            _makeCopy.Text = "コピーを作って当てる (元のフォルダは残す)";
            _makeCopy.Checked = true;
            _makeCopy.CheckedChanged += delegate { _dest.Enabled = _makeCopy.Checked; };
            top.Controls.Add(_makeCopy);
            top.Controls.Add(Lab("作る先", 10, 96));
            _dest.SetBounds(66, 92, 544, 24);
            top.Controls.Add(_dest);
            _folder.TextChanged += delegate { SuggestDest(); };

            top.Controls.Add(Lab("入力ブロックの先頭  V[", 10, 126));
            _netbase.SetBounds(150, 122, 60, 24);
            _netbase.Text = Patcher.DefaultNetBase.ToString();
            top.Controls.Add(_netbase);
            top.Controls.Add(Lab("]   同期ツールの設定と合わせること", 212, 126));

            _check.SetBounds(410, 121, 90, 26);
            _check.Text = "調べる";
            _check.Click += delegate { Run(false); };
            top.Controls.Add(_check);
            _apply.SetBounds(506, 121, 116, 26);
            _apply.Text = "パッチを当てる";
            _apply.Click += delegate { Run(true); };
            top.Controls.Add(_apply);
            _restore.SetBounds(628, 121, 80, 26);
            _restore.Text = "元に戻す";
            _restore.Click += OnRestore;
            top.Controls.Add(_restore);

            Controls.Add(top);
            Controls.SetChildIndex(top, 0);

            Say("ゲームのフォルダを選んで「調べる」を押すと、書き換えずに中身だけ確認します。");
            Say("「パッチを当てる」を押すと、パッチ済みのコピーを作ります。元のフォルダはそのままです。");
            Say("");
            Say("【重要】パッチ済みのゲームは入力が全部ネットワーク経由になります。");
            Say("        同期クライアントを動かしていないと、キャラ選択から先で");
            Say("        キーがまったく効きません。ふだん遊ぶときは元のフォルダを使ってください。");
            Say("");
        }

        private static Label Lab(string t, int x, int y)
        {
            return new Label { Text = t, AutoSize = true, Left = x, Top = y };
        }

        /// <summary>コピー先を元フォルダの隣に「〜_online」で提案する。</summary>
        private void SuggestDest()
        {
            string f = _folder.Text.Trim();
            if (f.Length == 0) { _dest.Text = ""; return; }
            try
            {
                f = f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(f);
                string name = Path.GetFileName(f);
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return;
                _dest.Text = Path.Combine(parent, name + "_online");
            }
            catch { }
        }

        /// <summary>フォルダをまるごと複製する。</summary>
        private static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(src, dst));
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
        }

        private void Say(string s)
        {
            _log.AppendText(s + Environment.NewLine);
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "るねキャラvsカワイコチャンズ のフォルダを選んでください";
                if (_folder.Text.Trim().Length > 0 && Directory.Exists(_folder.Text.Trim()))
                    d.SelectedPath = _folder.Text.Trim();
                if (d.ShowDialog(this) == DialogResult.OK) { _folder.Text = d.SelectedPath; SuggestDest(); }
            }
        }

        private bool Prepare(out string ldb, out byte[] buf, out int netbase)
        {
            ldb = null; buf = null; netbase = Patcher.DefaultNetBase;
            string f = _folder.Text.Trim();
            if (f.Length == 0)
            {
                MessageBox.Show("ゲームのフォルダを選んでください。", "LvKPatch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            ldb = Patcher.LdbPath(f);
            if (!File.Exists(ldb))
            {
                MessageBox.Show("RPG_RT.ldb が見つかりません:\n" + ldb, "LvKPatch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!int.TryParse(_netbase.Text.Trim(), out netbase) || netbase < 1)
                netbase = Patcher.DefaultNetBase;
            buf = File.ReadAllBytes(ldb);
            return true;
        }

        private void Run(bool write)
        {
            string ldb; byte[] buf; int netbase;
            if (!Prepare(out ldb, out buf, out netbase)) return;

            Say("──────────────────────────────");
            Say((write ? "パッチ: " : "確認: ") + ldb);
            Say(string.Format("{0} バイト   入力ブロック V[{1}..{2}]",
                buf.Length, netbase, netbase + 4 * 6 - 1));

            List<Group> groups;
            try { groups = Patcher.FindGroups(buf); }
            catch (Exception ex) { Say("読み取りに失敗しました: " + ex.Message); return; }

            int todo = 0, done = 0;
            foreach (var g in groups)
            {
                if (g.Patched) { done++; continue; }
                todo++;
            }

            Say("");
            foreach (var g in groups)
            {
                if (g.Patched)
                    Say(string.Format("  0x{0:X6}  {1,3}B  すでにパッチ済み", g.Offset, g.Size));
                else
                    Say(string.Format("  0x{0:X6}  {1,3}B  {2}P の入力読み取り (indent {3})",
                        g.Offset, g.Size, g.Player, g.Indent));
            }
            Say("");
            Say(string.Format("未パッチ {0} 箇所 / パッチ済み {1} 箇所", todo, done));

            if (!write)
            {
                if (todo == 0 && done > 0) Say("→ このゲームはすでにオンライン対戦に対応しています。");
                else if (todo == 0) Say("→ 対象が見つかりません。別のゲームか、対応していないバージョンです。");
                else Say("→ 「パッチを当てる」で書き換えられます。");
                Say("");
                return;
            }

            if (todo == 0)
            {
                Say("書き換える箇所がありません。");
                Say("");
                return;
            }

            // コピーを作る場合は、以降はコピー側を書き換える
            if (_makeCopy.Checked)
            {
                string src = _folder.Text.Trim().TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dst = _dest.Text.Trim();
                if (dst.Length == 0) { Say("コピー先を指定してください。"); return; }
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst),
                        StringComparison.OrdinalIgnoreCase))
                { Say("コピー先が元のフォルダと同じです。"); return; }
                if (Directory.Exists(dst))
                {
                    if (MessageBox.Show(dst + "\n\nすでにあります。中身を上書きします。よろしいですか。",
                            "LvKPatch", MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Question) != DialogResult.OK) return;
                }
                Say("コピーしています… " + dst);
                Cursor = Cursors.WaitCursor;
                try { CopyTree(src, dst); }
                catch (Exception ex) { Say("コピーに失敗しました: " + ex.Message); return; }
                finally { Cursor = Cursors.Default; }
                Say("コピーしました。ここから先はコピー側を書き換えます。");
                ldb = Patcher.LdbPath(dst);
                buf = File.ReadAllBytes(ldb);
                groups = Patcher.FindGroups(buf);
            }

            // 元のファイルを退避してから書く
            string bak = ldb + ".bak";
            try
            {
                if (!File.Exists(bak)) File.Copy(ldb, bak);
                Say("退避: " + bak);
            }
            catch (Exception ex) { Say("退避に失敗しました: " + ex.Message); return; }

            int applied = 0;
            try
            {
                foreach (var g in groups)
                {
                    if (g.Patched) continue;
                    var blob = Patcher.MakeReplacement(g, netbase);
                    Buffer.BlockCopy(blob, 0, buf, g.Offset, blob.Length);
                    Patcher.Verify(buf, g);
                    int src = netbase + (g.Player - 1) * 6;
                    Say(string.Format("  0x{0:X6}  {1}P  ←  V[{2}..{3}] を V[321..326] へ (最初の{4}フレームは無効)",
                        g.Offset, g.Player, src, src + 5, Patcher.StartGuardFrames));
                    applied++;
                }
            }
            catch (Exception ex)
            {
                Say("書き換えに失敗しました: " + ex.Message);
                Say("ファイルは変更していません。");
                return;
            }

            try { File.WriteAllBytes(ldb, buf); }
            catch (Exception ex) { Say("保存に失敗しました: " + ex.Message); return; }

            Say("");
            Say(string.Format("{0} 箇所にパッチを当てました。", applied));
            Say("対象: " + ldb);
            Say("同期ツールの「入力ブロックの先頭」も V[" + netbase + "] にしてください。");
            Say("");
            Say("【重要】このビルドは同期クライアントを動かしていないと、");
            Say("        キャラ選択から先でキーがまったく効きません。");
            if (_makeCopy.Checked)
                Say("        ふだん遊ぶときは元のフォルダのほうを使ってください。");
            else
                Say("        オフラインで遊ぶときは「元に戻す」で戻してください。");
            Say("");
        }

        private void OnRestore(object sender, EventArgs e)
        {
            string f = _folder.Text.Trim();
            if (f.Length == 0) return;
            string ldb = Patcher.LdbPath(f);
            string bak = ldb + ".bak";
            if (!File.Exists(bak))
            {
                MessageBox.Show("退避ファイルがありません:\n" + bak, "LvKPatch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("パッチを当てる前の状態に戻します。よろしいですか。", "LvKPatch",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                File.Copy(bak, ldb, true);
                Say("元に戻しました: " + ldb);
                Say("");
            }
            catch (Exception ex) { Say("戻せませんでした: " + ex.Message); }
        }

        /// <summary>まとめて処理したいとき用。--folder ... --apply --exit</summary>
        private void ApplyArgs(string[] args)
        {
            bool apply = false, quit = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string nx = (i + 1 < args.Length) ? args[i + 1] : null;
                switch (a)
                {
                    case "--folder": if (nx != null) { _folder.Text = nx; SuggestDest(); i++; } break;
                    case "--dest": if (nx != null) { _dest.Text = nx; i++; } break;
                    case "--in-place": _makeCopy.Checked = false; break;
                    case "--netbase": if (nx != null) { _netbase.Text = nx; i++; } break;
                    case "--apply": apply = true; break;
                    case "--exit": quit = true; break;
                }
            }
            if (apply)
                Shown += delegate
                {
                    Run(true);
                    if (quit) Close();
                };
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new PatchForm();
            f.ApplyArgs(args);
            Application.Run(f);
        }
    }
}
