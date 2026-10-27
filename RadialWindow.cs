using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using IO = System.IO;

namespace ShakeConvert
{
    /// <summary>
    /// Windows 11 style pie menu: each format is a glass (acrylic) slice with rounded corners,
    /// separated from the others by an even gap. The file sits in the center. The slice under the cursor
    /// grows, slides outward and takes the Windows accent color.
    /// </summary>
    internal sealed class RadialWindow : Window
    {
        const double W = 460, C = W / 2;
        const double HubR = 64;        // center circle
        const double InnerR = 78;      // inner edge of the slices
        const double OuterR = 188;     // outer edge of the slices
        const double Gap = 7;          // gap between slices (constant, parallel edges)
        const double Corner = 7;       // rounded corner radius
        const double PopScale = 1.06, PopShift = 7;
        static byte ShadowAlpha => (byte)(Theme.IsDark ? 70 : 34);   // light shadows, like Windows 11 menus

        public event Action<string[], Conversion> ConversionChosen;

        sealed class Slice
        {
            public Canvas Root;
            public Shapes.Path Glass, Overlay;
            public SolidColorBrush OverlayBrush;
            public TextBlock Glyph, Label;
            public ScaleTransform Scale;
            public TranslateTransform Shift;
            public double Mid;           // center angle (degrees)
        }

        readonly bool _clickMode;
        string[] _files;
        List<Conversion> _convs = new List<Conversion>();
        readonly List<Slice> _slices = new List<Slice>();

        readonly Grid _root;
        readonly ScaleTransform _rootScale;
        readonly Canvas _sliceLayer;
        readonly Shapes.Ellipse _hubGlass;
        readonly ScaleTransform _hubScale;
        readonly TextBlock _hubGlyph, _hubTitle, _hubSub;
        readonly System.Windows.Controls.Image _hubIcon;
        readonly DispatcherTimer _closeTimer;
        Brush _glass = Theme.FlyoutSolid;
        int _selected = -1;
        bool _populated, _closing;

        static Color OverlayIdle => Color.FromArgb(0, Theme.AccentColor.R, Theme.AccentColor.G, Theme.AccentColor.B);

        public RadialWindow(string[] clickFiles = null)
        {
            _clickMode = clickFiles != null;

            Width = W; Height = W;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = _clickMode;
            AllowDrop = !_clickMode;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -30000; Top = -30000;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;

            var outer = new Grid();
            // almost invisible disc (alpha 1/255): catches the drag even in the gaps between slices
            outer.Children.Add(new Shapes.Ellipse { Width = OuterR * 2 + 40, Height = OuterR * 2 + 40, Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) });

            _rootScale = new ScaleTransform(0.92, 0.92);
            _root = new Grid { Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _rootScale };

            // slices
            _sliceLayer = new Canvas { Width = W, Height = W };
            _root.Children.Add(_sliceLayer);

            // center circle
            _hubScale = new ScaleTransform(1, 1);
            var hub = new Grid { Width = HubR * 2, Height = HubR * 2, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _hubScale };
            _hubGlass = new Shapes.Ellipse { Fill = _glass };
            hub.Children.Add(new Shapes.Ellipse
            {
                Fill = new SolidColorBrush(Color.FromArgb(ShadowAlpha, 0, 0, 0)),
                RenderTransform = new TranslateTransform(0, 2.5),
                Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance },
                CacheMode = new BitmapCache(),
                IsHitTestVisible = false
            });
            hub.Children.Add(new Shapes.Ellipse { Fill = Theme.FlyoutSolid });
            hub.Children.Add(_hubGlass);
            hub.Children.Add(new Shapes.Ellipse { Fill = Theme.HubBg, Stroke = Theme.Stroke, StrokeThickness = 1 });

