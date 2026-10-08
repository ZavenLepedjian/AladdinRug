using System;
using System.Collections.Generic;
using System.Linq;
using Drawing = System.Drawing;
using WF = System.Windows.Forms;

namespace AladdinRug
{
    /// <summary>Tray icon + menu: the always-reachable way to roll the rug up or out.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly RugSet _rug;
        private readonly WF.NotifyIcon _icon;
        private readonly WF.ContextMenuStrip _menu = new WF.ContextMenuStrip();
        private readonly WF.ToolStripMenuItem _toggle, _startup, _styles, _aladdin, _roller, _broom, _pullOut, _tidy, _untidy, _autoTidy, _autoSweep;

        public TrayIcon(RugSet rug, Action quit)
        {
            _rug = rug;

            _toggle = new WF.ToolStripMenuItem("Roll up the rug", null, (s, e) => _rug.Toggle());
            _startup = new WF.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
            _startup.Click += (s, e) => StartupEntry.Enabled = _startup.Checked;

            _styles = new WF.ToolStripMenuItem("Rug style");
            foreach (StyleSpec spec in RugStyles.All)
            {
                var item = new WF.ToolStripMenuItem(spec.Name) { ToolTipText = spec.Description, Tag = spec.Style };
                item.Click += (s, e) => _rug.SetStyle((RugStyle)item.Tag);
                _styles.DropDownItems.Add(item);
            }

            _aladdin = new WF.ToolStripMenuItem("Aladdin on the rug") { CheckOnClick = true };
            _aladdin.Click += (s, e) => _rug.SetAladdin(_aladdin.Checked);

            _roller = new WF.ToolStripMenuItem("A merchant rolls the rug") { CheckOnClick = true };
            _roller.Click += (s, e) => { RugStyles.Roller = _roller.Checked; RugStyles.Save(); };

            _broom = new WF.ToolStripMenuItem("Broom: sweep the icons under the rug", null, (s, e) => _rug.Sweep());
            _pullOut = new WF.ToolStripMenuItem("Pull everything out from under the rug", null, (s, e) => _rug.PullOut());

            _tidy = new WF.ToolStripMenuItem("Merchant: sort the desktop files into folders", null, (s, e) => OnTidy());
            _untidy = new WF.ToolStripMenuItem("Undo: put the sorted files back", null, (s, e) => _rug.Untidy());

            _autoTidy = new WF.ToolStripMenuItem("Sort the desktop when the rug starts") { CheckOnClick = true };
            _autoTidy.Click += (s, e) => { RugStyles.AutoTidy = _autoTidy.Checked; RugStyles.Save(); };
            _autoSweep = new WF.ToolStripMenuItem("...and then sweep the folders under the rug") { CheckOnClick = true };
            _autoSweep.Click += (s, e) => { RugStyles.AutoSweep = _autoSweep.Checked; RugStyles.Save(); };

            _menu.Items.Add(_toggle);
            _menu.Items.Add(_tidy);
            _menu.Items.Add(_untidy);
            _menu.Items.Add(_broom);
            _menu.Items.Add(_pullOut);
            _menu.Items.Add(_styles);
            _menu.Items.Add(_aladdin);
            _menu.Items.Add(_roller);
            _menu.Items.Add(_autoTidy);
            _menu.Items.Add(_autoSweep);
            _menu.Items.Add(_startup);
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add("Take the rug away", null, (s, e) => quit());
            _menu.Opening += (s, e) => Refresh();

            _icon = new WF.NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "Aladdin Rug - click to roll up or unroll (Ctrl+Alt+R)",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) _rug.Toggle(); };
            _rug.Notice += msg => _icon.ShowBalloonTip(5000, "Aladdin Rug", msg, WF.ToolTipIcon.Warning);
        }

        // Sorting moves real files, so ask first (the test hook RUG_ORGANIZE_ONLY skips the question).
        private void OnTidy()
        {
            var (files, folders) = _rug.PreviewTidy();
            if (files == 0) { _icon.ShowBalloonTip(4000, "Aladdin Rug", "There are no loose files on the desktop to sort.", WF.ToolTipIcon.Info); return; }
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUG_ORGANIZE_ONLY")))
            {
                WF.DialogResult answer = WF.MessageBox.Show(
                    "The merchant will make " + folders + " folders on your desktop and move " + files + " loose files into them, sorted by type.\n\n" +
                    "Nothing is deleted or renamed, and your existing folders are left alone. You can undo it any time from this menu.\n\nGo ahead?",
                    "Sort the desktop?", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Question, WF.MessageBoxDefaultButton.Button2);
                if (answer != WF.DialogResult.Yes) return;
            }
            _rug.Tidy();
        }

        /// <summary>
        /// The first time the rug starts with this feature: ask once whether it may sort the loose desktop files into folders every
        /// time it starts (it moves real files). The answer is remembered and can be changed in the menu. Sweeping the folders
        /// under the rug is separate, and off unless switched on there.
        /// </summary>
        public bool AskAboutStartupChores()
        {
            if (RugStyles.AutoTidy.HasValue) return RugStyles.AutoTidy.Value;
            WF.DialogResult answer = WF.MessageBox.Show(
                "Every time the rug starts, the merchant can sort the loose files on your desktop into folders by type (\"PDF files\", \"Shortcuts\"...).\n\n" +
                "Files are moved, never deleted or overwritten, and it can all be undone from the rug's menu (Undo: put the sorted files back). " +
                "You can switch this off there too.\n\nSort the desktop every time the rug starts?",
                "Aladdin Rug", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Question, WF.MessageBoxDefaultButton.Button1);
            RugStyles.AutoTidy = answer == WF.DialogResult.Yes;
            RugStyles.Save();
            return RugStyles.AutoTidy.Value;
        }

        public void ShowMenu()
        {
            Refresh();
            _menu.Show(WF.Cursor.Position);
        }

        private void Refresh()
        {
            _toggle.Text = _rug.IsRolled ? "Unroll the rug" : "Roll up the rug";
            _startup.Checked = StartupEntry.Enabled;
            _aladdin.Checked = RugStyles.Aladdin;
            _autoTidy.Checked = RugStyles.AutoTidy == true;
            _autoSweep.Checked = RugStyles.AutoSweep;
            _autoSweep.Enabled = _autoTidy.Checked;
            int under = SweptStore.Count;
            int sorted = Organizer.Tidied;
            _untidy.Enabled = sorted > 0;
            _untidy.Text = sorted > 0 ? "Undo: put the " + sorted + " sorted files back" : "Undo: nothing has been sorted";
            _pullOut.Enabled = under > 0;
            _pullOut.Text = under > 0 ? "Pull the " + under + " things out from under the rug" : "Nothing is under the rug";
            _roller.Checked = RugStyles.Roller;
            foreach (WF.ToolStripItem item in _styles.DropDownItems)
                if (item is WF.ToolStripMenuItem mi) mi.Checked = (RugStyle)mi.Tag == RugStyles.Current;
        }

        // A tiny rug: red field, cream border, fringe at both ends.
        private static Drawing.Icon MakeIcon()
        {
            using (var bmp = new Drawing.Bitmap(32, 32))
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var fringe = new Drawing.Pen(Drawing.Color.FromArgb(0xEA, 0xDF, 0xC0), 1.2f))
                    for (int y = 9; y <= 23; y += 2)
                    {
                        g.DrawLine(fringe, 1, y, 5, y);
                        g.DrawLine(fringe, 27, y, 31, y);
                    }
                g.FillRectangle(new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1E, 0x2D, 0x55)), 5, 6, 22, 20);
                g.FillRectangle(new Drawing.SolidBrush(Drawing.Color.FromArgb(0xEA, 0xDF, 0xC0)), 6, 7, 20, 18);
                g.FillRectangle(new Drawing.SolidBrush(Drawing.Color.FromArgb(0x7B, 0x1C, 0x27)), 8, 9, 16, 14);
                g.FillPolygon(new Drawing.SolidBrush(Drawing.Color.FromArgb(0xC8, 0x99, 0x3C)),
                    new[] { new Drawing.Point(16, 11), new Drawing.Point(21, 16), new Drawing.Point(16, 21), new Drawing.Point(11, 16) });
                return Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }
}
