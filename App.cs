using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ShakeConvert
{
    public sealed class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            using var mutex = new Mutex(true, @"Local\ShakeConvert.SingleInstance", out bool created);
            if (!created)
            {
                // already running: pass the files (or the request to open settings) to the first instance
                Ipc.Send(args.Length > 0 ? args : new[] { "--settings" });
                return;
            }
            var app = new App { _startupArgs = args };
            app.Run();
        }

        internal static AppSettings Settings;
        string[] _startupArgs;
        ShakeDetector _detector;
        RadialWindow _current;
        SettingsWindow _settings;
        Tray _tray;

        readonly List<string> _pending = new List<string>();
        DispatcherTimer _pendingTimer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (s, a) => { a.Handled = true; Log(a.Exception); };
            TaskScheduler.UnobservedTaskException += (s, a) => { a.SetObserved(); Log(a.Exception); };

            Theme.Load();
            Settings = AppSettings.Load();
            ApplySettings();
            Converters.Init();
            System.Threading.Tasks.Task.Run(CleanTemp);

            _detector = new ShakeDetector();
            _detector.SetSensitivity(Settings.Sensitivity);
            _detector.Shaken += (x, y) => Dispatcher.BeginInvoke(new Action(() => OnShake(x, y)));
            _detector.ButtonUp += () => Dispatcher.BeginInvoke(new Action(() => _current?.OnMouseReleased()));
            _detector.Start();

            if (ExplorerMenu.IsRegistered) ExplorerMenu.Set(false); // removes the right-click menu entry from the previous version
            _tray = new Tray(this);
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;

            _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _pendingTimer.Tick += (s, a) => { _pendingTimer.Stop(); FlushPending(); };
            Ipc.Listen(lines => Dispatcher.BeginInvoke(new Action(() => HandleArgs(lines))));

            bool background = _startupArgs.Contains("--background");
            if (!Settings.FirstRunDone)
            {
                Settings.FirstRunDone = true;
                Settings.Save();
                var t = new ToastWindow();
                t.Show();
                t.SetInfo(L.T("ShakeConvert è pronto", "ShakeConvert is ready"), L.T("Trascina un file e scuotilo col mouse: apparirà il menu di conversione.", "Drag a file and give it a quick shake: the convert menu pops up."), 9);
            }
            if (_startupArgs.Contains("--settings")) OpenSettings();
            else if (_startupArgs.Any(File.Exists)) HandleArgs(_startupArgs);
            else if (!background) OpenSettings();
        }

        internal static void ApplySettings()
        {
            Converters.OutputMode = Settings.OutputMode;
            Converters.Quality = Settings.Quality switch { 0 => 82, 2 => 100, _ => 92 };
        }

        void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() => { Theme.Load(); _tray?.RefreshTheme(); }));

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            _detector?.Dispose();
            _tray?.Dispose();
            base.OnExit(e);
        }

        // ------------------------------------------------------------ inputs

        void HandleArgs(string[] args)
        {
            if (args.Contains("--settings")) { OpenSettings(); return; }
            var files = args.Where(File.Exists).ToList();
            if (files.Count == 0) return;
            // several files passed almost together: collect them for a moment
            _pending.AddRange(files.Where(f => !_pending.Contains(f, StringComparer.OrdinalIgnoreCase)));
            _pendingTimer.Stop();
            _pendingTimer.Start();
        }

        void FlushPending()
        {
            if (_pending.Count == 0) return;
            var files = _pending.ToArray();
            _pending.Clear();
            ShowClickMenu(files);
        }

        void OnShake(int x, int y)
        {
            if (_current != null) return;
            Theme.Load(); // immediately follows theme, accent color and transparency effects
            var w = new RadialWindow();
            _current = w;
            w.ConversionChosen += RunConversion;
            w.Closed += (s, e) => { if (_current == w) _current = null; };
            w.ShowAt(x, y);
        }

        void ShowClickMenu(string[] files)
        {
            Native.GetCursorPos(out var p);
            var w = new RadialWindow(files);
            w.ConversionChosen += RunConversion;
            w.ShowAt(p.X, p.Y);
        }

        internal void OpenSettings()
        {
            if (_settings != null)
            {
                if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
                _settings.Activate();
                return;
            }
            _settings = new SettingsWindow(this);
            _settings.Closed += (s, e) => _settings = null;
            _settings.Show();
            _settings.Activate();
        }

        internal void SetSensitivity(int level)
        {
            Settings.Sensitivity = level;
            Settings.Save();
            _detector.SetSensitivity(level);
        }

        internal void PickFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T("Scegli i file da convertire", "Choose files to convert") };
            if (dlg.ShowDialog() != true) return;
            ShowClickMenu(dlg.FileNames);
        }

        // ------------------------------------------------------------ conversion

        async void RunConversion(string[] files, Conversion chosen)
        {
            var cts = new CancellationTokenSource();
            var toast = new ToastWindow();
            toast.Show();
            toast.SetProgress(
                files.Length == 1 ? L.T($"Conversione in {chosen.Label}…", $"Converting to {chosen.Label}…") : L.T($"Conversione di {files.Length} file in {chosen.Label}…", $"Converting {files.Length} files to {chosen.Label}…"),
                files.Length == 1 ? Path.GetFileName(files[0]) : null,
                () => cts.Cancel());

            var outputs = new List<string>();
            var errors = new List<string>();
            bool cancelled = false;

            for (int i = 0; i < files.Length; i++)
            {
                string f = files[i];
                int index = i;
                if (files.Length > 1) toast.SetMessage(L.T($"{i + 1} di {files.Length} · {Path.GetFileName(f)}", $"{i + 1} of {files.Length} · {Path.GetFileName(f)}"));
                var job = new Job
                {
                    Source = f,
                    Token = cts.Token,
                    Progress = new Progress<double>(v => toast.SetValue((index + v) / files.Length))
                };
                try
                {
                    var conv = Converters.ForFile(f).First(c => c.Id == chosen.Id);
                    // all the work runs in the background: the UI (and the cursor) stay smooth
                    string made = await Task.Run(() => conv.Run(job));
                    outputs.Add(made);
                    Native.NotifyCreated(made); // shows up in File Explorer right away
                    toast.SetValue((index + 1.0) / files.Length);
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception ex)
                {
                    Log(ex);
                    var inner = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;
                    errors.Add($"{Path.GetFileName(f)}: {inner.Message}");
                }
            }

            if (cancelled)
            {
                toast.SetInfo(L.T("Conversione annullata", "Conversion canceled"), outputs.Count > 0 ? L.T($"{outputs.Count} file già convertiti sono stati mantenuti.", $"{L.Files(outputs.Count)} already converted were kept.") : L.T("Nessun file è stato creato.", "No files were created."), 5);
                return;
            }

            if (errors.Count == 0)
            {
                bool single = outputs.Count == 1;
                string done = single ? Path.GetFileName(outputs[0].TrimEnd('\\')) : L.T($"{outputs.Count} file creati", $"{L.Files(outputs.Count)} created");
                toast.SetDone(L.T("Conversione completata", "Conversion complete"),
                    done,
                    single ? outputs[0] : null,
                    outputs.LastOrDefault());
                if (Settings.OpenWhenDone && single) Shell.Open(outputs[0]);
            }
            else
            {
                toast.SetError(outputs.Count == 0 ? L.T("Conversione non riuscita", "Conversion failed") : L.T($"{errors.Count} file non convertiti", $"{L.Files(errors.Count)} not converted"), string.Join("\n", errors));
            }
        }

        /// <summary>Error log, kept on this PC only. Kept small: past 256 KB it starts over.</summary>
        internal static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                string path = Path.Combine(AppSettings.Folder, "log.txt");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
            }
            catch { }
        }

        /// <summary>Deletes temp files left over from previous sessions (e.g. PC turned off mid-conversion).</summary>
        static void CleanTemp()
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "ShakeConvert");
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root))
                    if (Directory.GetLastWriteTime(d) < DateTime.Now.AddHours(-6)) Converters.TryDelete(d);
            }
            catch { }
        }
    }
}