            _hubIcon = new System.Windows.Controls.Image { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_hubIcon, BitmapScalingMode.HighQuality);
            _hubGlyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 24, Foreground = Theme.AccentBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            _hubTitle = new TextBlock { FontFamily = Theme.Display, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Width = 96, HorizontalAlignment = HorizontalAlignment.Center };
            _hubSub = new TextBlock { FontSize = 11.5, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Width = 96, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            // text wraps and shrinks if needed: it's always fully readable
            hub.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                Width = HubR * 2 * 0.72, Height = HubR * 2 * 0.72,
                Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _hubIcon, _hubGlyph, _hubTitle, _hubSub } }
            });
            _root.Children.Add(hub);

            outer.Children.Add(_root);
            Content = outer;

            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _closeTimer.Tick += (s, e) => { _closeTimer.Stop(); CloseAnimated(); };

            if (_clickMode)
            {
                _files = clickFiles;
                Loaded += (s, e) => { Populate(); Appear(); Activate(); };
                MouseMove += (s, e) => Select(HitIndex(e.GetPosition(this)));
                MouseLeave += (s, e) => Select(-1);
                MouseLeftButtonUp += (s, e) =>
                {
                    int i = HitIndex(e.GetPosition(this));
                    if (i >= 0 && _convs[i].Id == NavId) { NextPage(); return; }
                    if (i >= 0) Choose(i);
                    CloseAnimated();
                };
                Deactivated += (s, e) => CloseAnimated();
                KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseAnimated(); };
            }
            else
            {
                DragEnter += OnDragEnter;
                DragOver += (s, e) => { e.Handled = true; UpdateDrag(e); };
                DragLeave += (s, e) =>
                {
                    Select(-1);
                    _closeTimer.Interval = TimeSpan.FromMilliseconds(300);
                    _closeTimer.Start();
                };
                Drop += OnDrop;
            }
        }

        // ------------------------------------------------------------ positioning

        public void ShowAt(int x, int y)
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.SetExStyle(hwnd, Native.GetExStyle(hwnd) | Native.WS_EX_TOOLWINDOW | (_clickMode ? 0 : Native.WS_EX_NOACTIVATE));

            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Show();

            double scale = Math.Max(96u, Native.GetDpiForWindow(hwnd)) / 96.0;
            int size = (int)Math.Round(W * scale);
            var wa = Native.WorkAreaAt(x, y);
            int left = Math.Max(wa.Left, Math.Min(x - size / 2, wa.Right - size));
            int top = Math.Max(wa.Top, Math.Min(y - size / 2, wa.Bottom - size));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            // glass: capture and blur the backdrop, then use it to fill the slices and the center
            _glass = Acrylic.CreateBrush(left, top, W, W, scale);
            _hubGlass.Fill = _glass;
            foreach (var s in _slices) { s.Glass.Fill = _glass; }

            if (_clickMode) Native.SetForegroundWindow(hwnd);
            else _closeTimer.Start();
        }

        public void OnMouseReleased()
        {
            if (_clickMode || _closing) return;
            _closeTimer.Stop();
            _closeTimer.Interval = TimeSpan.FromMilliseconds(400);
            _closeTimer.Start();
        }

        // ------------------------------------------------------------ drag & drop

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _closeTimer.Stop();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                CloseAnimated();
                return;
            }
            if (!_populated)
            {
                var dropped = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
                _files = dropped.Where(IO.File.Exists).ToArray();
                Populate();
                Appear();
            }
            UpdateDrag(e);
        }

        void UpdateDrag(DragEventArgs e)
        {
            int i = HitIndex(e.GetPosition(this));
            Select(i);
            e.Effects = i >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            int i = HitIndex(e.GetPosition(this));
            e.Effects = i >= 0 ? DragDropEffects.Copy : DragDropEffects.None; // Copy: the original is not moved
            if (i >= 0) Choose(i);
            CloseAnimated();
        }

        void Choose(int i)
        {
            if (_convs[i].Id == NavId) return; // dropped on "More": no conversion
            var files = _files;
            var conv = _convs[i];
            Dispatcher.BeginInvoke(new Action(() => ConversionChosen?.Invoke(files, conv)));
        }

        // ------------------------------------------------------------ slice geometry

        static Point P(double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(C + r * Math.Cos(a), C + r * Math.Sin(a));
        }

        /// <summary>Ring slice with parallel edges (constant gap) and rounded corners.</summary>
        static Geometry SliceGeometry(double a0, double a1, double r0, double r1)
        {
            Geometry core;
            if (a1 - a0 >= 359.9)
            {
                // a single format: full ring
                core = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(C, C), r1 - Corner, r1 - Corner),
                    new EllipseGeometry(new Point(C, C), r0 + Corner, r0 + Corner));
            }
            else
            {
                // the core is smaller than Corner on every side; widening it with round joins
                // brings it back to the right size with rounded corners
                double h = Gap / 2 + Corner, ri = r0 + Corner, ro = r1 - Corner;
                double di = Math.Asin(Math.Min(0.99, h / ri)) * 180 / Math.PI;
                double dout = Math.Asin(Math.Min(0.99, h / ro)) * 180 / Math.PI;
                double s0 = a0 + di, e0 = a1 - di, s1 = a0 + dout, e1 = a1 - dout;
                if (e0 <= s0) { double m = (a0 + a1) / 2; s0 = e0 = m; }

                var fig = new PathFigure { StartPoint = P(ro, s1), IsClosed = true, IsFilled = true };
                fig.Segments.Add(new ArcSegment(P(ro, e1), new Size(ro, ro), 0, e1 - s1 > 180, SweepDirection.Clockwise, true));
                fig.Segments.Add(new LineSegment(P(ri, e0), true));
                fig.Segments.Add(new ArcSegment(P(ri, s0), new Size(ri, ri), 0, e0 - s0 > 180, SweepDirection.Counterclockwise, true));
                var pg = new PathGeometry();
                pg.Figures.Add(fig);
                core = pg;
            }
            var pen = new Pen(Brushes.Black, Corner * 2) { LineJoin = PenLineJoin.Round };
            var rounded = Geometry.Combine(core, core.GetWidenedPathGeometry(pen), GeometryCombineMode.Union, null);
            rounded.Freeze();
            return rounded;
        }

        // ------------------------------------------------------------ content

        // ------------------------------------------------------------ pages ("More")

        const int PerPage = 7;                 // 7 formats + the "More" slice
        const string NavId = "nav:more";
        List<Conversion> _all = new List<Conversion>();
        int _page, _pages = 1;
        DispatcherTimer _navTimer;
        const int NavDwellMs = 900;            // how long to hold on "More" before the page changes
        bool _navArmed = true;                 // after a page change you have to leave the slice and come back

        void Populate()
        {
            _populated = true;
            _all = _files != null && _files.Length > 0 ? Converters.For(_files) : new List<Conversion>();
            _pages = _all.Count <= 8 ? 1 : (int)Math.Ceiling(_all.Count / (double)PerPage);
            _page = 0;
            BuildSlices();
            SetHubDefault();
        }

        void BuildSlices()
        {
            _sliceLayer.Children.Clear();
            _slices.Clear();
            _selected = -1;
            if (_pages <= 1) _convs = _all.ToList();
            else
            {
                _convs = _all.Skip(_page * PerPage).Take(PerPage).ToList();
                _convs.Add(new Conversion { Id = NavId, Label = L.T("ALTRI", "MORE"), Glyph = "\uE712", Description = L.T("Altri formati", "More formats") });
            }
            int n = _convs.Count;
            double step = n == 0 ? 360 : 360.0 / n;

            for (int i = 0; i < n; i++)
            {
                double mid = -90 + i * step;
                var geo = SliceGeometry(mid - step / 2, mid + step / 2, InnerR, OuterR);

                var glass = new Shapes.Path { Data = geo, Fill = _glass };
                var overlayBrush = new SolidColorBrush(OverlayIdle);
                var overlay = new Shapes.Path { Data = geo, Fill = overlayBrush, Stroke = Theme.Stroke, StrokeThickness = 1 };

                var glyph = new TextBlock { Text = _convs[i].Glyph, FontFamily = Theme.Icons, FontSize = 20, Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center };
                var label = new TextBlock { Text = _convs[i].Label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                var content = new Grid
                {
                    Width = 80, Height = 52, IsHitTestVisible = false,
                    Children = { new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } } }
                };
                var pos = P((InnerR + OuterR) / 2, mid);
                Canvas.SetLeft(content, pos.X - 40);
                Canvas.SetTop(content, pos.Y - 26);

                var scale = new ScaleTransform(1, 1, C, C);
                var shift = new TranslateTransform();
                var root = new Canvas
                {
                    Width = W, Height = W, Opacity = 0,
                    RenderTransform = new TransformGroup { Children = { scale, shift } },
                    // slice cached as a bitmap: animations move it without redrawing
                    CacheMode = new BitmapCache { RenderAtScale = 1.1, SnapsToDevicePixels = true }
                };
                root.Children.Add(new Shapes.Path
                {
                    Data = geo,
                    Fill = new SolidColorBrush(Color.FromArgb(ShadowAlpha, 0, 0, 0)),
                    RenderTransform = new TranslateTransform(0, 2.5),
                    Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance },
                    CacheMode = new BitmapCache(),
                    IsHitTestVisible = false
                });
                root.Children.Add(glass);
                root.Children.Add(overlay);
                root.Children.Add(content);
                _sliceLayer.Children.Add(root);

                _slices.Add(new Slice { Root = root, Glass = glass, Overlay = overlay, OverlayBrush = overlayBrush, Glyph = glyph, Label = label, Scale = scale, Shift = shift, Mid = mid });
            }
        }

        /// <summary>Goes to the next page of formats, with a small animation.</summary>
        void NextPage()
        {
            if (_pages <= 1) return;
            _page = (_page + 1) % _pages;
            _navArmed = false;                 // one page at a time: no rapid-fire changes
            BuildSlices();
            for (int i = 0; i < _slices.Count; i++)
            {
                var sl = _slices[i];
                var begin = TimeSpan.FromMilliseconds(i * 18);
                sl.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(150)) { BeginTime = begin });
                var grow = new DoubleAnimation(0.88, 1, Ms(260)) { BeginTime = begin, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
                sl.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                sl.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
            SetHub(null, "\uE712", L.T("Altri formati", "More formats"), L.T($"Pagina {_page + 1} di {_pages}", $"Page {_page + 1} of {_pages}"));
        }

        void NavTick(object sender, EventArgs e)
        {
            _navTimer.Stop();
            NextPage();
        }


        void SetHub(ImageSource icon, string glyph, string title, string sub)
        {
            _hubIcon.Source = icon;
            _hubIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            _hubGlyph.Visibility = icon == null && !string.IsNullOrEmpty(glyph) ? Visibility.Visible : Visibility.Collapsed;
            _hubGlyph.Text = glyph ?? "";
            _hubTitle.Text = title;
            _hubSub.Text = sub;
            _hubSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        }

        ImageSource _fileIcon;
        bool _iconLoaded;

        void SetHubDefault()
        {
            if (_files == null || _files.Length == 0) { SetHub(null, "", L.T("Nessun file", "No files"), null); return; }
            if (_convs.Count == 0)
            {
                if (Converters.ForFile(_files[0]).Count > 0)
                    SetHub(null, "\uE71C", L.T("Formati disattivati", "Formats turned off"), L.T("Riattivali nelle impostazioni", "Turn them back on in settings"));
                else
                    SetHub(null, "", L.T("Non supportato", "Not supported"), IO.Path.GetExtension(_files[0]).TrimStart('.').ToUpperInvariant());
                return;
            }
            if (_files.Length == 1)
            {
                if (!_iconLoaded) { _fileIcon = Native.FileIcon(_files[0]); _iconLoaded = true; }
                SetHub(_fileIcon, Converters.GlyphFor(_files[0]), IO.Path.GetFileName(_files[0]), null);
                return;
            }
            SetHub(null, "", L.T($"{_files.Length} file", $"{L.Files(_files.Length)}"), null);
        }

        // ------------------------------------------------------------ selection

        int HitIndex(Point p)
        {
            if (_slices.Count == 0) return -1;
            double dx = p.X - C, dy = p.Y - C;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < HubR + 6 || dist > OuterR + 36) return -1; // center or outside = cancel
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            int best = -1; double bestDiff = double.MaxValue;
            for (int i = 0; i < _slices.Count; i++)
            {
                double d = Math.Abs(((angle - _slices[i].Mid) % 360 + 540) % 360 - 180);
                if (d < bestDiff) { bestDiff = d; best = i; }
            }
            return best;
        }

        void Select(int i)
        {
            if (i == _selected) return;
            if (_selected >= 0 && _selected < _slices.Count) SetSliceState(_slices[_selected], false);
            _selected = i;
            _navTimer?.Stop();
            bool onNav = i >= 0 && _convs[i].Id == NavId;
            if (!onNav) _navArmed = true;      // left "More": next time in it can change page again
            if (i >= 0)
            {
                SetSliceState(_slices[i], true);
                if (onNav)
                {
                    var sl = _slices[i];
                    // readable text until the slice is full
                    sl.Glyph.Foreground = Theme.Text;
                    sl.Label.Foreground = Theme.Text;
                    var light = Color.FromArgb(0x40, Theme.AccentColor.R, Theme.AccentColor.G, Theme.AccentColor.B);
                    if (_navArmed)
                    {
                        // the slice fills with color: when it's full the page changes
                        sl.OverlayBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                            new ColorAnimation(light, Theme.AccentColor, Ms(NavDwellMs)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });
                        if (_navTimer == null) { _navTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NavDwellMs) }; _navTimer.Tick += NavTick; }
                        _navTimer.Start();
                        SetHub(null, "\uE712", L.T("Altri formati", "More formats"), L.T($"Pagina {_page + 1} di {_pages} · resta qui", $"Page {_page + 1} of {_pages} · hold here"));
                    }
                    else
                    {
                        sl.OverlayBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(light, Ms(120)));
                        SetHub(null, "\uE712", L.T($"Pagina {_page + 1} di {_pages}", $"Page {_page + 1} of {_pages}"), L.T("Esci e rientra per la successiva", "Move out and back for the next one"));
                    }
                }
                else SetHub(null, null, _convs[i].Description, _files.Length == 1 ? null : L.T($"{_files.Length} file", $"{L.Files(_files.Length)}"));
            }
            else SetHubDefault();
        }

        void SetSliceState(Slice s, bool on)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = on ? 0.35 : 0.0 };

            s.OverlayBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(on ? Theme.AccentColor : OverlayIdle, Ms(160)) { EasingFunction = ease });
            var fg = on ? Theme.OnAccentBrush : Theme.Text;
            s.Glyph.Foreground = fg;
            s.Label.Foreground = fg;

            var sc = new DoubleAnimation(on ? PopScale : 1.0, Ms(240)) { EasingFunction = back };
            s.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            s.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
            double a = s.Mid * Math.PI / 180;
            s.Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? Math.Cos(a) * PopShift : 0, Ms(240)) { EasingFunction = back });
            s.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? Math.Sin(a) * PopShift : 0, Ms(240)) { EasingFunction = back });

            // the active slice sits on top of the others
            Panel.SetZIndex(s.Root, on ? 1 : 0);
        }

        // ------------------------------------------------------------ animations

        void Appear()
        {
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var pop = new DoubleAnimation(1, Ms(300)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } };
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

            var hubPop = new DoubleAnimation(0.7, 1, Ms(360)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            _hubScale.BeginAnimation(ScaleTransform.ScaleXProperty, hubPop);
            _hubScale.BeginAnimation(ScaleTransform.ScaleYProperty, hubPop);

            // slices open one after another, clockwise
            for (int i = 0; i < _slices.Count; i++)
            {
                var s = _slices[i];
                var begin = TimeSpan.FromMilliseconds(40 + i * 28);
                s.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(180)) { BeginTime = begin });
                var grow = new DoubleAnimation(0.82, 1, Ms(340)) { BeginTime = begin, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
                s.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                s.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }

        public void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            _closeTimer.Stop();
            _navTimer?.Stop();
            AllowDrop = false;
            var delay = TimeSpan.FromMilliseconds(_selected >= 0 ? 90 : 0);
            var fade = new DoubleAnimation(0, Ms(150)) { BeginTime = delay };
            fade.Completed += (s, e) => Close();
            _root.BeginAnimation(OpacityProperty, fade);
            var shrink = new DoubleAnimation(0.94, Ms(150)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        static Duration Ms(int ms) => new Duration(TimeSpan.FromMilliseconds(ms));
    }
}
