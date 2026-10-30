using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassRecorder.Core;
using WinForms = System.Windows.Forms;
using CaptureMode = GlassRecorder.Core.CaptureMode;

namespace GlassRecorder.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly App _app;
        private AppSettings S => _app.Settings;
        private bool _loading;

        // hotkey capture state
        private string _captureId;
        private Button _captureChip;
        private readonly List<int> _captureCombo = new();
        private readonly HashSet<int> _captureDown = new();

        public SettingsWindow(App app)
        {
            _app = app;
            InitializeComponent();
            SourceInitialized += (_, _) => Glass.Apply(this, excludeFromCapture: true);

            Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            CloseButton.Click += (_, _) => Hide();
            MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
            Closing += (s, e) => { e.Cancel = true; Hide(); };
            Deactivated += (_, _) => CancelCapture();
            IsVisibleChanged += (_, _) => { if (!IsVisible) CancelCapture(); };

            LoadValues();
            Wire();
        }

        private void LoadValues()
        {
            _loading = true;
            ModeFull.IsChecked = S.Mode == CaptureMode.FullScreen;
            ModeWindow.IsChecked = S.Mode == CaptureMode.ActiveWindow;
            Fps30.IsChecked = S.Fps <= 30;
            Fps60.IsChecked = S.Fps > 30;
            BitrateSlider.Value = Math.Clamp(S.BitrateMbps, 4, 60);
            HwSwitch.IsChecked = S.HardwareEncoding;
            CursorSwitch.IsChecked = S.ShowCursor;
            SysAudioSwitch.IsChecked = S.RecordSystemAudio;
            MicSwitch.IsChecked = S.MicOnAtStart;
            ClicksSwitch.IsChecked = S.ShowClicks;
            ZoomSlider.Value = Math.Clamp(S.ZoomFactor, 1.5, 4);
            FolderText.Text = S.OutputFolder;
            BuildMonitors();
            BuildHotkeys();
            UpdateTexts();
            _loading = false;
        }

        private void BuildMonitors()
        {
            MonitorPanel.Children.Clear();
            var screens = WinForms.Screen.AllScreens;
            if (string.IsNullOrEmpty(S.MonitorDevice) || screens.All(s => s.DeviceName != S.MonitorDevice))
                S.MonitorDevice = WinForms.Screen.PrimaryScreen.DeviceName;
            int i = 1;
            foreach (var screen in screens)
            {
                var rb = new RadioButton
                {
                    GroupName = "monitor",
                    Style = (Style)FindResource("Segment"),
                    Content = $"{i}{(screen.Primary ? " · основной" : "")}  {screen.Bounds.Width}×{screen.Bounds.Height}",
                    IsChecked = screen.DeviceName == S.MonitorDevice,
                    Tag = screen.DeviceName,
                };
                rb.Checked += (s, _) => { if (_loading) return; S.MonitorDevice = (string)((RadioButton)s).Tag; Save(); };
                MonitorPanel.Children.Add(rb);
                i++;
            }
        }

        private void BuildHotkeys()
        {
            HotkeyList.Children.Clear();
            foreach (var id in HotkeyIds.All)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = HotkeyIds.Title(id), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                var chip = new Button
                {
                    Style = (Style)FindResource("KeyChip"),
                    Content = AppSettings.FormatKeys(S.Hotkeys.TryGetValue(id, out var k) ? k : null),
                    Tag = id,
                };
                chip.Click += (s, _) => BeginCapture((Button)s);
                Grid.SetColumn(chip, 1);
                row.Children.Add(label);
                row.Children.Add(chip);
                HotkeyList.Children.Add(row);
            }
        }

        private void Wire()
        {
            ModeFull.Checked += (_, _) => { if (_loading) return; S.Mode = CaptureMode.FullScreen; Save(); };
            ModeWindow.Checked += (_, _) => { if (_loading) return; S.Mode = CaptureMode.ActiveWindow; Save(); };
            Fps30.Checked += (_, _) => { if (_loading) return; S.Fps = 30; Save(); };
            Fps60.Checked += (_, _) => { if (_loading) return; S.Fps = 60; Save(); };
            BitrateSlider.ValueChanged += (_, _) => { if (_loading) return; S.BitrateMbps = (int)BitrateSlider.Value; Save(); };
            ZoomSlider.ValueChanged += (_, _) => { if (_loading) return; S.ZoomFactor = Math.Round(ZoomSlider.Value, 2); Save(); };
            HwSwitch.Click += (_, _) => { S.HardwareEncoding = HwSwitch.IsChecked == true; Save(); };
            CursorSwitch.Click += (_, _) => { S.ShowCursor = CursorSwitch.IsChecked == true; Save(); };
            SysAudioSwitch.Click += (_, _) => { S.RecordSystemAudio = SysAudioSwitch.IsChecked == true; Save(); };
            MicSwitch.Click += (_, _) => { S.MicOnAtStart = MicSwitch.IsChecked == true; Save(); };
            ClicksSwitch.Click += (_, _) => { S.ShowClicks = ClicksSwitch.IsChecked == true; Save(); };

            ChangeFolderButton.Click += (_, _) =>
            {
                using var dlg = new WinForms.FolderBrowserDialog
                {
                    Description = "Куда сохранять записи",
                    UseDescriptionForTitle = true,
                    SelectedPath = Directory.Exists(S.OutputFolder) ? S.OutputFolder : "",
                };
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    S.OutputFolder = dlg.SelectedPath;
                    FolderText.Text = S.OutputFolder;
                    Save();
                }
            };
            OpenFolderButton.Click += (_, _) => _app.OpenOutputFolder();
            ResetKeysButton.Click += (_, _) =>
            {
                S.Hotkeys = AppSettings.DefaultHotkeys();
                Save();
                BuildHotkeys();
            };
            RecordButton.Click += (_, _) =>
            {
                if (_app.IsRecording) _app.StopRecording();
                else { Hide(); _app.StartRecording(); }
            };
        }

        private void Save()
        {
            UpdateTexts();
            _app.SettingsChanged();
        }

        private void UpdateTexts()
        {
            BitrateText.Text = $"{(int)BitrateSlider.Value} Мбит/с";
            ZoomText.Text = $"×{ZoomSlider.Value:0.##}";
            ModeHint.Text = S.Mode == CaptureMode.FullScreen
                ? "Пишется весь монитор целиком, что бы на нём ни было открыто."
                : "Пишется только окно, которое сейчас активно. Переключились на другое окно — запись переходит на него.";
            FooterHint.Text = $"Старт: {AppSettings.FormatKeys(S.Hotkeys[HotkeyIds.Start])}   ·   Стоп: {AppSettings.FormatKeys(S.Hotkeys[HotkeyIds.Stop])}";
        }

        public void UpdateState(bool recording, bool paused)
        {
            RecordButtonText.Text = recording ? "Остановить запись" : "Начать запись";
            RecordGlyph.CornerRadius = new CornerRadius(recording ? 2 : 6);
            StatusText.Text = recording ? (paused ? "Пауза" : "Идёт запись…") : "Готов к записи · живёт в трее";
        }

        // ---------------- hotkey capture ----------------

        private void BeginCapture(Button chip)
        {
            CancelCapture();
            _captureId = (string)chip.Tag;
            _captureChip = chip;
            _captureCombo.Clear();
            _captureDown.Clear();
            chip.Content = "Нажмите клавиши…";
            chip.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x45, 0x3A));
            _app.Hotkeys.Capture = OnCaptureKey;
        }

        private void OnCaptureKey(int vk, bool down)
        {
            if (_captureId == null) return;
            if (down)
            {
                if (vk == 0x1B && _captureCombo.Count == 0) { CancelCapture(); return; } // Esc
                _captureDown.Add(vk);
                if (!_captureCombo.Contains(vk)) _captureCombo.Add(vk);
                _captureChip.Content = AppSettings.FormatKeys(_captureCombo) + " …";
            }
            else
            {
                _captureDown.Remove(vk);
                if (_captureDown.Count == 0 && _captureCombo.Count > 0) FinishCapture();
            }
        }

        private void FinishCapture()
        {
            var combo = _captureCombo.ToList();
            string id = _captureId;
            bool plainSingle = combo.Count == 1 && ((combo[0] >= 'A' && combo[0] <= 'Z') || (combo[0] >= '0' && combo[0] <= '9') || combo[0] == 0x20);
            EndCaptureUi();
            if (plainSingle)
            {
                _app.ShowToast("Одна буква мешала бы печатать текст. Добавьте к ней F-клавишу, Ctrl или Alt.");
                return;
            }
            // Remove the same combo from other actions to avoid conflicts.
            foreach (var other in HotkeyIds.All)
            {
                if (other != id && S.Hotkeys.TryGetValue(other, out var keys) && keys.Count == combo.Count && keys.All(combo.Contains))
                    S.Hotkeys[other] = new List<int>();
            }
            S.Hotkeys[id] = combo;
            Save();
            BuildHotkeys();
        }

        private void CancelCapture()
        {
            if (_captureId == null) return;
            EndCaptureUi();
            BuildHotkeys();
        }

        private void EndCaptureUi()
        {
            _app.Hotkeys.Capture = null;
            _captureId = null;
            _captureChip = null;
        }
    }
}
