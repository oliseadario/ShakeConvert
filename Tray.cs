using System;
using System.Linq;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ShakeConvert
{
    /// <summary>Notification area icon with a Windows 11 style menu (light/dark).</summary>
    internal sealed class Tray : IDisposable
    {
        readonly App _app;
        readonly WF.NotifyIcon _icon;
        readonly WF.ContextMenuStrip _menu;
        readonly WF.ToolStripMenuItem[] _sens;
        readonly WF.ToolStripMenuItem _startup;

        public Tray(App app)
        {
            _app = app;
            _menu = new WF.ContextMenuStrip { Font = new SD.Font("Segoe UI", 9.5f), ShowImageMargin = true, Padding = new WF.Padding(2, 4, 2, 4) };


            var sensMenu = Item(L.T("Sensibilità shake", "Shake sensitivity"), null);
            string[] names = { L.T("Bassa", "Low"), L.T("Media", "Medium"), L.T("Alta", "High") };
            _sens = names.Select((n, i) => Item(n, (s, e) => { _app.SetSensitivity(i); UpdateChecks(); })).ToArray();
            sensMenu.DropDownItems.AddRange(_sens);

            _startup = Item(L.T("Avvia con Windows", "Start with Windows"), (s, e) =>
            {
                AppSettings.StartWithWindows = !AppSettings.StartWithWindows;
                UpdateChecks();
            });

            var settings = Item(L.T("Impostazioni", "Settings"), (s, e) => _app.OpenSettings());

            _menu.Items.Add(settings);
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add(sensMenu);
            _menu.Items.Add(_startup);
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add(Item(L.T("Esci", "Quit"), (s, e) => System.Windows.Application.Current.Shutdown()));

            _icon = new WF.NotifyIcon
            {
                Text = L.T("ShakeConvert – trascina un file e scuotilo", "ShakeConvert – drag a file and shake it"),
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) _app.OpenSettings(); };

            _menu.Opening += (s, e) => UpdateChecks();
            RefreshTheme();
        }

        static WF.ToolStripMenuItem Item(string text, EventHandler click)
        {
            var it = new WF.ToolStripMenuItem(text) { Padding = new WF.Padding(4, 5, 4, 5) };
            if (click != null) it.Click += click;
            return it;
        }

        void UpdateChecks()
        {
            for (int i = 0; i < _sens.Length; i++) _sens[i].Checked = App.Settings.Sensitivity == i;
            _startup.Checked = AppSettings.StartWithWindows;
        }

        public void RefreshTheme()
        {
            _menu.Renderer = new FluentMenuRenderer();
            UpdateChecks();
            var old = _icon.Icon;
            _icon.Icon = MakeIcon();
            old?.Dispose();
        }

        /// <summary>
        /// Monochrome icon like the system ones: white on a dark taskbar, dark on a light taskbar.
        /// Hand-drawn for 16, 20, 24 and 32 px; Windows picks the right size for the screen DPI.
        /// </summary>
        static SD.Icon MakeIcon()
        {
            string name = Theme.TaskbarLight ? "tray-dark.ico" : "tray-light.ico";
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + name));
                using var stream = res.Stream;
                return new SD.Icon(stream, WF.SystemInformation.SmallIconSize);
            }
            catch { return SD.SystemIcons.Application; }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }

    internal sealed class FluentMenuRenderer : WF.ToolStripProfessionalRenderer
    {
        static SD.Color Bg => Theme.IsDark ? SD.Color.FromArgb(44, 44, 44) : SD.Color.FromArgb(249, 249, 249);
        static SD.Color Hover => Theme.IsDark ? SD.Color.FromArgb(61, 61, 61) : SD.Color.FromArgb(234, 234, 234);
        static SD.Color Border => Theme.IsDark ? SD.Color.FromArgb(64, 64, 64) : SD.Color.FromArgb(222, 222, 222);
        static SD.Color Fg => Theme.IsDark ? SD.Color.White : SD.Color.FromArgb(27, 27, 27);
        static SD.Color FgDisabled => Theme.IsDark ? SD.Color.FromArgb(140, 140, 140) : SD.Color.FromArgb(140, 140, 140);

        public FluentMenuRenderer() : base(new Table()) { RoundedEdges = true; }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fg : FgDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(WF.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Fg;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new SD.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundRect(r, 4);
            using var br = new SD.SolidBrush(Hover);
            e.Graphics.FillPath(br, path);
        }

        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new SD.Pen(Fg, 1.6f);
            e.Graphics.DrawLines(pen, new[]
            {
                new SD.PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new SD.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new SD.PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f)
            });
        }

        static SD.Drawing2D.GraphicsPath RoundRect(SD.Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new SD.Drawing2D.GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        sealed class Table : WF.ProfessionalColorTable
        {
            public override SD.Color ToolStripDropDownBackground => Bg;
            public override SD.Color ImageMarginGradientBegin => Bg;
            public override SD.Color ImageMarginGradientMiddle => Bg;
            public override SD.Color ImageMarginGradientEnd => Bg;
            public override SD.Color MenuBorder => Border;
            public override SD.Color MenuItemBorder => SD.Color.Transparent;
            public override SD.Color MenuItemSelected => Hover;
            public override SD.Color SeparatorDark => Border;
            public override SD.Color SeparatorLight => Bg;
            public override SD.Color CheckBackground => SD.Color.Transparent;
            public override SD.Color CheckSelectedBackground => SD.Color.Transparent;
            public override SD.Color CheckPressedBackground => SD.Color.Transparent;
        }
    }
}
