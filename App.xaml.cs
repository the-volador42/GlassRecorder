using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GlassRecorder.Core;
using GlassRecorder.UI;
using Vortice.MediaFoundation;
using WinForms = System.Windows.Forms;

namespace GlassRecorder
{
    public partial class App : Application
    {
        public AppSettings Settings { get; private set; }
        public HotkeyManager Hotkeys { get; private set; }
        public Recorder Recorder { get; private set; }
        public bool IsRecording => Recorder != null;

        private Mutex _mutex;
        private MouseClickHook _mouse;
        private WinForms.NotifyIcon _tray;
        private WinForms.ToolStripMenuItem _miStart, _miPause, _miStop, _miModeFull, _miModeWindow;
        private SettingsWindow _settingsWindow;
        private RecordingPanel _panel;
        private Icon _iconIdle, _iconRec, _iconPause;
        private bool _stopping;
        private bool _starting;
        private bool _stopAfterStart;
        private bool _exitAfterStop;
        private string _lastFile;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Test helper: full-screen moving picture, used to measure load under motion.
            int stress = Array.IndexOf(e.Args, "--stresswindow");
            if (stress >= 0)
            {
                int secs = stress + 1 < e.Args.Length && int.TryParse(e.Args[stress + 1], out var ss) ? ss : 15;
                var canvas = new System.Windows.Controls.Canvas { Background = System.Windows.Media.Brushes.Black };
                var win = new Window { WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Topmost = true, Content = canvas, ShowActivated = false };
                var rnd = new Random(1);
                var dots = new System.Collections.Generic.List<(System.Windows.Shapes.Ellipse e, double vx, double vy)>();
                for (int i = 0; i < 80; i++)
                {
                    var el = new System.Windows.Shapes.Ellipse { Width = 120, Height = 120, Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))) };
                    System.Windows.Controls.Canvas.SetLeft(el, rnd.Next(1500)); System.Windows.Controls.Canvas.SetTop(el, rnd.Next(800));
                    canvas.Children.Add(el); dots.Add((el, rnd.Next(-9, 9) + 0.5, rnd.Next(-9, 9) + 0.5));
                }
                System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
                {
                    for (int i = 0; i < dots.Count; i++)
                    {
                        var (el, vx, vy) = dots[i];
                        double x = System.Windows.Controls.Canvas.GetLeft(el) + vx, y = System.Windows.Controls.Canvas.GetTop(el) + vy;
                        if (x < 0 || x > canvas.ActualWidth - 120) vx = -vx;
                        if (y < 0 || y > canvas.ActualHeight - 120) vy = -vy;
                        System.Windows.Controls.Canvas.SetLeft(el, x); System.Windows.Controls.Canvas.SetTop(el, y);
                        dots[i] = (el, vx, vy);
                    }
                };
                win.Show();
                var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                close.Tick += (_, _) => Shutdown();
                close.Start();
                return;
            }

            _mutex = new Mutex(true, "GlassRecorder.SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("GlassRecorder уже запущен — ищите его значок в трее.", "GlassRecorder");
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, ex) =>
            {
                Log.Write("UI exception: " + ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Fatal: " + ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("Task: " + ex.Exception); ex.SetObserved(); };

            Settings = AppSettings.Load();
            Settings.Save();
            MediaFactory.MFStartup(true);

            Hotkeys = new HotkeyManager(Dispatcher);
            Hotkeys.SetBindings(Settings.Hotkeys);
            Hotkeys.Pressed += OnHotkeyPressed;
            Hotkeys.Released += OnHotkeyReleased;

            CreateTray();
            _settingsWindow = new SettingsWindow(this);

            int selfTest = Array.IndexOf(e.Args, "--selftest");
            if (selfTest >= 0)
            {
                int seconds = selfTest + 1 < e.Args.Length && int.TryParse(e.Args[selfTest + 1], out var s) ? s : 5;
                _exitAfterStop = true;
                if (Array.IndexOf(e.Args, "--window") >= 0) Settings.Mode = CaptureMode.ActiveWindow; // in-memory only
                StartRecording();
                if (Array.IndexOf(e.Args, "--demo") >= 0 && Recorder != null)
                {
                    // Exercise zoom + click marks without user input.
                    var demo = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    int tick = 0;
                    demo.Tick += (_, _) =>
                    {
                        if (Recorder == null) { demo.Stop(); return; }
                        tick++;
                        Recorder.ZoomHeld = tick >= 3 && tick <= 6;
                        Interop.Native.GetCursorPos(out var p);
                        Recorder.ShowClicks = true;
                        Recorder.AddClick(p.X, p.Y, tick % 2 == 0);
                    };
                    demo.Start();
                }
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                t.Tick += (_, _) => { t.Stop(); StopRecording(); };
                t.Start();
                return;
            }

            if (Array.IndexOf(e.Args, "--settings") >= 0) ShowSettings();
            ShowToast($"Работаю в трее. Запись: {AppSettings.FormatKeys(Settings.Hotkeys[HotkeyIds.Start])}, стоп: {AppSettings.FormatKeys(Settings.Hotkeys[HotkeyIds.Stop])}");
        }

        // ---------------- hotkeys ----------------

        private void OnHotkeyPressed(string id)
        {
            switch (id)
            {
                case HotkeyIds.Start: StartRecording(); break;
                case HotkeyIds.Stop: StopRecording(); break;
                case HotkeyIds.Pause: TogglePause(); break;
                case HotkeyIds.Mic: ToggleMic(); break;
                case HotkeyIds.Clicks: ToggleClicks(); break;
                case HotkeyIds.Zoom: if (Recorder != null) Recorder.ZoomHeld = true; _panel?.Refresh(); break;
            }
        }

        private void OnHotkeyReleased(string id)
        {
            if (id == HotkeyIds.Zoom && Recorder != null) { Recorder.ZoomHeld = false; _panel?.Refresh(); }
        }

        // ---------------- recording control ----------------

        public async void StartRecording()
        {
            if (IsRecording || _stopping || _starting) return;
            _starting = true;
            _stopAfterStart = false;
            var rec = new Recorder(Settings);
            Recorder = rec;
            // Show the panel right away; the GPU encoder takes a moment to warm up.
            _panel = new RecordingPanel(this);
            _panel.Show();
            UpdateUi();
            try
            {
                await Task.Run(() => rec.Start());
            }
            catch (Exception ex)
            {
                Log.Write("Start failed: " + ex);
                Recorder = null;
                _starting = false;
                _panel?.Close(); _panel = null;
                try { rec.Dispose(); } catch { }
                UpdateUi();
                ShowToast("Не удалось начать запись: " + ex.Message);
                if (_exitAfterStop) ExitApp();
                return;
            }
            _starting = false;
            _mouse = new MouseClickHook();
            _mouse.Click += (x, y, left) => Recorder?.AddClick(x, y, left);
            UpdateUi();
            if (_stopAfterStart) StopRecording();
        }

        public void StopRecording()
        {
            if (_starting) { _stopAfterStart = true; return; }
            if (!IsRecording || _stopping) return;
            _stopping = true;
            var rec = Recorder;
            Recorder = null;
            _mouse?.Dispose(); _mouse = null;
            _panel?.Close(); _panel = null;
            UpdateUi();

            Task.Run(() => rec.Stop()).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    _stopping = false;
                    UpdateUi();
                    if (t.IsFaulted)
                    {
                        Log.Write("Stop failed: " + t.Exception);
                        ShowToast("Ошибка при сохранении записи (подробности в log.txt)");
                    }
                    else
                    {
                        _lastFile = t.Result;
                        ShowToast("Сохранено: " + Path.GetFileName(_lastFile) + "\nНажмите, чтобы открыть папку.");
                    }
                    if (_exitAfterStop) ExitApp();
                });
            });
        }

        public void TogglePause()
        {
            if (Recorder == null || _starting) return;
            if (Recorder.IsPaused) Recorder.Resume(); else Recorder.Pause();
            UpdateUi();
        }

        public void ToggleMic()
        {
            if (Recorder != null)
            {
                if (_starting) return;
                if (!Recorder.HasMic) { ShowToast("Микрофон не найден"); return; }
                Recorder.MicEnabled = !Recorder.MicEnabled;
                _panel?.Refresh();
            }
            else
            {
                Settings.MicOnAtStart = !Settings.MicOnAtStart;
                SettingsChanged();
                ShowToast("Микрофон при старте записи: " + (Settings.MicOnAtStart ? "включён" : "выключен"));
            }
        }

        public void ToggleClicks()
        {
            if (Recorder != null)
            {
                Recorder.ShowClicks = !Recorder.ShowClicks;
                _panel?.Refresh();
            }
            else
            {
                Settings.ShowClicks = !Settings.ShowClicks;
                SettingsChanged();
                ShowToast("Показ кликов: " + (Settings.ShowClicks ? "включён" : "выключен"));
            }
        }

        public void SettingsChanged()
        {
            Settings.Save();
            Hotkeys.SetBindings(Settings.Hotkeys);
            UpdateUi();
        }

        public void OpenOutputFolder()
        {
            try
            {
                Directory.CreateDirectory(Settings.OutputFolder);
                if (_lastFile != null && File.Exists(_lastFile))
                    Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
                else
                    Process.Start("explorer.exe", $"\"{Settings.OutputFolder}\"");
            }
            catch (Exception ex) { Log.Write("Open folder: " + ex.Message); }
        }

        public void ShowSettings()
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        public void ShowToast(string text)
        {
            try { _tray?.ShowBalloonTip(3500, "GlassRecorder", text, WinForms.ToolTipIcon.None); } catch { }
        }

        private void UpdateUi()
        {
            bool rec = IsRecording;
            bool paused = rec && Recorder.IsPaused;
            if (_tray != null)
            {
                _tray.Icon = !rec ? _iconIdle : paused ? _iconPause : _iconRec;
                _tray.Text = !rec ? "GlassRecorder — готов" : paused ? "GlassRecorder — пауза" : "GlassRecorder — идёт запись";
                _miStart.Enabled = !rec && !_stopping;
                _miStart.ShortcutKeyDisplayString = AppSettings.FormatKeys(Settings.Hotkeys[HotkeyIds.Start]);
                _miStop.Enabled = rec;
                _miStop.ShortcutKeyDisplayString = AppSettings.FormatKeys(Settings.Hotkeys[HotkeyIds.Stop]);
                _miPause.Enabled = rec;
                _miPause.Text = paused ? "Продолжить" : "Пауза";
                _miPause.ShortcutKeyDisplayString = AppSettings.FormatKeys(Settings.Hotkeys[HotkeyIds.Pause]);
                _miModeFull.Checked = Settings.Mode == CaptureMode.FullScreen;
                _miModeWindow.Checked = Settings.Mode == CaptureMode.ActiveWindow;
            }
            _settingsWindow?.UpdateState(rec, paused);
            _panel?.Refresh();
        }

        // ---------------- tray ----------------

        private void CreateTray()
        {
            _iconIdle = MakeIcon(Color.FromArgb(255, 69, 58), recording: false);
            _iconRec = MakeIcon(Color.FromArgb(255, 69, 58), recording: true);
            _iconPause = MakeIcon(Color.FromArgb(255, 204, 0), recording: true);

            var menu = new WinForms.ContextMenuStrip
            {
                Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()),
                ShowImageMargin = false,
                Font = new Font("Segoe UI", 10f),
            };
            _miStart = new WinForms.ToolStripMenuItem("Начать запись", null, (_, _) => StartRecording());
            _miPause = new WinForms.ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
            _miStop = new WinForms.ToolStripMenuItem("Остановить и сохранить", null, (_, _) => StopRecording());
            _miModeFull = new WinForms.ToolStripMenuItem("Режим: весь экран", null, (_, _) => { Settings.Mode = CaptureMode.FullScreen; SettingsChanged(); });
            _miModeWindow = new WinForms.ToolStripMenuItem("Режим: активное окно", null, (_, _) => { Settings.Mode = CaptureMode.ActiveWindow; SettingsChanged(); });
            menu.Items.Add(_miStart);
            menu.Items.Add(_miPause);
            menu.Items.Add(_miStop);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_miModeFull);
            menu.Items.Add(_miModeWindow);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("Настройки…", null, (_, _) => ShowSettings()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Открыть папку с записями", null, (_, _) => OpenOutputFolder()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("Выход", null, (_, _) => ExitApp()));
            foreach (WinForms.ToolStripItem item in menu.Items) item.ForeColor = Color.White;

            _tray = new WinForms.NotifyIcon
            {
                Icon = _iconIdle,
                Text = "GlassRecorder",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowSettings(); };
            _tray.BalloonTipClicked += (_, _) => OpenOutputFolder();
            UpdateUi();
        }

        private static Icon MakeIcon(Color dot, bool recording)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var ring = new Pen(Color.FromArgb(235, 255, 255, 255), 3f);
                g.DrawEllipse(ring, 2.5f, 2.5f, 27f, 27f);
                float r = recording ? 20f : 14f;
                using var fill = new SolidBrush(dot);
                g.FillEllipse(fill, 16 - r / 2, 16 - r / 2, r, r);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private sealed class DarkColors : WinForms.ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(32, 32, 36);
            private static readonly Color Hover = Color.FromArgb(60, 60, 68);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Color.FromArgb(70, 70, 78);
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color SeparatorDark => Color.FromArgb(70, 70, 78);
            public override Color SeparatorLight => Color.FromArgb(70, 70, 78);
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color CheckBackground => Hover;
            public override Color CheckSelectedBackground => Hover;
        }

        // ---------------- exit ----------------

        public void ExitApp()
        {
            try
            {
                if (Recorder != null)
                {
                    var rec = Recorder;
                    Recorder = null;
                    _mouse?.Dispose();
                    _panel?.Close();
                    rec.Stop();
                }
            }
            catch (Exception ex) { Log.Write("Exit stop: " + ex.Message); }
            Hotkeys?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            try { MediaFactory.MFShutdown(); } catch { }
            Shutdown();
        }
    }
}
