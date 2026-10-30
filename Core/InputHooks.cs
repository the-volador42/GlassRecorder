using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using GlassRecorder.Interop;

namespace GlassRecorder.Core
{
    /// <summary>
    /// Global low-level keyboard hook that supports chords of any keys (e.g. F3+F2),
    /// including "hold" chords that report press and release.
    /// Must be created on a thread with a message loop (the WPF UI thread).
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly Native.HookProc _proc;
        private IntPtr _hook;
        private readonly Dispatcher _dispatcher;
        private readonly HashSet<int> _down = new();
        private readonly HashSet<string> _active = new();
        private readonly HashSet<int> _swallowed = new();
        private Dictionary<string, List<int>> _bindings = new();

        public event Action<string> Pressed;
        public event Action<string> Released;

        /// <summary>When set, every key event goes here (and is swallowed) instead of matching hotkeys.</summary>
        public Action<int, bool> Capture { get; set; }

        public HotkeyManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _proc = HookCallback;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Write("Keyboard hook failed: " + Marshal.GetLastWin32Error());
        }

        public void SetBindings(Dictionary<string, List<int>> bindings)
        {
            _bindings = bindings.Where(b => b.Value != null && b.Value.Count > 0)
                .ToDictionary(b => b.Key, b => b.Value.Select(Normalize).Distinct().ToList());
            _active.Clear();
        }

        public static int Normalize(int vk) => vk switch
        {
            0xA0 or 0xA1 => 0x10, // shift
            0xA2 or 0xA3 => 0x11, // ctrl
            0xA4 or 0xA5 => 0x12, // alt
            0x5C => 0x5B,         // win
            _ => vk
        };

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            try
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool isDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool isUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                int vk = Normalize((int)data.vkCode);

                // Ignore keys injected by software (e.g. our own or remote tools)
                bool injected = (data.flags & 0x10) != 0;

                if (Capture != null && !injected)
                {
                    var cap = Capture;
                    _dispatcher.BeginInvoke(() => cap(vk, isDown));
                    return (IntPtr)1;
                }

                if (isDown)
                {
                    // Drop keys we think are held but really are not (missed key-ups).
                    _down.RemoveWhere(k => k != vk && (Native.GetAsyncKeyState(k) & 0x8000) == 0);
                    _down.Add(vk);
                    bool swallow = false;
                    foreach (var kv in _bindings)
                    {
                        if (!kv.Value.Contains(vk)) continue;
                        if (_active.Contains(kv.Key)) { swallow = true; continue; } // auto-repeat
                        if (kv.Value.Count == _down.Count && kv.Value.All(_down.Contains))
                        {
                            _active.Add(kv.Key);
                            swallow = true;
                            string id = kv.Key;
                            _dispatcher.BeginInvoke(() => Pressed?.Invoke(id));
                        }
                    }
                    if (swallow)
                    {
                        _swallowed.Add(vk);
                        return (IntPtr)1;
                    }
                }
                else if (isUp)
                {
                    _down.Remove(vk);
                    foreach (var id in _active.ToList())
                    {
                        if (_bindings.TryGetValue(id, out var keys) && keys.Contains(vk))
                        {
                            _active.Remove(id);
                            string rid = id;
                            _dispatcher.BeginInvoke(() => Released?.Invoke(rid));
                        }
                    }
                    if (_swallowed.Remove(vk)) return (IntPtr)1;
                }
            }
            catch (Exception ex) { Log.Write("Key hook error: " + ex.Message); }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    /// <summary>Global low-level mouse hook that reports left/right button presses in physical screen pixels.</summary>
    public sealed class MouseClickHook : IDisposable
    {
        private readonly Native.HookProc _proc;
        private IntPtr _hook;

        /// <summary>Raised on the hook thread: (x, y, isLeft). Handlers must be fast.</summary>
        public event Action<int, int, bool> Click;

        public MouseClickHook()
        {
            _proc = HookCallback;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Write("Mouse hook failed: " + Marshal.GetLastWin32Error());
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN)
                {
                    var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    try { Click?.Invoke(data.pt.X, data.pt.Y, msg == Native.WM_LBUTTONDOWN); } catch { }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }
}
