using System.Globalization;

namespace ShakeConvert
{
    /// <summary>
    /// Tiny localization helper: every user-facing string is written as L.T("italiano", "English").
    /// The language follows the Windows display language:
    /// Italian when Windows is set to Italian, English otherwise.
    /// </summary>
    internal static class L
    {
        static bool? _it;

        /// <summary>True when the interface should be in Italian.</summary>
        public static bool It
        {
            get
            {
                if (_it == null) _it = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
                return _it.Value;
            }
        }

        public static string T(string it, string en) => It ? it : en;

        /// <summary>"1 file" / "3 files" in English, "1 file" / "3 file" in Italian.</summary>
        public static string Files(int n) => It ? $"{n} file" : $"{n} {(n == 1 ? "file" : "files")}";
    }
}
