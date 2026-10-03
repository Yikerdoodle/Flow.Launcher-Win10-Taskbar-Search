using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Flow.Launcher.Helper;

/// <summary>
/// A composition surface holding a small image from pixels in memory, made without Win2D: a Direct3D 11 device for a
/// composition graphics device, a drawing surface of it, and the pixels uploaded into the surface's texture.
/// </summary>
internal static unsafe class CompositionNoiseSurface
{
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [ComImport]
    [Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorInterop
    {
        [PreserveSig] int CreateCompositionSurfaceForHandle(IntPtr handle, out IntPtr result);
        [PreserveSig] int CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr result);
        [PreserveSig] int CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [ComImport]
    [Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositionDrawingSurfaceInterop
    {
        [PreserveSig] int BeginDraw(IntPtr updateRect, in Guid iid, out IntPtr updateObject, out Point offset);
        [PreserveSig] int EndDraw();
    }

    private static readonly Guid IidCompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IidDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static T InteropOf<T>(object winrtObject, Guid iid) where T : class
    {
        var unknown = MarshalInspectable<object>.FromManaged(winrtObject);
        Marshal.QueryInterface(unknown, in iid, out var pointer);
        return (T)Marshal.GetObjectForIUnknown(pointer);
    }

    /// <summary>A surface of size x size pixels from BGRA bytes (size * size * 4).</summary>
    internal static CompositionDrawingSurface Create(Compositor compositor, byte[] bgra, int size)
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x20 /* BGRA support */, IntPtr.Zero, 0, 7, out var device, out _, out _);
        if (hr != 0) throw new InvalidOperationException("D3D11CreateDevice 0x" + hr.ToString("X"));

        var compositorInterop = InteropOf<ICompositorInterop>(compositor, IidCompositorInterop);
        hr = compositorInterop.CreateGraphicsDevice(device, out var graphicsDevicePointer);
        if (hr != 0) throw new InvalidOperationException("CreateGraphicsDevice 0x" + hr.ToString("X"));
        var graphicsDevice = CompositionGraphicsDevice.FromAbi(graphicsDevicePointer);

        var surface = graphicsDevice.CreateDrawingSurface(new Windows.Foundation.Size(size, size), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var surfaceInterop = InteropOf<ICompositionDrawingSurfaceInterop>(surface, IidDrawingSurfaceInterop);
        hr = surfaceInterop.BeginDraw(IntPtr.Zero, in IidTexture2D, out var texture, out var offset);
        if (hr != 0) throw new InvalidOperationException("BeginDraw 0x" + hr.ToString("X"));
        try
        {
            var box = stackalloc uint[6] { (uint)offset.X, (uint)offset.Y, 0, (uint)(offset.X + size), (uint)(offset.Y + size), 1 };
            fixed (byte* data = bgra)
            {
                // ID3D11Device::GetImmediateContext is method 40, ID3D11DeviceContext::UpdateSubresource is method 48
                var deviceVtable = *(IntPtr**)device;
                IntPtr context;
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)deviceVtable[40])(device, &context);
                var contextVtable = *(IntPtr**)context;
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, byte*, uint, uint, void>)contextVtable[48])(context, texture, 0, box, data, (uint)(size * 4), 0);
                Marshal.Release(context);
            }
        }
        finally
        {
            surfaceInterop.EndDraw();
            Marshal.Release(texture);
        }

        return surface;
    }
}
