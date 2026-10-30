using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using GlassRecorder.Interop;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using AlphaMode = Vortice.DCommon.AlphaMode;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;

namespace GlassRecorder.Core
{
    /// <summary>
    /// Owns one recording: GPU device, capture source(s), compositor (zoom + click circles), encoder and audio.
    /// </summary>
    public sealed class Recorder : IDisposable
    {
        private struct ClickMark { public int X, Y; public bool Left; public long Born; }

        private readonly AppSettings _settings;
        private readonly object _gpuLock = new();
        private readonly RecordingClock _clock = new();
        private readonly ConcurrentQueue<ClickMark> _newClicks = new();
        private readonly List<ClickMark> _clicks = new();
        private readonly Stopwatch _realTime = Stopwatch.StartNew();
        private readonly int _ownPid = Environment.ProcessId;

        private ID3D11Device _device;
        private IDirect3DDevice _winrtDevice;
        private ID2D1Factory1 _d2dFactory;
        private ID2D1Device _d2dDevice;
        private ID2D1DeviceContext _ctx;
        private ID3D11Texture2D _output;
        private ID2D1Bitmap1 _target;
        private ID2D1SolidColorBrush _brush;

        private CaptureSource _current;
        private CaptureSource _pending;
        private IntPtr _monitor;
        private Native.RECT _monitorRect;
        private int _width, _height;

        private MediaWriter _writer;
        private AudioMixer _audio;
        private Thread _renderThread;
        private volatile bool _running;

        private int _fps = 60;
        private string _gpuName = "?";
        private long _lastWriteTime = long.MinValue / 2;
        private RawRectF _lastSrcRect, _lastDest;
        private CaptureSource _lastSource;
        private bool _hadClicks;

        private static bool SameRect(RawRectF a, RawRectF b) =>
            Math.Abs(a.Left - b.Left) < 0.05f && Math.Abs(a.Top - b.Top) < 0.05f &&
            Math.Abs(a.Right - b.Right) < 0.05f && Math.Abs(a.Bottom - b.Bottom) < 0.05f;

        private float _zoom = 1f;
        private Vector2 _zoomFocus;
        private bool _focusInit;

        public string OutputPath { get; private set; }
        public bool IsStarted { get; private set; }
        public bool IsPaused => _clock.IsPaused;
        public TimeSpan Elapsed => TimeSpan.FromTicks(_clock.Now);
        public bool MicEnabled
        {
            get => _audio?.MicEnabled ?? false;
            set { if (_audio != null) _audio.MicEnabled = value; }
        }
        public bool HasMic => _audio?.HasMic ?? false;
        public volatile bool ShowClicks;
        public volatile bool ZoomHeld;
        public string EncoderInfo => _writer == null ? "" : (_writer.Hardware ? "GPU" : "CPU");

        public Recorder(AppSettings settings)
        {
            _settings = settings;
            ShowClicks = settings.ShowClicks;
        }

        public static IntPtr ResolveMonitor(string deviceName)
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                if (s.DeviceName == deviceName)
                    return Native.MonitorFromPoint(new Native.POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);
            }
            var p = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return Native.MonitorFromPoint(new Native.POINT { X = p.Left + p.Width / 2, Y = p.Top + p.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);
        }

