using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace GlassRecorder.Core
{
    /// <summary>
    /// Writes H.264 video + AAC audio into an MP4 file using the Media Foundation sink writer.
    /// In hardware mode frames stay on the GPU (NVENC / AMF / QuickSync via MF).
    /// </summary>
    internal sealed class MediaWriter : IDisposable
    {
        public const int AudioSampleRate = 48000;
        public const int AudioChannels = 2;

        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid CodecApiRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        private static readonly Guid CodecApiMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        private static readonly Guid CodecApiGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

        private readonly IMFSinkWriter _writer;
        private readonly IMFDXGIDeviceManager _manager;
        private readonly ID3D11Device _device;
        private readonly int _width, _height;
        private readonly int _videoStream;
        private readonly int _audioStream = -1;
        private readonly object _sync = new();
        private ID3D11Texture2D _staging;
        private bool _finished;

        // GPU colour conversion BGRA -> NV12 (hardware path), so the encoder gets its native format.
        private static readonly Guid VideoFormatNV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        private static readonly Guid YuvMatrixKey = new("3e23d450-2c75-4d25-a00e-b91670d12327");
        private const int MF_E_SAMPLEALLOCATOR_EMPTY = unchecked((int)0xC00D4A3E);
        private ID3D11VideoDevice _videoDevice;
        private ID3D11VideoContext _videoContext;
        private ID3D11VideoProcessorEnumerator _vpEnum;
        private ID3D11VideoProcessor _vp;
        private ID3D11VideoProcessorInputView _vpInput;
        private ID3D11Texture2D _vpInputTexture;
        private int _droppedFrames;

        public bool Hardware { get; }
        public bool HasAudio => _audioStream >= 0;

        public MediaWriter(string path, ID3D11Device device, int width, int height, int fps, int bitrate, bool withAudio, bool hardware)
        {
            _device = device;
            _width = width;
            _height = height;
            Hardware = hardware;

            using var attrs = MediaFactory.MFCreateAttributes(4);
            attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            if (hardware)
            {
                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(device);
                attrs.Set(SinkWriterAttributeKeys.D3DManager, _manager);
                InitVideoProcessor(fps);
            }
            _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attrs);

            // ---- video ----
            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
                outType.Set(YuvMatrixKey, 1u);                                   // BT.709
                outType.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);       // 16..235
                MediaFactory.MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                _videoStream = _writer.AddStream(outType);
            }
            using (var inType = MediaFactory.MFCreateMediaType())
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inType.Set(MediaTypeAttributeKeys.Subtype, hardware ? VideoFormatNV12 : VideoFormatGuids.Rgb32);
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                if (hardware)
                {
                    inType.Set(YuvMatrixKey, 1u);
                    inType.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);
                }
                else inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
                MediaFactory.MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                // Encoder settings: VBR around the requested bitrate, a keyframe every 2 seconds.
                using var enc = MediaFactory.MFCreateAttributes(3);
                enc.Set(CodecApiRateControlMode, 2u);           // eAVEncCommonRateControlMode_UnconstrainedVBR
                enc.Set(CodecApiMeanBitRate, (uint)bitrate);
                enc.Set(CodecApiGopSize, (uint)(fps * 2));
                try { _writer.SetInputMediaType(_videoStream, inType, enc); }
                catch (Exception ex)
                {
                    Log.Write("Encoder rejected custom settings, using defaults: " + ex.Message);
                    _writer.SetInputMediaType(_videoStream, inType, null);
                }
            }

            // ---- audio ----
            if (withAudio)
            {
                using (var outType = MediaFactory.MFCreateMediaType())
                {
                    outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
                    outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate);
                    outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u); // 192 kbps
                    _audioStream = _writer.AddStream(outType);
                }
                using (var inType = MediaFactory.MFCreateMediaType())
                {
                    inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
                    inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate);
                    inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(AudioChannels * 2));
                    inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(AudioSampleRate * AudioChannels * 2));
                    _writer.SetInputMediaType(_audioStream, inType, null);
                }
            }

            _writer.BeginWriting();
        }

        /// <summary>Encodes one frame. time/duration are in 100 ns units.</summary>
        public void WriteVideo(ID3D11Texture2D frame, long time, long duration)
        {
            if (_finished) return;
            if (Hardware)
            {
                // Convert to a fresh NV12 texture each frame: the encoder may keep it after WriteSample returns.
                if (_vpInput == null || !ReferenceEquals(_vpInputTexture, frame))
                {
                    _vpInput?.Dispose();
                    _vpInput = _videoDevice.CreateVideoProcessorInputView(frame, _vpEnum, new VideoProcessorInputViewDescription
                    {
                        FourCC = 0,
                        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                    });
                    _vpInputTexture = frame;
                }
                var nv12 = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                });
                try
                {
                    using (var outView = _videoDevice.CreateVideoProcessorOutputView(nv12, _vpEnum, new VideoProcessorOutputViewDescription
                    {
                        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                    }))
                    {
                        var stream = new VideoProcessorStream { Enable = true, InputSurface = _vpInput };
                        _videoContext.VideoProcessorBlt(_vp, outView, 0, 1, new[] { stream }).CheckError();
                    }
                    using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(IID_ID3D11Texture2D, nv12, 0, false);
                    buffer.CurrentLength = _width * _height * 3 / 2;
                    WriteBuffer(_videoStream, buffer, time, duration, isVideo: true);
                }
                finally { nv12.Dispose(); }
            }
            else
            {
                _staging ??= _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
                var ctx = _device.ImmediateContext;
                ctx.CopyResource(_staging, frame);
                var mapped = ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int rowBytes = _width * 4;
                int size = rowBytes * _height;
                using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
                try
                {
                    buffer.Lock(out IntPtr dst, out _, out _);
                    unsafe
                    {
                        byte* s = (byte*)mapped.DataPointer;
                        byte* d = (byte*)dst;
                        for (int y = 0; y < _height; y++)
                            Buffer.MemoryCopy(s + y * (long)mapped.RowPitch, d + y * (long)rowBytes, rowBytes, rowBytes);
                    }
                    buffer.Unlock();
                }
                finally { ctx.Unmap(_staging, 0); }
                buffer.CurrentLength = size;
                WriteBuffer(_videoStream, buffer, time, duration, isVideo: true);
            }
        }

        /// <summary>Writes interleaved 16-bit PCM (48 kHz stereo).</summary>
        public void WriteAudio(byte[] pcm, int count, long time, long duration)
        {
            if (_finished || _audioStream < 0 || count <= 0) return;
            using var buffer = MediaFactory.MFCreateMemoryBuffer(count);
            buffer.Lock(out IntPtr dst, out _, out _);
            Marshal.Copy(pcm, 0, dst, count);
            buffer.Unlock();
            buffer.CurrentLength = count;
            WriteBuffer(_audioStream, buffer, time, duration, isVideo: false);
        }

        private void WriteBuffer(int stream, IMFMediaBuffer buffer, long time, long duration, bool isVideo)
        {
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            // The encoder pipeline can be momentarily full; retry briefly, then drop the video frame.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    lock (_sync)
                    {
                        if (_finished) return;
                        _writer.WriteSample(stream, sample);
                    }
                    return;
                }
                catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == MF_E_SAMPLEALLOCATOR_EMPTY)
                {
                    if (isVideo && attempt >= 3)
                    {
                        if (_droppedFrames++ % 30 == 0) Log.Write($"Encoder busy, dropped frames: {_droppedFrames}");
                        return;
                    }
                    if (attempt >= 50) throw;
                    System.Threading.Thread.Sleep(2);
                }
            }
        }

        private void InitVideoProcessor(int fps)
        {
            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
            _vpEnum = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)fps, 1),
                InputWidth = (uint)_width,
                InputHeight = (uint)_height,
                OutputFrameRate = new Rational((uint)fps, 1),
                OutputWidth = (uint)_width,
                OutputHeight = (uint)_height,
                Usage = VideoUsage.PlaybackNormal,
            });
            _vp = _videoDevice.CreateVideoProcessor(_vpEnum, 0);
            _videoContext.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamColorSpace(_vp, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
            _videoContext.VideoProcessorSetOutputColorSpace(_vp, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        }

        public void Finish()
        {
            lock (_sync)
            {
                if (_finished) return;
                _finished = true;
                _writer.Finalize();
            }
        }

        public void Dispose()
        {
            try { Finish(); } catch (Exception ex) { Log.Write("Finalize failed: " + ex.Message); }
            _writer?.Dispose();
            _manager?.Dispose();
            _staging?.Dispose();
            _vpInput?.Dispose();
            _vp?.Dispose();
            _vpEnum?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
            if (_droppedFrames > 0) Log.Write($"Total dropped frames: {_droppedFrames}");
        }
    }
}
