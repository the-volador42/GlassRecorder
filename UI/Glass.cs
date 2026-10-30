using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassRecorder.Interop;

namespace GlassRecorder.UI
{
    /// <summary>Turns a WPF window into a blurred, translucent "glass" window using the Windows 11 acrylic backdrop.</summary>
    internal static class Glass
    {
        public static IntPtr Apply(Window window, bool excludeFromCapture, bool toolWindow = false, bool noActivate = false)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;

            int dark = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            int corner = 2; // round
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            int backdrop = 3; // acrylic (transient)
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Hide the native caption buttons that would otherwise shine through the glass.
            long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
            Native.SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x80000L)); // WS_SYSMENU

            if (toolWindow || noActivate)
            {
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if (toolWindow) ex |= Native.WS_EX_TOOLWINDOW;
                if (noActivate) ex |= Native.WS_EX_NOACTIVATE;
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            }

            // Our own UI never shows up in the recording.
            // (GR_SHOW_UI=1 keeps it visible to screenshots, handy for UI testing.)
            if (excludeFromCapture && Environment.GetEnvironmentVariable("GR_SHOW_UI") != "1")
                Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            return hwnd;
        }
    }
}
