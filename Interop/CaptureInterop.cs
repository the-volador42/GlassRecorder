using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GlassRecorder.Interop
{
    /// <summary>Bridges WinRT Graphics Capture objects and raw D3D11 (Vortice) objects.</summary>
    internal static unsafe class CaptureInterop
    {
        private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static IntPtr GetItemInterop()
        {
            const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out IntPtr hs));
            try
            {
                Guid iid = IID_IGraphicsCaptureItemInterop;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out IntPtr f));
                return f;
            }
            finally { WindowsDeleteString(hs); }
        }

        private static GraphicsCaptureItem CreateItem(IntPtr handle, int slot)
        {
            IntPtr interop = GetItemInterop();
            try
            {
                IntPtr* vtbl = *(IntPtr**)interop;
                Guid iid = IID_IGraphicsCaptureItem;
                IntPtr result;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot])(interop, handle, &iid, &result);
                Marshal.ThrowExceptionForHR(hr);
                var item = GraphicsCaptureItem.FromAbi(result);
                Marshal.Release(result);
                return item;
            }
            finally { Marshal.Release(interop); }
        }

        public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd) => CreateItem(hwnd, 3);
        public static GraphicsCaptureItem CreateForMonitor(IntPtr hmon) => CreateItem(hmon, 4);

        public static IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr ptr));
            var device = MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
            Marshal.Release(ptr);
            return device;
        }

        public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
        {
            IntPtr abi = ((IWinRTObject)surface).NativeObject.ThisPtr;
            Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, ref accessIid, out IntPtr access));
            try
            {
                IntPtr* vtbl = *(IntPtr**)access;
                Guid texIid = IID_ID3D11Texture2D;
                IntPtr tex;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3])(access, &texIid, &tex);
                Marshal.ThrowExceptionForHR(hr);
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
    }
}
