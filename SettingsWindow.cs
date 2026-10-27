using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ShakeConvert
{
    /// <summary>Settings window with Mica material, in the style of the Windows 11 Settings app.</summary>
    internal sealed class SettingsWindow : Window
    {
        readonly App _app;

        public SettingsWindow(App app)
        {
            _app = app;
            Title = L.T("ShakeConvert – Impostazioni", "ShakeConvert – Settings");
            Width = 760; Height = 780;                       // same size as Gendy
            MinWidth = 560; MinHeight = 480;
            var wa = SystemParameters.WorkArea;
            if (Height > wa.Height - 40) Height = wa.Height - 40;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            Foreground = Theme.Text;
            Background = Theme.WindowBg;
            UseLayoutRounding = true;
            try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

            // custom-drawn title bar, 40 px tall like Windows 11 apps (and Gendy):
            // it gets the same Mica material as the page
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = TitleBarHeight,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(-1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            SourceInitialized += (s, e) =>
            {
                Background = Native.ApplyMica(this, Theme.IsDark) ? Brushes.Transparent : Theme.WindowBg;
            };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            var page = new StackPanel { Margin = new Thickness(40, 12, 40, 40), MaxWidth = 1000 };

            // --- header
            var logo = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app-256.png")); } catch { }
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(logo);
            head.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "ShakeConvert", FontFamily = Theme.Display, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text },
                    new TextBlock { Text = L.T("Trascina un file, scuotilo e scegli il formato.", "Drag a file, shake it, pick a format."), FontSize = 14, Foreground = Theme.TextSecondary, Margin = new Thickness(0, 2, 0, 0) }
                }
            });
            page.Children.Add(head);

            // --- how it works
            page.Children.Add(Fluent.SectionHeader(L.T("Come funziona", "How it works")));
            var steps = new UniformGrid3();
            steps.Add("", L.T("1. Trascina", "1. Drag"), L.T("Prendi uno o più file in Esplora risorse tenendo premuto il tasto sinistro.", "Grab one or more files in File Explorer and hold the left mouse button."));
            steps.Add("", L.T("2. Scuoti", "2. Shake"), L.T("Muovi velocemente il mouse a destra e sinistra: appare il cerchio.", "Quickly move the mouse left and right: the ring appears."));
            steps.Add("", L.T("3. Rilascia", "3. Drop"), L.T("Lascia il file sul formato che vuoi. Fatto!", "Drop the file on the format you want. Done!"));
            page.Children.Add(steps.Panel);

            // --- general
            page.Children.Add(Fluent.SectionHeader(L.T("Generale", "General")));
            page.Children.Add(Fluent.Card("", L.T("Avvia con Windows", "Start with Windows"), L.T("ShakeConvert resta pronto in background, nell'area di notifica.", "ShakeConvert stays ready in the background, in the notification area."),
                Fluent.Toggle(AppSettings.StartWithWindows, v => AppSettings.StartWithWindows = v)));
            page.Children.Add(Fluent.Card("", L.T("Sensibilità dello shake", "Shake sensitivity"), L.T("Quanto devi scuotere il mouse per far apparire il menu.", "How much you need to shake the mouse to bring up the menu."),
                Fluent.Segmented(new[] { L.T("Bassa", "Low"), L.T("Media", "Medium"), L.T("Alta", "High") }, App.Settings.Sensitivity, i => _app.SetSensitivity(i))));

            // --- conversion
            page.Children.Add(Fluent.SectionHeader(L.T("Conversione", "Conversion")));
            page.Children.Add(Fluent.Card("", L.T("Dove salvare i file convertiti", "Where to save converted files"), L.T("L'originale non viene mai modificato.", "The original is never modified."),
                Fluent.Segmented(new[] { L.T("Stessa cartella", "Same folder"), L.T("Sottocartella \"Convertiti\"", "\"Converted\" subfolder") }, App.Settings.OutputMode, i =>
                {
                    App.Settings.OutputMode = i; App.Settings.Save(); App.ApplySettings();
                })));
            page.Children.Add(Fluent.Card("", L.T("Qualità delle immagini", "Image quality"), L.T("Per JPG, WEBP e AVIF. Più alta = file più grandi.", "For JPG, WEBP, AVIF and HEIC. Higher = larger files."),
                Fluent.Segmented(new[] { L.T("Buona", "Good"), L.T("Alta", "High"), L.T("Massima", "Maximum") }, App.Settings.Quality, i =>
                {
                    App.Settings.Quality = i; App.Settings.Save(); App.ApplySettings();
                })));
            page.Children.Add(Fluent.Card("", L.T("Apri il file al termine", "Open the file when done"), L.T("Apre automaticamente il risultato quando la conversione finisce.", "Automatically opens the result when the conversion finishes."),
                Fluent.Toggle(App.Settings.OpenWhenDone, v => { App.Settings.OpenWhenDone = v; App.Settings.Save(); })));

            // --- components
            page.Children.Add(Fluent.SectionHeader(L.T("Formati nel cerchio", "Formats in the ring")));
            page.Children.Add(BuildTargetsSection());

            page.Children.Add(Fluent.SectionHeader(L.T("Componenti", "Components")));
            page.Children.Add(Fluent.Card("", L.T("Audio e video", "Audio & video"), L.T("FFmpeg, per MP3, MP4, WAV, FLAC, GIF animate e molti altri.", "FFmpeg, for MP3, MP4, WAV, FLAC, animated GIF and many more."),
                Fluent.Status(Media.Ffmpeg != null, Media.Ffmpeg != null ? L.T("Incluso", "Included") : L.T("Non trovato", "Not found"))));
            page.Children.Add(Fluent.Card("", "Microsoft Office", L.T("Se presente, viene usato per Word, Excel e PowerPoint.", "If installed, it is used for Word, Excel and PowerPoint."),
                Fluent.Status(Office.HasMsOffice, Office.HasMsOffice ? L.T("Rilevato", "Detected") : L.T("Non installato", "Not installed"))));
            page.Children.Add(BuildLibreOfficeCard());
            page.Children.Add(Fluent.Card("\uEA90", L.T("Ricostruzione PDF → Word ed Excel", "PDF → Word & Excel reconstruction"),
                L.T("Motore locale pdf2docx: testo modificabile, tabelle e immagini. Funziona offline; le scansioni vengono lette con l'OCR di Windows.", "Local pdf2docx engine: editable text, tables and images. Works offline; scans are read with Windows OCR."),
                Fluent.Status(LocalEngine.Available, LocalEngine.Available ? L.T("Incluso", "Included") : L.T("Non trovato", "Not found"))));

            // --- formats
            page.Children.Add(Fluent.SectionHeader(L.T("Formati supportati", "Supported formats")));
            var formats = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (cat, list) in Converters.SupportedSummary())
            {
                formats.Children.Add(new TextBlock { Text = cat, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 6, 0, 2) });
                formats.Children.Add(new TextBlock { Text = list, FontSize = 12.5, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap });
            }
            // expandable card, collapsed by default: keeps the page clean
            var chevron = new TextBlock { Text = "\uE70D", FontFamily = Theme.Icons, FontSize = 12, Foreground = Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
            var expanderHead = Fluent.Card("\uE8FD", L.T("Mostra tutti i formati", "Show all formats"), L.T("Immagini, Photoshop, Illustrator, PDF, Office, audio e video.", "Images, Photoshop, Illustrator, PDF, Office, audio and video."), chevron);
            expanderHead.Margin = new Thickness(0);
            expanderHead.Cursor = System.Windows.Input.Cursors.Hand;
            var expander = new Border
            {
                Background = Theme.CardBg, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 4)
            };
            expanderHead.Background = Brushes.Transparent;
            expanderHead.BorderThickness = new Thickness(0);
            formats.Margin = new Thickness(56, 0, 20, 16);
            expander.Child = new StackPanel { Children = { expanderHead, formats } };
            expanderHead.MouseLeftButtonUp += (s, e) =>
            {
                bool open = formats.Visibility != Visibility.Visible;
                formats.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? "\uE70E" : "\uE70D";
            };
            page.Children.Add(expander);

            // --- about
            page.Children.Add(Fluent.SectionHeader(L.T("Informazioni", "About")));
            string ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
            page.Children.Add(Fluent.Card("", $"ShakeConvert {ver}", L.T("Funziona solo sul tuo PC: nessun dato raccolto o inviato. Impostazioni e log in %AppData%\\ShakeConvert", "Runs only on your PC: no data collected or sent. Settings and log in %AppData%\\ShakeConvert"),
                Fluent.Button(L.T("Esci da ShakeConvert", "Quit ShakeConvert"), false, () => Application.Current.Shutdown())));

            Resources.MergedDictionaries.Add(Fluent.ScrollBarStyle());
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = page,
                Focusable = false
            };
            var title = TitleBar();
            DockPanel.SetDock(title, Dock.Top);
            var root = new DockPanel { Children = { title, scroll } };
            Content = root;
            // when maximized Windows pushes the edges off screen: compensate
            StateChanged += (s, e) => root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

            // smooth content entrance
            var tt = new TranslateTransform(0, 24);
            page.RenderTransform = tt;
            page.Opacity = 0;
            Loaded += (s, e) =>
            {
                page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            };
        }

        // ================================================================ title bar

        const double TitleBarHeight = 40;

        FrameworkElement TitleBar()
        {
            var bar = new Grid { Height = TitleBarHeight, Background = Brushes.Transparent };
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            try { icon.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app-256.png")); } catch { }
            var caption = new TextBlock { Text = L.T("ShakeConvert · Impostazioni", "ShakeConvert · Settings"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Text };
            Activated += (s, e) => caption.Foreground = Theme.Text;           // like Windows: dimmer title
            Deactivated += (s, e) => caption.Foreground = Theme.TextTertiary; // when the window is inactive
            bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, caption } });

            Border CaptionButton(string glyph, string tip, Action click, bool close = false)
            {
                var t = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 10, Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var b = new Border { Width = 46, Height = TitleBarHeight, Child = t, ToolTip = tip, Background = Brushes.Transparent, Cursor = Cursors.Arrow };
                System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);
                var closeRed = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
                b.MouseEnter += (s, e) => { b.Background = close ? closeRed : Theme.ItemHover; t.Foreground = close ? Brushes.White : Theme.Text; };
                b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; t.Foreground = Theme.Text; b.Opacity = 1; };
                b.MouseLeftButtonDown += (s, e) => { e.Handled = true; b.CaptureMouse(); b.Opacity = 0.8; };
                b.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (!b.IsMouseCaptured) return;
                    b.ReleaseMouseCapture();
                    b.Opacity = 1;
                    var p = e.GetPosition(b);
                    if (p.X >= 0 && p.Y >= 0 && p.X <= b.ActualWidth && p.Y <= b.ActualHeight) click();
                };
                return b;
            }

            var max = CaptionButton("\uE922", L.T("Ingrandisci", "Maximize"), () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            var maxGlyph = (TextBlock)max.Child;
            StateChanged += (s, e) =>
            {
                maxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                max.ToolTip = WindowState == WindowState.Maximized ? L.T("Ripristina", "Restore") : L.T("Ingrandisci", "Maximize");
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    CaptionButton("\uE921", L.T("Riduci a icona", "Minimize"), () => WindowState = WindowState.Minimized),
                    max,
                    CaptionButton("\uE8BB", L.T("Chiudi", "Close"), Close, close: true)
                }
            };
            Grid.SetColumn(buttons, 1);
            bar.Children.Add(buttons);
            return bar;
        }

        /// <summary>One expandable group per category: each format is an on/off pill.</summary>
        UIElement BuildTargetsSection()
        {
            var panel = new StackPanel();
            var disabled = App.Settings.DisabledTargets;
            TextBlock summary = null;

            void Save()
            {
                App.Settings.Save();
                int total = Converters.AllTargets().Sum(c => c.items.Length);
                int off = Converters.AllTargets().SelectMany(c => c.items).Count(i => disabled.Contains(i.id));
                summary.Text = off == 0 ? L.T("Tutti i formati sono attivi.", "All formats are on.") : L.T($"{total - off} formati attivi su {total}.", $"{total - off} of {total} formats on.");
            }

            var allChips = new List<(string id, Border chip)>();
            var refreshers = new List<Action>();
            var reset = Fluent.Button(L.T("Attiva tutti", "Turn all on"), false, () =>
            {
                disabled.Clear();
                foreach (var (_, chip) in allChips) ((Action<bool>)chip.Tag)(true);
                foreach (var r in refreshers) r();
                Save();
            });
            var head = Fluent.Card("\uE71C", L.T("Scegli i formati da mostrare", "Choose which formats to show"),
                L.T("Un formato disattivato non compare più nel cerchio, per nessun tipo di file. Meno formati = cerchio più semplice.", "A format that is off no longer appears in the ring, for any file type. Fewer formats = a simpler ring."), reset, out summary);
            panel.Children.Add(head);

            foreach (var (category, glyph, items) in Converters.AllTargets())
            {
                var wrap = new WrapPanel { Margin = new Thickness(56, 0, 16, 10), Visibility = Visibility.Collapsed };
                var chips = new List<(string id, Border chip)>();
                Action refreshCategory = null;
                foreach (var (id, label) in items)
                {
                    var chip = Fluent.Chip(label, !disabled.Contains(id), on =>
                    {
                        if (on) disabled.Remove(id); else if (!disabled.Contains(id)) disabled.Add(id);
                        refreshCategory?.Invoke();
                        Save();
                    });
                    chips.Add((id, chip));
                    wrap.Children.Add(chip);
                }
                allChips.AddRange(chips);

                // quick buttons for the category
                var toggleAll = new TextBlock { FontSize = 12, Foreground = Theme.AccentBrush, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
                void RefreshToggleAll() => toggleAll.Text = chips.All(c => !disabled.Contains(c.id)) ? L.T("Disattiva tutti", "Turn all off") : L.T("Attiva tutti", "Turn all on");
                toggleAll.MouseLeftButtonUp += (s, e) =>
                {
                    bool turnOn = chips.Any(c => disabled.Contains(c.id));
                    foreach (var (id, chip) in chips)
                    {
                        if (turnOn) disabled.Remove(id); else if (!disabled.Contains(id)) disabled.Add(id);
                        ((Action<bool>)chip.Tag)(turnOn);
                    }
                    RefreshToggleAll(); Save(); e.Handled = true;
                };
                refreshCategory = RefreshToggleAll;
                refreshers.Add(RefreshToggleAll);
                RefreshToggleAll();

                var chevron = new TextBlock { Text = "\uE70D", FontFamily = Theme.Icons, FontSize = 12, Foreground = Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
                var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { toggleAll, chevron } };
                var cardHead = Fluent.Card(glyph, category, string.Join(", ", items.Take(6).Select(i => i.label)) + (items.Length > 6 ? "…" : ""), right);
                cardHead.Margin = new Thickness(0);
                cardHead.Background = Brushes.Transparent;
                cardHead.BorderThickness = new Thickness(0);
                cardHead.Cursor = System.Windows.Input.Cursors.Hand;
                cardHead.MouseLeftButtonUp += (s, e) =>
                {
                    bool open = wrap.Visibility != Visibility.Visible;
                    wrap.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    chevron.Text = open ? "\uE70E" : "\uE70D";
                };
                panel.Children.Add(new Border
                {
                    Background = Theme.CardBg, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 4),
                    Child = new StackPanel { Children = { cardHead, wrap } }
                });
            }

            summary.Visibility = Visibility.Visible;
            Save();
            return panel;
        }

        Border BuildLibreOfficeCard()
        {
            bool has = Office.Soffice != null;
            var status = new ContentControl { Focusable = false };
            Border card = null;
            TextBlock desc;

            void Refresh()
            {
                bool ok = Office.Soffice != null;
                status.Content = ok ? Fluent.Status(true, L.T("Installato", "Installed")) : InstallButton();
            }

            Border InstallButton()
            {
                Border b = null;
                b = Fluent.Button(L.T("Installa (gratis)", "Install (free)"), true, async () =>
                {
                    b.IsEnabled = false;
                    Fluent.SetButtonText(b, L.T("Installazione…", "Installing…"));
                    bool ok = await Office.InstallLibreOffice();
                    if (!ok)
                    {
                        b.IsEnabled = true;
                        Fluent.SetButtonText(b, L.T("Riprova", "Try again"));
                        MessageBox.Show(this, L.T("Non è stato possibile installare LibreOffice automaticamente.\nPuoi scaricarlo da libreoffice.org.", "LibreOffice could not be installed automatically.\nYou can download it from libreoffice.org."), "ShakeConvert", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    Refresh();
                });
                return b;
            }

            card = Fluent.Card("", "LibreOffice",
                has || Office.HasMsOffice
                    ? L.T("Usato per i documenti Office quando Microsoft Office non è disponibile.", "Used for Office documents when Microsoft Office is not available.")
                    : L.T("Serve per convertire Word, Excel e PowerPoint perché Microsoft Office non è installato.", "Needed to convert Word, Excel and PowerPoint because Microsoft Office is not installed."),
                status, out desc);
            Refresh();
            return card;
        }

        /// <summary>Three side-by-side tiles for the "how it works" steps.</summary>
        sealed class UniformGrid3
        {
            public readonly System.Windows.Controls.Primitives.UniformGrid Panel =
                new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(-2, 0, -2, 4) };

            public void Add(string glyph, string title, string text)
            {
                Panel.Children.Add(new Border
                {
                    Background = Theme.CardBg, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(16), Margin = new Thickness(2),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new Border
                            {
                                Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Theme.AccentBrush,
                                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12),
                                Child = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 16, Foreground = Theme.OnAccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                            },
                            new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text },
                            new TextBlock { Text = text, FontSize = 12.5, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }
                        }
                    }
                });
            }
        }
    }
}
