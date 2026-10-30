using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassRecorder.Core;
using GlassRecorder.Interop;

namespace GlassRecorder.UI
{
    public partial class RecordingPanel : Window
    {
        private readonly App _app;
        private readonly DispatcherTimer _timer;
        private IntPtr _hwnd;

        private static readonly Brush Normal = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Off = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x45, 0x3A));
        private static readonly Brush On = new SolidColorBrush(Color.FromArgb(0x59, 0x34, 0xC7, 0x59));
        private static readonly Brush Lit = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));

        public RecordingPanel(App app)
        {
            _app = app;
            InitializeComponent();
            SourceInitialized += (_, _) => _hwnd = Glass.Apply(this, excludeFromCapture: true, toolWindow: true, noActivate: true);
            ContentRendered += (_, _) => PlaceTopCenter();

            DragArea.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            PauseButton.Click += (_, _) => _app.TogglePause();
            MicButton.Click += (_, _) => _app.ToggleMic();
            ClicksButton.Click += (_, _) => _app.ToggleClicks();
            StopButton.Click += (_, _) => _app.StopRecording();

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Refresh(), Dispatcher);
            _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }

        private void StartPulse()
        {
            var a1 = new DoubleAnimation(1, 1.9, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            var a2 = new DoubleAnimation(0.45, 0, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, a1);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, a1);
            Pulse.BeginAnimation(OpacityProperty, a2);
        }

        private void PlaceTopCenter()
        {
            var screen = System.Windows.Forms.Screen.AllScreens is { Length: > 0 } all
                ? Array.Find(all, s => s.DeviceName == _app.Settings.MonitorDevice) ?? System.Windows.Forms.Screen.PrimaryScreen
                : System.Windows.Forms.Screen.PrimaryScreen;
            if (_hwnd == IntPtr.Zero) _hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)(ActualWidth * dpi.DpiScaleX);
            int x = screen.WorkingArea.Left + (screen.WorkingArea.Width - w) / 2;
            int y = screen.WorkingArea.Top + (int)(14 * dpi.DpiScaleY);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Refresh()
        {
            var rec = _app.Recorder;
            if (rec == null) return;
            if (!rec.IsStarted)
            {
                TimerText.Text = "00:00";
                StateText.Text = "ЗАПУСК…";
                return;
            }
            var t = rec.Elapsed;
            TimerText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

            bool paused = rec.IsPaused;
            StateText.Text = paused ? "ПАУЗА" : $"REC · {rec.EncoderInfo}";
            PauseButton.Content = paused ? "" : "";
            PauseButton.ToolTip = paused ? "Продолжить" : "Пауза";
            Dot.Fill = paused ? new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00)) : (Brush)FindResource("RecordBrush");
            // Blink twice a second instead of a continuous animation: every repaint of an
            // acrylic window makes DWM re-blur what is behind it.
            Pulse.Visibility = Visibility.Collapsed;
            Dot.Opacity = !paused && (DateTime.Now.Millisecond >= 500) ? 0.35 : 1.0;

            bool mic = rec.MicEnabled;
            MicButton.Content = mic ? "" : "";
            MicButton.Background = !rec.HasMic ? Normal : (mic ? Normal : Off);
            MicButton.IsEnabled = rec.HasMic;
            MicButton.ToolTip = !rec.HasMic ? "Микрофон не найден" : (mic ? "Микрофон включён" : "Микрофон выключен");

            ClicksButton.Background = rec.ShowClicks ? On : Normal;
            ClicksButton.ToolTip = rec.ShowClicks ? "Показ кликов: вкл" : "Показ кликов: выкл";

            bool zoom = rec.ZoomHeld;
            ZoomBadge.Background = zoom ? Lit : Normal;
            ZoomGlyph.Foreground = zoom ? Brushes.Black : (Brush)FindResource("TextBrush");
            ZoomBadge.ToolTip = $"Зум ×{_app.Settings.ZoomFactor:0.##}: удерживайте {AppSettings.FormatKeys(_app.Settings.Hotkeys[HotkeyIds.Zoom])}";
        }
    }
}