        public void Start()
        {
            var timer = Stopwatch.StartNew();
            _fps = _settings.Fps <= 30 ? 30 : 60;
            // ---- monitor & output size ----
            _monitor = ResolveMonitor(_settings.MonitorDevice);
            var screen = FindScreen(_monitor);
            _monitorRect = new Native.RECT { Left = screen.Bounds.Left, Top = screen.Bounds.Top, Right = screen.Bounds.Right, Bottom = screen.Bounds.Bottom };
            _width = _monitorRect.Width & ~1;
            _height = _monitorRect.Height & ~1;

            // ---- GPU ----
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 });
            using (var mt = _device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
            using (var dxgi = _device.QueryInterface<IDXGIDevice>())
            {
                _winrtDevice = CaptureInterop.CreateDirect3DDevice(dxgi);
                using (var adapter = dxgi.GetAdapter()) _gpuName = adapter.Description.Description;
                _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded, DebugLevel.None);
                _d2dDevice = _d2dFactory.CreateDevice(dxgi);
            }
            _ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            _output = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            using (var surface = _output.QueryInterface<IDXGISurface>())
            {
                _target = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            }
            _ctx.Target = _target;
            _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            _brush = _ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

            // ---- first capture source ----
            _current = CreateInitialSource();
            long tCapture = timer.ElapsedMilliseconds;

            // ---- audio ----
            _audio = new AudioMixer(_settings.RecordSystemAudio, _settings.MicOnAtStart);
            bool withAudio = _audio.HasAnySource;
            long tAudio = timer.ElapsedMilliseconds;

            // ---- encoder ----
            Directory.CreateDirectory(_settings.OutputFolder);
            OutputPath = Path.Combine(_settings.OutputFolder, $"Запись {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            int bitrate = Math.Clamp(_settings.BitrateMbps, 1, 200) * 1_000_000;
            int fps = _settings.Fps <= 30 ? 30 : 60;
            if (_settings.HardwareEncoding)
            {
                try { _writer = new MediaWriter(OutputPath, _device, _width, _height, fps, bitrate, withAudio, true); }
                catch (Exception ex)
                {
                    Log.Write("Hardware encoder failed, falling back to CPU: " + ex.Message);
                    TryDelete(OutputPath);
                }
            }
            _writer ??= new MediaWriter(OutputPath, _device, _width, _height, fps, bitrate, withAudio, false);
            Log.Write($"Recording on {_gpuName}: {_width}x{_height}@{fps} {(_writer.Hardware ? "GPU" : "CPU")} audio={withAudio} -> {OutputPath} " +
                      $"(init: gpu+capture {tCapture} ms, audio {tAudio - tCapture} ms, encoder {timer.ElapsedMilliseconds - tAudio} ms)");

            // ---- go ----
            _running = true;
            _clock.Start();
            if (withAudio) _audio.Start(_writer, _clock);
            _renderThread = new Thread(() => RenderLoop(fps)) { IsBackground = true, Name = "Render", Priority = ThreadPriority.AboveNormal };
            _renderThread.Start();
            IsStarted = true;
        }

        private static System.Windows.Forms.Screen FindScreen(IntPtr monitor)
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var c = new Native.POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
                if (Native.MonitorFromPoint(c, Native.MONITOR_DEFAULTTONEAREST) == monitor) return s;
            }
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        private CaptureSource CreateInitialSource()
        {
            if (_settings.Mode == CaptureMode.ActiveWindow)
            {
                var hwnd = PickForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    try { return new CaptureSource(_device, _winrtDevice, _gpuLock, hwnd, IntPtr.Zero, _settings.ShowCursor, _fps); }
                    catch (Exception ex) { Log.Write("Window capture failed: " + ex.Message); }
                }
            }
            return new CaptureSource(_device, _winrtDevice, _gpuLock, IntPtr.Zero, _monitor, _settings.ShowCursor, _fps);
        }

        private static readonly HashSet<string> IgnoredClasses = new()
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
            "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "Xaml_WindowedPopupClass", "#32768", "tooltips_class32",
        };

        /// <summary>
        /// Returns the window that should be recorded in active-window mode:
        /// a real window, IntPtr.Zero for "desktop" (record the monitor), or -1 for "keep the current one".
        /// </summary>
        private IntPtr PickForegroundWindow()
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return new IntPtr(-1);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return new IntPtr(-1);
            string cls = Native.GetClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW") return IntPtr.Zero;
            if (IgnoredClasses.Contains(cls)) return new IntPtr(-1);
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsCloaked(hwnd)) return new IntPtr(-1);
            var r = Native.GetVisibleBounds(hwnd);
            if (r.Width < 64 || r.Height < 64) return new IntPtr(-1);
            return hwnd;
        }

        private void UpdateWindowTarget()
        {
            if (_settings.Mode != CaptureMode.ActiveWindow) return;

            if (_pending != null)
            {
                lock (_gpuLock) _pending.TakeNewFrame();
                if (_pending.HasFrame)
                {
                    var old = _current;
                    _current = _pending;
                    _pending = null;
                    old?.Dispose();
                }
                else return; // still waiting for the first frame of the new window
            }

            var target = PickForegroundWindow();
            if (_current != null && _current.Window != IntPtr.Zero && (_current.IsClosed || !Native.IsWindow(_current.Window)))
                target = target == new IntPtr(-1) ? IntPtr.Zero : target;
            if (target == new IntPtr(-1)) return;

            bool same = target == IntPtr.Zero
                ? _current != null && _current.Window == IntPtr.Zero
                : _current != null && _current.Window == target;
            if (same) return;

            try
            {
                _pending = target == IntPtr.Zero
                    ? new CaptureSource(_device, _winrtDevice, _gpuLock, IntPtr.Zero, _monitor, _settings.ShowCursor, _fps)
                    : new CaptureSource(_device, _winrtDevice, _gpuLock, target, IntPtr.Zero, _settings.ShowCursor, _fps);
            }
            catch (Exception ex)
            {
                Log.Write("Switch capture failed: " + ex.Message);
                _pending = null;
            }
        }

        public void AddClick(int x, int y, bool left)
        {
            if (!_running || !ShowClicks || _clock.IsPaused) return;
            _newClicks.Enqueue(new ClickMark { X = x, Y = y, Left = left, Born = _realTime.ElapsedMilliseconds });
        }

        public void Pause() => _clock.Pause();
        public void Resume() => _clock.Resume();

        private void RenderLoop(int fps)
        {
            Native.timeBeginPeriod(1);
            try
            {
                long frameTicks = 10_000_000 / fps;
                var sw = Stopwatch.StartNew();
                long next = 0;
                long lastWindowCheck = -1_000_000_000;
                double lastFrameMs = 0;

                while (_running)
                {
                    long now = sw.Elapsed.Ticks;
                    if (now < next)
                    {
                        long waitMs = (next - now) / 10_000;
                        Thread.Sleep(waitMs > 1 ? (int)(waitMs - 1) : 1); // never busy-spin
                        continue;
                    }
                    next += frameTicks;
                    if (now - next > frameTicks * 4) next = now + frameTicks; // fell behind: skip ahead

                    if (now - lastWindowCheck > 1_500_000) // every 150 ms
                    {
                        lastWindowCheck = now;
                        UpdateWindowTarget();
                    }

                    double ms = sw.Elapsed.TotalMilliseconds;
                    float dt = (float)Math.Min(0.1, (ms - lastFrameMs) / 1000.0);
                    lastFrameMs = ms;

                    if (_clock.IsPaused) continue;

                    try
                    {
                        // Nothing changed on screen → skip drawing, colour conversion and encoding.
                        // A keep-alive frame still goes out every second.
                        long t = _clock.Now;
                        bool keepAlive = t - _lastWriteTime > 10_000_000;
                        if (!Compose(dt, keepAlive)) continue;
                        _writer.WriteVideo(_output, t, frameTicks);
                        _lastWriteTime = t;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Render error: " + ex);
                        Thread.Sleep(50);
                    }
                }
            }
            finally { Native.timeEndPeriod(1); }
        }

        /// <summary>Draws the current frame (with zoom and click marks) into the output texture.</summary>
        private bool Compose(float dt, bool force)
        {
            while (_newClicks.TryDequeue(out var c)) _clicks.Add(c);
            long nowMs = _realTime.ElapsedMilliseconds;
            _clicks.RemoveAll(c => nowMs - c.Born > ClickLifeMs);

            lock (_gpuLock)
            {
                var src = _current;
                if (src == null || src.Disposed) return false;
                bool newFrame = src.TakeNewFrame();
                if (!src.HasFrame) return false;
                var bitmap = src.GetBitmap(_ctx);
                if (bitmap == null) return false;

                float fw = src.FrameWidth, fh = src.FrameHeight;

                // Screen-space origin of the captured frame (to map the cursor and clicks into it).
                float originX, originY;
                if (src.Window != IntPtr.Zero)
                {
                    var r = Native.GetVisibleBounds(src.Window);
                    originX = r.Left; originY = r.Top;
                }
                else { originX = _monitorRect.Left; originY = _monitorRect.Top; }

                // Destination rectangle: fill for monitors, fit with a margin for windows.
                RawRectF dest;
                if (src.Window == IntPtr.Zero && Math.Abs(fw - _width) < 4 && Math.Abs(fh - _height) < 4)
                {
                    dest = new RawRectF(0, 0, _width, _height);
                }
                else
                {
                    float margin = src.Window != IntPtr.Zero ? 0.03f : 0f;
                    float availW = _width * (1 - 2 * margin), availH = _height * (1 - 2 * margin);
                    float s = Math.Min(availW / fw, availH / fh);
                    float dw = fw * s, dh = fh * s;
                    float dx = (_width - dw) / 2, dy = (_height - dh) / 2;
                    dest = new RawRectF(dx, dy, dx + dw, dy + dh);
                }

                // ---- zoom ----
                Native.GetCursorPos(out var cur);
                var cursor = new Vector2(cur.X - originX, cur.Y - originY);
                cursor = Vector2.Clamp(cursor, Vector2.Zero, new Vector2(fw, fh));
                float targetZoom = ZoomHeld ? (float)Math.Clamp(_settings.ZoomFactor, 1.1, 6.0) : 1f;
                float k = 1f - MathF.Exp(-dt * 10f);
                _zoom += (targetZoom - _zoom) * k;
                if (Math.Abs(_zoom - targetZoom) < 0.002f) _zoom = targetZoom;
                if (!_focusInit) { _zoomFocus = cursor; _focusInit = true; }
                _zoomFocus += (cursor - _zoomFocus) * (1f - MathF.Exp(-dt * 14f));

                float sw = fw / _zoom, sh = fh / _zoom;
                float sx = Math.Clamp(_zoomFocus.X - sw / 2, 0, fw - sw);
                float sy = Math.Clamp(_zoomFocus.Y - sh / 2, 0, fh - sh);
                var srcRect = new RawRectF(sx, sy, sx + sw, sy + sh);

                bool moved = !SameRect(srcRect, _lastSrcRect) || !SameRect(dest, _lastDest) || !ReferenceEquals(src, _lastSource);
                bool marks = _clicks.Count > 0 || _hadClicks;
                _hadClicks = _clicks.Count > 0;
                if (!newFrame && !moved && !marks && !force) return false;
                _lastSrcRect = srcRect; _lastDest = dest; _lastSource = src;

                _ctx.BeginDraw();
                _ctx.Clear(src.Window != IntPtr.Zero ? new Color4(0.07f, 0.075f, 0.09f, 1f) : new Color4(0, 0, 0, 1));
                _ctx.DrawBitmap(bitmap, dest, 1f, InterpolationMode.Linear, srcRect, null);

                // ---- click marks ----
                if (_clicks.Count > 0)
                {
                    float scale = (dest.Right - dest.Left) / sw;
                    foreach (var c in _clicks)
                    {
                        float px = (c.X - originX - sx) * scale + dest.Left;
                        float py = (c.Y - originY - sy) * scale + dest.Top;
                        float age = (nowMs - c.Born) / (float)ClickLifeMs;          // 0..1
                        float ease = 1f - (1f - age) * (1f - age);
                        float radius = (16f + 22f * ease) * Math.Max(1f, _height / 1080f);
                        float alpha = 1f - age;
                        var color = c.Left ? new Color4(1f, 0.23f, 0.19f, 1f) : new Color4(0.2f, 0.85f, 0.35f, 1f);

                        _brush.Color = new Color4(color.R, color.G, color.B, 0.28f * alpha);
                        _ctx.FillEllipse(new Ellipse(new Vector2(px, py), radius, radius), _brush);
                        _brush.Color = new Color4(color.R, color.G, color.B, 0.95f * alpha);
                        _ctx.DrawEllipse(new Ellipse(new Vector2(px, py), radius, radius), _brush, 3.5f * Math.Max(1f, _height / 1080f));
                    }
                }

                var hr = _ctx.EndDraw();
                if (hr.Failure) { Log.Write("EndDraw failed: " + hr); return false; }
            }
            return true;
        }

        private const int ClickLifeMs = 650;

        /// <summary>Stops everything and finalizes the MP4. Blocking; call off the UI thread.</summary>
        public string Stop()
        {
            if (!_running) return OutputPath;
            _running = false;
            _renderThread?.Join(3000);
            try { _audio?.Stop(); } catch (Exception ex) { Log.Write("Audio stop: " + ex.Message); }
            try { _writer?.Finish(); } catch (Exception ex) { Log.Write("Finish: " + ex.Message); }
            Dispose();
            Log.Write("Saved " + OutputPath);
            return OutputPath;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose()
        {
            _running = false;
            try { _pending?.Dispose(); } catch { }
            try { _current?.Dispose(); } catch { }
            _pending = _current = null;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _brush?.Dispose(); _brush = null;
            _target?.Dispose(); _target = null;
            _ctx?.Dispose(); _ctx = null;
            _d2dDevice?.Dispose(); _d2dDevice = null;
            _d2dFactory?.Dispose(); _d2dFactory = null;
            _output?.Dispose(); _output = null;
            (_winrtDevice as IDisposable)?.Dispose(); _winrtDevice = null;
            _device?.Dispose(); _device = null;
        }
    }
}
