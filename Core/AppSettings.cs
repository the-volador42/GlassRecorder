using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassRecorder.Core
{
    public enum CaptureMode { FullScreen, ActiveWindow }

    public static class HotkeyIds
    {
        public const string Start = "start";
        public const string Stop = "stop";
        public const string Pause = "pause";
        public const string Mic = "mic";
        public const string Zoom = "zoom";
        public const string Clicks = "clicks";

        public static readonly string[] All = { Start, Stop, Pause, Mic, Zoom, Clicks };

        public static string Title(string id) => id switch
        {
            Start => "Начать запись",
            Stop => "Остановить запись",
            Pause => "Пауза / продолжить",
            Mic => "Микрофон вкл/выкл",
            Zoom => "Зум к курсору (удерживать)",
            Clicks => "Показ кликов вкл/выкл",
            _ => id
        };
    }

    public sealed class AppSettings
    {
        public CaptureMode Mode { get; set; } = CaptureMode.FullScreen;
        public string MonitorDevice { get; set; } = "";
        public int Fps { get; set; } = 60;
        public int BitrateMbps { get; set; } = 16;
        public bool HardwareEncoding { get; set; } = true;
        public bool ShowCursor { get; set; } = true;
        public bool RecordSystemAudio { get; set; } = true;
        public bool MicOnAtStart { get; set; } = true;
        public bool ShowClicks { get; set; } = true;
        public double ZoomFactor { get; set; } = 2.0;
        public string OutputFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GlassRecorder");

        public Dictionary<string, List<int>> Hotkeys { get; set; } = DefaultHotkeys();

        // Virtual-key codes: F1=0x70 ... F4=0x73, letters = ASCII uppercase.
        public static Dictionary<string, List<int>> DefaultHotkeys() => new()
        {
            [HotkeyIds.Start] = new() { 0x72, 0x71 },   // F3 + F2
            [HotkeyIds.Stop] = new() { 0x72, 0x73 },    // F3 + F4
            [HotkeyIds.Pause] = new() { 0x72, 'P' },    // F3 + P
            [HotkeyIds.Mic] = new() { 0x72, 'M' },      // F3 + M  (Fn is invisible to Windows)
            [HotkeyIds.Zoom] = new() { 0x72, 'C' },     // F3 + C  (hold)
            [HotkeyIds.Clicks] = new() { 0x72, 'K' },   // F3 + K
        };

        [JsonIgnore]
        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassRecorder");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    if (s != null)
                    {
                        var defaults = DefaultHotkeys();
                        s.Hotkeys ??= new();
                        foreach (var id in HotkeyIds.All)
                            if (!s.Hotkeys.ContainsKey(id)) s.Hotkeys[id] = defaults[id];
                        return s;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
        }

        public static string FormatKeys(IEnumerable<int> keys)
        {
            var list = keys?.ToList() ?? new List<int>();
            if (list.Count == 0) return "не задано";
            return string.Join(" + ", list.Select(KeyName));
        }

        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
                case 0x5B: return "Win";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PrtSc";
                case 0x13: return "Pause";
                case 0x91: return "ScrollLock";
                case 0xC0: return "`";
            }
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if ((vk >= '0' && vk <= '9') || (vk >= 'A' && vk <= 'Z')) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            try
            {
                var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
                return key.ToString();
            }
            catch { return "0x" + vk.ToString("X2"); }
        }
    }

    public static class Log
    {
        private static readonly object Sync = new();
        public static void Write(string msg)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(AppSettings.Dir);
                    File.AppendAllText(Path.Combine(AppSettings.Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
