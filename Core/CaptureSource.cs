using System;
using GlassRecorder.Interop;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace GlassRecorder.Core
{
    /// <summary>
    /// One Windows.Graphics.Capture session (a monitor or a window).
    /// Keeps a private copy of the most recent frame so the renderer can draw it at any time.
    /// </summary>
    internal sealed class CaptureSource : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly IDirect3DDevice _winrtDevice;
        private readonly object _gpuLock;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private Windows.Graphics.SizeInt32 _poolSize;

        private ID3D11Texture2D _latest;
        private Direct3D11CaptureFrame _held;           // newest frame not yet copied
        private readonly object _heldLock = new();
        private ID2D1Bitmap1 _bitmap;
        private ID3D11Texture2D _bitmapTexture;

        public IntPtr Window { get; }
        public IntPtr Monitor { get; }
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public bool HasFrame => _latest != null;
        public bool IsClosed { get; private set; }
        public bool Disposed { get; private set; }

        public CaptureSource(ID3D11Device device, IDirect3DDevice winrtDevice, object gpuLock,
                             IntPtr window, IntPtr monitor, bool showCursor, int fps)
        {
            _device = device;
            _winrtDevice = winrtDevice;
            _gpuLock = gpuLock;
            Window = window;
            Monitor = monitor;

            _item = window != IntPtr.Zero ? CaptureInterop.CreateForWindow(window) : CaptureInterop.CreateForMonitor(monitor);
            _item.Closed += (_, _) => IsClosed = true;
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = showCursor; } catch { }
            try { _session.IsBorderRequired = false; } catch { }
            // Windows 11 24H2+: don't let DWM produce capture frames faster than we record (240 Hz screens!).
            try { _session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps)); } catch { }
            _session.StartCapture();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            // Cheap: only keep a reference to the newest frame. The copy happens at the recording
            // frame rate in TakeNewFrame(), not at the monitor refresh rate.
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame == null) return;
                if (Disposed) { frame.Dispose(); return; }
                var content = frame.ContentSize;
                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    lock (_heldLock) { _held?.Dispose(); _held = null; }
                    frame.Dispose();
                    _poolSize = content;
                    sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, content);
                    return;
                }
                lock (_heldLock)
                {
                    _held?.Dispose();
                    _held = frame;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Frame error: " + ex.Message);
            }
        }

        /// <summary>
        /// Copies the newest captured frame (if any) into the private texture.
        /// Returns true when the picture changed. Call only while holding the GPU lock.
        /// </summary>
        public bool TakeNewFrame()
        {
            Direct3D11CaptureFrame frame;
            lock (_heldLock) { frame = _held; _held = null; }
            if (frame == null || Disposed) { frame?.Dispose(); return false; }
            try
            {
                var content = frame.ContentSize;
                using var tex = CaptureInterop.GetTexture(frame.Surface);
                var desc = tex.Description;
                int w = (int)Math.Min((uint)Math.Max(1, content.Width), desc.Width);
                int h = (int)Math.Min((uint)Math.Max(1, content.Height), desc.Height);
                if (_latest == null || FrameWidth != w || FrameHeight != h)
                {
                    _latest?.Dispose();
                    _latest = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w,
                        Height = (uint)h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                    });
                    FrameWidth = w;
                    FrameHeight = h;
                }
                _device.ImmediateContext.CopySubresourceRegion(_latest, 0, 0, 0, 0, tex, 0, new Box(0, 0, 0, w, h, 1));
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Frame copy error: " + ex.Message);
                return false;
            }
            finally { frame.Dispose(); }
        }

        /// <summary>Returns a D2D bitmap of the latest frame. Call only while holding the GPU lock.</summary>
        public ID2D1Bitmap1 GetBitmap(ID2D1DeviceContext ctx)
        {
            if (_latest == null) return null;
            if (_bitmap == null || !ReferenceEquals(_bitmapTexture, _latest))
            {
                _bitmap?.Dispose();
                using var surface = _latest.QueryInterface<IDXGISurface>();
                _bitmap = ctx.CreateBitmapFromDxgiSurface(surface,
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96, BitmapOptions.None));
                _bitmapTexture = _latest;
            }
            return _bitmap;
        }

        public void Dispose()
        {
            lock (_gpuLock)
            {
                if (Disposed) return;
                Disposed = true;
                _bitmap?.Dispose(); _bitmap = null;
                _latest?.Dispose(); _latest = null;
            }
            lock (_heldLock) { _held?.Dispose(); _held = null; }
            try { _pool.FrameArrived -= OnFrameArrived; } catch { }
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null; _pool = null; _item = null;
        }
    }
}
