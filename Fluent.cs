using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shapes = System.Windows.Shapes;

namespace ShakeConvert
{
    /// <summary>Controls drawn like Windows 11 (WinUI 3) ones: buttons, toggles, selectors, cards.</summary>
    internal static class Fluent
    {
        static Duration Ms(int ms) => new Duration(TimeSpan.FromMilliseconds(ms));

        // ------------------------------------------------------------ Windows 11 scrollbar

        /// <summary>
        /// Thin scrollbar like Windows 11: a discreet line that widens
        /// when you hover it; no arrows, no gray track.
        /// </summary>
        public static ResourceDictionary ScrollBarStyle()
        {
            var c = Theme.IsDark ? "#8BFFFFFF" : "#72000000";
            var ch = Theme.IsDark ? "#C5FFFFFF" : "#9E000000";
            string xaml = $@"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Style x:Key=""FluentThumb"" TargetType=""Thumb"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Thumb"">
          <Border Background=""{{TemplateBinding Background}}"" CornerRadius=""3""/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType=""ScrollBar"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""Width"" Value=""14""/>
    <Setter Property=""MinWidth"" Value=""14""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ScrollBar"">
          <Grid Background=""Transparent"">
            <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
              <Track.Thumb>
                <Thumb x:Name=""thumb"" Style=""{{StaticResource FluentThumb}}"" Background=""{c}""
                       Width=""3"" HorizontalAlignment=""Center"" Margin=""0,4""/>
              </Track.Thumb>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""thumb"" Property=""Width"" Value=""7""/>
              <Setter TargetName=""thumb"" Property=""Background"" Value=""{ch}""/>
            </Trigger>
            <Trigger SourceName=""thumb"" Property=""IsDragging"" Value=""True"">
              <Setter TargetName=""thumb"" Property=""Width"" Value=""7""/>
              <Setter TargetName=""thumb"" Property=""Background"" Value=""{ch}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        // ------------------------------------------------------------ button

        public static Border Button(string text, bool accent, Action click, bool icon = false)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = icon ? Theme.Icons : Theme.Font,
                FontSize = icon ? 10 : 13,
                Foreground = accent ? Theme.OnAccentBrush : Theme.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Brush normal = accent ? Theme.AccentBrush : icon ? Brushes.Transparent : Theme.ItemBg;
            Brush hover = accent ? Theme.AccentHover : Theme.ItemHover;
            Brush pressed = accent ? Theme.AccentHover : Theme.ItemPressed;

            var b = new Border
            {
                Child = label,
                Background = normal,
                CornerRadius = new CornerRadius(4),
                Padding = icon ? new Thickness(9) : new Thickness(14, 5, 14, 6),
                Margin = icon ? new Thickness(0) : new Thickness(0, 0, 8, 0),
                BorderBrush = Theme.Stroke,
                BorderThickness = new Thickness(accent || icon ? 0 : 1),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = icon ? 0 : 80,
                MinHeight = icon ? 0 : 32,
                Focusable = true
            };
            b.MouseEnter += (s, e) => b.Background = hover;
            b.MouseLeave += (s, e) => { b.Background = normal; b.Opacity = 1; };
            b.MouseLeftButtonDown += (s, e) => { b.Background = pressed; b.Opacity = accent ? 0.85 : 1; e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) => { b.Background = hover; b.Opacity = 1; if (b.IsEnabled) click?.Invoke(); e.Handled = true; };
            b.KeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) click?.Invoke(); };
            b.IsEnabledChanged += (s, e) => b.Opacity = b.IsEnabled ? 1 : 0.45;
            return b;
        }

        public static void SetButtonText(Border button, string text)
        {
            if (button.Child is TextBlock t) t.Text = text;
        }

        // ------------------------------------------------------------ toggle switch (ToggleSwitch)

        public static FrameworkElement Toggle(bool initial, Action<bool> changed)
        {
            bool on = initial;
            var knobMove = new TranslateTransform(on ? 20 : 0, 0);
            var knobScale = new ScaleTransform(on ? 1.2 : 1, on ? 1.2 : 1);
            var knob = new Shapes.Ellipse
            {
                Width = 12, Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { knobScale, knobMove } }
            };
            var track = new Border { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = knob, Cursor = Cursors.Hand };
            var stateText = new TextBlock { FontSize = 13, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 64, TextAlignment = TextAlignment.Right };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { stateText, track } };

            void Paint(bool animate)
            {
                track.Background = on ? Theme.AccentBrush : Brushes.Transparent;
                track.BorderBrush = on ? Theme.AccentBrush : Theme.ToggleOffStroke;
                knob.Fill = on ? Theme.OnAccentBrush : Theme.TextSecondary;
                stateText.Text = on ? L.T("Attivato", "On") : L.T("Disattivato", "Off");
                double x = on ? 20 : 0, sc = 1;
                if (animate)
                {
                    knobMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Ms(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    var a = new DoubleAnimation(sc, Ms(150));
                    knobScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                    knobScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else { knobMove.X = x; knobScale.ScaleX = knobScale.ScaleY = sc; }
            }

            panel.MouseEnter += (s, e) =>
            {
                var a = new DoubleAnimation(1.17, Ms(120));
                knobScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                knobScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            };
            panel.MouseLeave += (s, e) =>
            {
                var a = new DoubleAnimation(1, Ms(120));
                knobScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                knobScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            };
            panel.MouseLeftButtonUp += (s, e) =>
            {
                on = !on;
                Paint(true);
                changed?.Invoke(on);
                e.Handled = true;
            };
            Paint(false);
            return panel;
        }

        // ------------------------------------------------------------ segmented selector (SelectorBar)

        public static FrameworkElement Segmented(string[] options, int selected, Action<int> changed)
        {
            var host = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Theme.ItemBg,
                BorderBrush = Theme.Stroke,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            host.Child = row;
            var items = new List<Border>();

            void Paint()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    bool on = i == selected;
                    items[i].Background = on ? Theme.AccentBrush : Brushes.Transparent;
                    ((TextBlock)items[i].Child).Foreground = on ? Theme.OnAccentBrush : Theme.Text;
                    ((TextBlock)items[i].Child).FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }

            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var b = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(12, 5, 12, 6),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = options[i], FontSize = 13 }
                };
                b.MouseEnter += (s, e) => { if (idx != selected) b.Background = Theme.ItemHover; };
                b.MouseLeave += (s, e) => { if (idx != selected) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (s, e) =>
                {
                    if (selected == idx) return;
                    selected = idx;
                    Paint();
                    changed?.Invoke(idx);
                };
                items.Add(b);
                row.Children.Add(b);
            }
            Paint();
            return host;
        }

        // ------------------------------------------------------------ settings card (SettingsCard)

        public static Border Card(string glyph, string title, string description, UIElement action, out TextBlock descriptionBlock)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 20, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 18, 0) };
            var t = new TextBlock { Text = title, FontSize = 14, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap };
            var d = new TextBlock { Text = description, FontSize = 12, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0), Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible };
            descriptionBlock = d;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { t, d } };
            Grid.SetColumn(icon, 0); Grid.SetColumn(texts, 1);
            grid.Children.Add(icon); grid.Children.Add(texts);
            if (action != null)
            {
                Grid.SetColumn(action, 2);
                if (action is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(action);
            }

            return new Border
            {
                Background = Theme.CardBg,
                BorderBrush = Theme.CardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
                MinHeight = 68,
                Margin = new Thickness(0, 0, 0, 4),
                Child = grid
            };
        }

        public static Border Card(string glyph, string title, string description, UIElement action) =>
            Card(glyph, title, description, action, out _);

        /// <summary>Compact on/off pill (like Windows 11 filters): filled with the accent color when on.</summary>
        public static Border Chip(string text, bool on, Action<bool> changed)
        {
            var label = new TextBlock { Text = text, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                Child = label, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 4, 12, 5),
                Margin = new Thickness(0, 0, 6, 6), MinWidth = 52, BorderThickness = new Thickness(1), Cursor = Cursors.Hand
            };
            void Paint()
            {
                chip.Background = on ? Theme.AccentBrush : Theme.ItemBg;
                chip.BorderBrush = on ? Theme.AccentBrush : Theme.Stroke;
                label.Foreground = on ? Theme.OnAccentBrush : Theme.TextSecondary;
                label.TextDecorations = on ? null : TextDecorations.Strikethrough;
            }
            chip.MouseEnter += (s, e) => chip.Opacity = 0.85;
            chip.MouseLeave += (s, e) => chip.Opacity = 1;
            chip.MouseLeftButtonUp += (s, e) => { on = !on; Paint(); changed?.Invoke(on); e.Handled = true; };
            chip.Tag = (Action<bool>)(v => { on = v; Paint(); });   // for "turn all on/off"
            Paint();
            return chip;
        }

        public static TextBlock SectionHeader(string text) => new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            Margin = new Thickness(2, 26, 0, 8)
        };

        /// <summary>Status label with an icon (e.g. ✓ Included).</summary>
        public static FrameworkElement Status(bool ok, string text)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = ok ? "" : "", FontFamily = Theme.Icons, FontSize = 14, Foreground = ok ? Theme.Success : Theme.TextTertiary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                    new TextBlock { Text = text, FontSize = 13, Foreground = ok ? Theme.Text : Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center }
                }
            };
        }
    }
}
