using System;
using System.IO;
using System.IO.Pipes;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace ShakeConvert
{
    internal sealed class AppSettings
    {
        public int Sensitivity { get; set; } = 1;   // 0 low, 1 medium, 2 high
        public int OutputMode { get; set; }          // 0 same folder, 1 "Converted" subfolder
        public int Quality { get; set; } = 1;        // 0 good (82), 1 high (92), 2 maximum (100)
        public bool OpenWhenDone { get; set; }
        public bool FirstRunDone { get; set; }
        /// <summary>Output formats hidden from the ring (e.g. "to:ico"), chosen in settings.</summary>
        public List<string> DisabledTargets { get; set; } = new List<string>();

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShakeConvert");
        static string FilePath => Path.Combine(Folder, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartWithWindows
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    return k?.GetValue("ShakeConvert") != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value) k.SetValue("ShakeConvert", $"\"{Environment.ProcessPath}\" --background");
                    else k.DeleteValue("ShakeConvert", false);
                }
                catch { }
            }
        }
    }

    /// <summary>"Convert with ShakeConvert" entry in the File Explorer right-click menu (per user, no admin).</summary>
    internal static class ExplorerMenu
    {
        const string Key = @"Software\Classes\*\shell\ShakeConvert";

        public static bool IsRegistered
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(Key);
                    return k != null;
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                if (!on) { Registry.CurrentUser.DeleteSubKeyTree(Key, false); return; }
                string exe = Environment.ProcessPath;
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                k.SetValue("", L.T("Converti con ShakeConvert", "Convert with ShakeConvert"));
                k.SetValue("Icon", $"\"{exe}\",0");
                k.SetValue("MultiSelectModel", "Player");
                using var c = k.CreateSubKey("command");
                c.SetValue("", $"\"{exe}\" \"%1\"");
            }
            catch { }
        }
    }

    /// <summary>Single instance: later instances pass their arguments to the first one through a named pipe.</summary>
    internal static class Ipc
    {
        static string PipeName => "ShakeConvert.Pipe." + Environment.UserName;

        public static void Send(string[] args)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(5000);
                using var w = new StreamWriter(c) { AutoFlush = true };
                foreach (var a in args) w.WriteLine(a);
            }
            catch { }
        }

        public static void Listen(Action<string[]> onMessage)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                        s.WaitForConnection();
                        using var r = new StreamReader(s);
                        var lines = new List<string>();
                        string line;
                        while ((line = r.ReadLine()) != null) lines.Add(line);
                        onMessage(lines.ToArray());
                    }
                    catch { Thread.Sleep(250); }
                }
            })
            { IsBackground = true, Name = "ShakeConvert IPC" };
            t.Start();
        }
    }
}
