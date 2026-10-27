using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace ShakeConvert
{
    /// <summary>Windows 11 colors and fonts, read from the system theme (light/dark + accent color).</summary>
    internal static class Theme
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static bool IsWin11 => Environment.OSVersion.Version.Build >= 22000;

        public static bool IsDark { get; private set; }
        /// <summary>Settings → Personalization → Colors → Transparency effects.</summary>
        public static bool Transparency { get; private set; }
        /// <summary>Taskbar color (in Windows it can differ from the apps' one).</summary>
        public static bool TaskbarLight { get; private set; }
        public static Color AccentColor { get; private set; }
        public static Color ItemBgColor { get; private set; }
        public static Color AcrylicTint { get; private set; }

        public static Brush Surface, SurfaceSolid, Layer, Stroke, Divider, ItemBg, ItemHover, ItemPressed,
                            Text, TextSecondary, TextTertiary, AccentBrush, AccentHover, OnAccentBrush,
                            Critical, Success, WindowBg, CardBg, CardStroke, ToggleOffStroke, HubBg, FlyoutSolid;

        public static void Load()
        {
            IsDark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
            TaskbarLight = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 0;
            Transparency = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) != 0;
            AccentColor = ReadAccent(IsDark);

            if (IsDark)
            {
                AcrylicTint = Color.FromArgb(0x80, 0x1C, 0x1C, 0x1C);
                FlyoutSolid = B(0xFF2C2C2C);
                HubBg = Transparency ? B(0x14FFFFFF) : B(0xFF383838);
                Surface = B(0xF0202020);
                SurfaceSolid = B(0xFF2C2C2C);
                Layer = B(0xFF2D2D2D);
                Stroke = B(0x24FFFFFF);
                Divider = B(0x15FFFFFF);
                ItemBgColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
                ItemHover = B(0x1AFFFFFF);
                ItemPressed = B(0x0BFFFFFF);
                Text = B(0xFFFFFFFF);
                TextSecondary = B(0xC5FFFFFF);
                TextTertiary = B(0x87FFFFFF);
                OnAccentBrush = B(0xFF000000);
                Critical = B(0xFFFF99A4);
                Success = B(0xFF6CCB5F);
                WindowBg = B(0xFF202020);
                CardBg = B(0x0DFFFFFF);
                CardStroke = B(0x19000000);
                ToggleOffStroke = B(0x8BFFFFFF);
            }
            else
            {
                AcrylicTint = Color.FromArgb(0x8C, 0xF3, 0xF3, 0xF3);
                FlyoutSolid = B(0xFFF9F9F9);
                HubBg = Transparency ? B(0x99FFFFFF) : B(0xFFFFFFFF);
                Surface = B(0xF5F3F3F3);
                SurfaceSolid = B(0xFFF9F9F9);
                Layer = B(0xFFFFFFFF);
                Stroke = B(0x1A000000);
                Divider = B(0x12000000);
                ItemBgColor = Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
                ItemHover = B(0xFFF5F5F5);
                ItemPressed = B(0xFFEBEBEB);
                Text = B(0xE4000000);
                TextSecondary = B(0x9E000000);
                TextTertiary = B(0x72000000);
                OnAccentBrush = B(0xFFFFFFFF);
                Critical = B(0xFFC42B1C);
                Success = B(0xFF0F7B0F);
                WindowBg = B(0xFFF3F3F3);
                CardBg = B(0xB3FFFFFF);
                CardStroke = B(0x0F000000);
                ToggleOffStroke = B(0x72000000);
            }

            ItemBg = Freeze(new SolidColorBrush(ItemBgColor));
            AccentBrush = Freeze(new SolidColorBrush(AccentColor));
            AccentHover = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, AccentColor.R, AccentColor.G, AccentColor.B)));
        }

        private static Color ReadAccent(bool dark)
        {
            // AccentPalette: 0 Light3, 1 Light2, 2 Light1, 3 Base, 4 Dark1, 5 Dark2, 6 Dark3
            // Windows 11 uses Light2 in the dark theme and Dark1 in the light theme.
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
                {
                    int i = (dark ? 1 : 4) * 4;
                    return Color.FromRgb(p[i], p[i + 1], p[i + 2]);
                }
            }
            catch { }

            int v = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD47800));
            return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
        }

        private static int ReadDword(string path, string name, int fallback)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(path);
                if (k?.GetValue(name) is int i) return i;
            }
            catch { }
            return fallback;
        }

        private static SolidColorBrush B(uint argb) => Freeze(new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)));

        private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
