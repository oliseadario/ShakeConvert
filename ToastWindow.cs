using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace ShakeConvert
{
    /// <summary>Windows 11 style notification at the bottom right: real progress, result, Open / Show in folder buttons.</summary>
    internal sealed class ToastWindow : Window
    {
        const double CardMargin = 16;

        readonly TextBlock _glyph, _title, _message, _percent;
        readonly Border _glyphBg, _track;
        readonly Canvas _acrylicHolder;
        FrameworkElement _acrylic;
        double _acrylicBaseH;
        readonly Shapes.Rectangle _indet, _det;
        readonly TranslateTransform _indetMove, _slide;
        readonly ScaleTransform _detScale;
        readonly StackPanel _buttons;
        readonly Grid _card;
        DispatcherTimer _autoClose;
        Action _onCancel;
        bool _closing, _determinate, _acrylicDone;

        public ToastWindow()
        {
            Width = 400;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -30000; Top = -30000;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;

            _glyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 15, Foreground = Theme.OnAccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _glyphBg = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Theme.AccentBrush, Child = _glyph, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };

            _title = new TextBlock { FontFamily = Theme.Display, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis };
            _message = new TextBlock { FontSize = 12.5, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 54, Margin = new Thickness(0, 2, 0, 0) };

            // Fluent progress bar: indeterminate (sliding) or determinate (filling)
            _indetMove = new TranslateTransform(-100, 0);
            _indet = new Shapes.Rectangle { Width = 100, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _indetMove };
            _detScale = new ScaleTransform(0, 1);
            _det = new Shapes.Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.AccentBrush, RenderTransform = _detScale, Visibility = Visibility.Collapsed };
            var barGrid = new Grid();
            barGrid.Children.Add(_indet);
            barGrid.Children.Add(_det);
            _track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Divider, ClipToBounds = true, Child = barGrid };
            _percent = new TextBlock { FontSize = 12, Foreground = Theme.TextSecondary, Margin = new Thickness(10, -5, 0, 0), MinWidth = 34, TextAlignment = TextAlignment.Right, Visibility = Visibility.Collapsed };
            var progRow = new DockPanel { Margin = new Thickness(0, 12, 0, 2), Visibility = Visibility.Collapsed, LastChildFill = true };
            DockPanel.SetDock(_percent, Dock.Right);
            progRow.Children.Add(_percent);
            progRow.Children.Add(_track);
            _progRow = progRow;

            _buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };

            var text = new StackPanel { Margin = new Thickness(12, 0, 4, 0), Children = { _title, _message, progRow, _buttons } };
            var close = Fluent.Button("", false, OnCloseClicked, icon: true);
            close.ToolTip = L.T("Chiudi", "Close");
            close.VerticalAlignment = VerticalAlignment.Top;      // X in the top-right corner, like Windows notifications
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, -6, -2, 0);

            var content = new Grid { Margin = new Thickness(16, 14, 10, 16) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_glyphBg, 0); Grid.SetColumn(text, 1); Grid.SetColumn(close, 2);
            content.Children.Add(_glyphBg); content.Children.Add(text); content.Children.Add(close);

            _slide = new TranslateTransform(0, 28);
            _card = new Grid { Margin = new Thickness(CardMargin), Opacity = 0, RenderTransform = _slide };
            _card.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = Theme.SurfaceSolid,
                Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 8, Direction = 270, Color = Colors.Black, Opacity = Theme.IsDark ? 0.45 : 0.16, RenderingBias = RenderingBias.Performance },
                CacheMode = new BitmapCache() // shadow computed only once
            });
            _acrylicHolder = new Canvas();
            _card.Children.Add(_acrylicHolder);
            _card.Children.Add(new Border { CornerRadius = new CornerRadius(8), BorderBrush = Theme.Stroke, BorderThickness = new Thickness(1) });
            _card.Children.Add(content);
            Content = _card;

            _card.SizeChanged += (s, e) =>
            {
                _acrylicHolder.Clip = new RectangleGeometry(new Rect(0, 0, _card.ActualWidth, _card.ActualHeight), 8, 8);
                // the notification is anchored at the bottom: if it grows, the acrylic stays put relative to the screen
                if (_acrylic != null) Canvas.SetTop(_acrylic, _card.ActualHeight - _acrylicBaseH - 120);
            };

            Loaded += (s, e) =>
            {
                Reposition();
                ApplyAcrylic();
                _card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(200)));
                _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Ms(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            };
            SizeChanged += (s, e) => Reposition();
            MouseEnter += (s, e) => _autoClose?.Stop();
            MouseLeave += (s, e) => _autoClose?.Start();
        }

        readonly DockPanel _progRow;

        void Reposition()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth;
            Top = wa.Bottom - ActualHeight;
        }

        void ApplyAcrylic()
        {
            if (_acrylicDone) return;
            _acrylicDone = true;
            try
            {
                UpdateLayout();
                var hwnd = new WindowInteropHelper(this).Handle;
                double scale = Math.Max(96u, Native.GetDpiForWindow(hwnd)) / 96.0;
                var origin = _card.PointToScreen(new Point(0, -_slide.Y)); // ignore the entrance animation offset
                // taller than the current content: the notification may grow (buttons, errors)
                _acrylicBaseH = _card.ActualHeight;
                _acrylic = Acrylic.Create((int)origin.X, (int)origin.Y - (int)(120 * scale), _card.ActualWidth, _card.ActualHeight + 120, scale);
                Canvas.SetLeft(_acrylic, 0);
                Canvas.SetTop(_acrylic, -120);
                _acrylicHolder.Children.Add(_acrylic);
            }
            catch { }
        }

        // ------------------------------------------------------------ states

        public void SetProgress(string title, string message, Action onCancel)
        {
            _onCancel = onCancel;
            _glyph.Text = "";
            _title.Text = title;
            _message.Text = message;
            _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            _progRow.Visibility = Visibility.Visible;
            _buttons.Visibility = Visibility.Collapsed;
            _indetMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-100, 360, Ms(1400))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        public void SetMessage(string message)
        {
            _message.Text = message;
            _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Progress 0..1: automatically switches to the determinate bar.</summary>
        DateTime _lastValueAt;
        double _pendingValue = -1;
        DispatcherTimer _valueTimer;

        /// <summary>Progress 0..1, at most ~12 updates per second (smooth and light).</summary>
        public void SetValue(double v)
        {
            var now = DateTime.UtcNow;
            if (v < 1 && (now - _lastValueAt).TotalMilliseconds < 80)
            {
                _pendingValue = v;
                if (_valueTimer == null)
                {
                    _valueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                    _valueTimer.Tick += (s, e) =>
                    {
                        _valueTimer.Stop();
                        if (_pendingValue >= 0) { double p = _pendingValue; _pendingValue = -1; ApplyValue(p); }
                    };
                }
                if (!_valueTimer.IsEnabled) _valueTimer.Start();
                return;
            }
            _pendingValue = -1;
            ApplyValue(v);
        }

        void ApplyValue(double v)
        {
            _lastValueAt = DateTime.UtcNow;
            if (!_determinate)
            {
                _determinate = true;
                _indetMove.BeginAnimation(TranslateTransform.XProperty, null);
                _indet.Visibility = Visibility.Collapsed;
                _det.Visibility = Visibility.Visible;
                _percent.Visibility = Visibility.Visible;
            }
            v = Math.Max(0, Math.Min(1, v));
            _detScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(v, Ms(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _percent.Text = $"{v * 100:0}%";
        }

        public void SetDone(string title, string message, string openPath, string revealPath)
        {
            StopProgress();
            _glyph.Text = "";
            _glyphBg.Background = Theme.AccentBrush;
            _title.Text = title;
            SetMessage(message);
            _buttons.Children.Clear();
            if (openPath != null) _buttons.Children.Add(Fluent.Button(L.T("Apri", "Open"), true, () => { Shell.Open(openPath); CloseAnimated(); }));
            if (revealPath != null) _buttons.Children.Add(Fluent.Button(L.T("Mostra nella cartella", "Show in folder"), openPath == null, () => { Shell.Reveal(revealPath); CloseAnimated(); }));
            _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PopGlyph();
            StartAutoClose(8);
        }

        public void SetError(string title, string message)
        {
            StopProgress();
            _glyph.Text = "";
            _glyphBg.Background = Theme.Critical;
            _title.Text = title;
            _message.MaxHeight = 96;
            SetMessage(message);
            PopGlyph();
            StartAutoClose(12);
        }

        public void SetInfo(string title, string message, int seconds = 7)
        {
            StopProgress();
            _glyph.Text = "";
            _title.Text = title;
            SetMessage(message);
            StartAutoClose(seconds);
        }

        void PopGlyph()
        {
            var st = new ScaleTransform(1, 1);
            _glyphBg.RenderTransformOrigin = new Point(0.5, 0.5);
            _glyphBg.RenderTransform = st;
            var a = new DoubleAnimation(0.6, 1, Ms(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        void StopProgress()
        {
            _onCancel = null;
            _indetMove.BeginAnimation(TranslateTransform.XProperty, null);
            _progRow.Visibility = Visibility.Collapsed;
        }

        void OnCloseClicked()
        {
            if (_onCancel != null)
            {
                var c = _onCancel;
                _onCancel = null;
                _title.Text = L.T("Annullamento…", "Canceling…");
                c();
                return;
            }
            CloseAnimated();
        }

        void StartAutoClose(int seconds)
        {
            _autoClose?.Stop();
            _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _autoClose.Tick += (s, e) => { _autoClose.Stop(); CloseAnimated(); };
            if (!IsMouseOver) _autoClose.Start();
        }

        public void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            _autoClose?.Stop();
            var fade = new DoubleAnimation(0, Ms(180));
            fade.Completed += (s, e) => Close();
            _card.BeginAnimation(OpacityProperty, fade);
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, Ms(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }

        static Duration Ms(int ms) => new Duration(TimeSpan.FromMilliseconds(ms));
    }

    internal static class Shell
    {
        public static void Open(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        public static void Reveal(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) Open(path);
                else System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch { }
        }
    }
}
