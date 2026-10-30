using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GlassRecorder.Core
{
    /// <summary>Recording time that stops while paused. Units: 100 ns.</summary>
    internal sealed class RecordingClock
    {
        private readonly Stopwatch _sw = new();
        private readonly object _sync = new();
        private long _pausedTotal, _pauseStart;
        private bool _paused;

        public void Start() => _sw.Start();
        public bool IsPaused { get { lock (_sync) return _paused; } }

        public long Now
        {
            get
            {
                lock (_sync)
                {
                    long t = _paused ? _pauseStart : _sw.Elapsed.Ticks;
                    return t - _pausedTotal;
                }
            }
        }

        public void Pause()
        {
            lock (_sync) { if (!_paused) { _pauseStart = _sw.Elapsed.Ticks; _paused = true; } }
        }

        public void Resume()
        {
            lock (_sync) { if (_paused) { _pausedTotal += _sw.Elapsed.Ticks - _pauseStart; _paused = false; } }
        }
    }

    /// <summary>
    /// Captures system audio (WASAPI loopback) and the microphone, mixes them to 48 kHz stereo
    /// and feeds the MP4 writer, paced by the recording clock.
    /// </summary>
    internal sealed class AudioMixer : IDisposable
    {
        private sealed class Input : IDisposable
        {
            public WasapiCapture Capture;
            public BufferedWaveProvider Buffer;
            public ISampleProvider Output;
            public float[] Scratch = Array.Empty<float>();

            public void Dispose()
            {
                try { Capture?.StopRecording(); } catch { }
                try { Capture?.Dispose(); } catch { }
            }
        }

        private const int Rate = MediaWriter.AudioSampleRate;
        private const int LatencyFrames = Rate / 20;      // 50 ms safety margin for device jitter
        private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(300);

        private readonly Input _system;
        private readonly Input _mic;
        private Thread _thread;
        private volatile bool _running;
        private long _writtenFrames;
        private MediaWriter _writer;
        private RecordingClock _clock;

        public volatile bool MicEnabled;
        public bool HasSystem => _system != null;
        public bool HasMic => _mic != null;
        public bool HasAnySource => _system != null || _mic != null;

        public AudioMixer(bool systemAudio, bool micEnabled)
        {
            MicEnabled = micEnabled;
            if (systemAudio)
            {
                try { _system = MakeInput(new WasapiLoopbackCapture()); }
                catch (Exception ex) { Log.Write("System audio unavailable: " + ex.Message); }
            }
            try
            {
                var enumerator = new MMDeviceEnumerator();
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                    _mic = MakeInput(new WasapiCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)));
            }
            catch (Exception ex) { Log.Write("Microphone unavailable: " + ex.Message); }
        }

        private static Input MakeInput(WasapiCapture capture)
        {
            var fmt = capture.WaveFormat;
            var buffer = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            ISampleProvider sp = buffer.ToSampleProvider();
            if (fmt.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
            else if (fmt.Channels > 2) sp = new MultiplexingSampleProvider(new[] { sp }, 2);
            if (fmt.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
            return new Input { Capture = capture, Buffer = buffer, Output = sp };
        }

        public void Start(MediaWriter writer, RecordingClock clock)
        {
            _writer = writer;
            _clock = clock;
            _running = true;
            try { _system?.Capture.StartRecording(); } catch (Exception ex) { Log.Write("Loopback start: " + ex.Message); }
            try { _mic?.Capture.StartRecording(); } catch (Exception ex) { Log.Write("Mic start: " + ex.Message); }
            _thread = new Thread(Loop) { IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            float[] mix = Array.Empty<float>();
            byte[] pcm = Array.Empty<byte>();
            while (_running)
            {
                Thread.Sleep(10);
                try
                {
                    if (_clock.IsPaused)
                    {
                        _system?.Buffer.ClearBuffer();
                        _mic?.Buffer.ClearBuffer();
                        continue;
                    }

                    long target = _clock.Now * Rate / 10_000_000 - LatencyFrames;
                    long frames = target - _writtenFrames;
                    if (frames <= 0) continue;
                    if (frames > Rate) frames = Rate;
                    int samples = (int)frames * 2;

                    if (mix.Length < samples) { mix = new float[samples]; pcm = new byte[samples * 2]; }
                    Array.Clear(mix, 0, samples);

                    AddInput(_system, mix, samples, true);
                    AddInput(_mic, mix, samples, MicEnabled);

                    for (int i = 0; i < samples; i++)
                    {
                        float v = mix[i];
                        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                        short s = (short)(v * 32767f);
                        pcm[i * 2] = (byte)s;
                        pcm[i * 2 + 1] = (byte)(s >> 8);
                    }

                    long time = _writtenFrames * 10_000_000 / Rate;
                    long duration = frames * 10_000_000 / Rate;
                    _writer.WriteAudio(pcm, samples * 2, time, duration);
                    _writtenFrames += frames;
                }
                catch (Exception ex)
                {
                    Log.Write("Audio loop: " + ex.Message);
                    Thread.Sleep(100);
                }
            }
        }

        private static void AddInput(Input input, float[] mix, int samples, bool audible)
        {
            if (input == null) return;
            // Keep latency bounded if the device clock runs faster than ours.
            if (input.Buffer.BufferedDuration > MaxBuffered) input.Buffer.ClearBuffer();
            if (input.Scratch.Length < samples) input.Scratch = new float[samples];
            int read = input.Output.Read(input.Scratch, 0, samples);
            if (!audible) return;
            for (int i = 0; i < read; i++) mix[i] += input.Scratch[i];
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(2000);
            _system?.Dispose();
            _mic?.Dispose();
        }

        public void Dispose() => Stop();
    }
}
