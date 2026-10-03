using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Flow.Launcher.Helper;

/// <summary>
/// The acrylic of the Windows 10 Start menu and search panel for Flow's window, made by the same thing that makes it for
/// them: the desktop compositor, on the GPU. Measured on the native panels, their acrylic is a chain of Direct2D effects
/// on a live copy of what is behind the window: a Gaussian blur, a saturation step, a luminosity blend with the tint, the
/// tint, and a fixed 256x256 noise texture. Flow's own window can't have it (a WPF window can't host a compositor visual
/// and the host backdrop only reaches windows composed by the compositor itself), so a window of its own sits right behind
/// Flow's window with the same rectangle, shows that effect graph, and Flow's window, transparent, draws on top of it.
/// <para>
/// The native panels are matched to within 1-2 of 255 per channel, identical on about 80% of the pixels (see FORK.md).
/// </para>
/// </summary>
internal sealed unsafe class Win10AcrylicWindow : IDisposable
{
    // What was measured on the native panels (light mode, Transparency effects on)
    internal readonly record struct Look(double Saturation, double LuminosityOpacity, double TintOpacity, double TintOffset, double LuminosityTarget);

    internal static readonly Look StartLight = new(0.540054371704427, 0.9008082467641607, 0.03245196648329378, -0.7210, 0.9429124184107964);
    internal static readonly Look SearchLight = new(0.6626286082481148, 0.8079161972434671, 0.01913878467029378, -0.1113, 0.9766497073793261);

    // The effective sigma of the compositor's blur effect that matches the native one (30 DIP, so 45 px at 150%), as a share
    // of that: the native panels' blur is a true Gaussian, the effect's is Direct2D's approximation of one
    private const double BlurShare = 0.725;
    private const int NoiseSize = 256;
    // The noise texture's offset down the panel (window-relative), as measured
    private const int NoiseOffsetY = 60;

    private const string ClassName = "FlowLauncher.Win10Acrylic";

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);
        [PreserveSig] int EnsureOnThread(uint threadId);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static readonly Guid IidDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    private readonly WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private bool _initialized;
    private bool _failed;

    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private SpriteVisual? _sprite;
    private CompositionDrawingSurface? _noiseSurface;
    private CompositionSurfaceBrush? _noiseBrush;
    private readonly Dictionary<(Look Look, int ScaleKey), CompositionEffectBrush> _brushes = new();
    private CompositionEffectBrush? _currentBrush;

    internal Win10AcrylicWindow()
    {
        _wndProc = WndProc;
    }

    /// <summary>Whether the effect could be set up. False when the compositor refuses (then Flow shows the solid color).</summary>
    internal bool IsAvailable => !_failed;

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmNcHitTest:
                return new IntPtr(-1); // HTTRANSPARENT: clicks belong to Flow's window
            case WmMouseActivate:
                return new IntPtr(3); // MA_NOACTIVATE
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private bool Initialize()
    {
        if (_initialized) return true;
        if (_failed) return false;

        try
        {
            var instance = GetModuleHandleW(null);
            var windowClass = new WndClassEx
            {
                Size = Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = instance,
                ClassName = ClassName
            };
            RegisterClassExW(ref windowClass); // already registered is fine

            _hwnd = CreateWindowExW(WsExNoRedirectionBitmap | WsExNoActivate | WsExToolWindow, ClassName, null, WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");

            // The "host backdrop" accent state: what makes the compositor give this window's visuals the backdrop
            var policy = new AccentPolicy { AccentState = 5, AccentFlags = 2 };
            var size = Marshal.SizeOf<AccentPolicy>();
            var policyMemory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, policyMemory, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = policyMemory, SizeOfData = size };
                if (SetWindowCompositionAttribute(_hwnd, ref data) == 0) throw new InvalidOperationException("host backdrop accent");
            }
            finally
            {
                Marshal.FreeHGlobal(policyMemory);
            }

            // The compositor needs a dispatcher queue on this thread, which the thread's message loop serves
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            CreateDispatcherQueueController(options, out _); // a queue that already exists is fine
            _compositor = new Compositor();

            var unknown = MarshalInspectable<Compositor>.FromManaged(_compositor);
            Marshal.QueryInterface(unknown, in IidDesktopInterop, out var interopPointer);
            var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(interopPointer);
            var hr = interop.CreateDesktopWindowTarget(_hwnd, false, out var targetPointer);
            if (hr != 0) throw new InvalidOperationException("CreateDesktopWindowTarget 0x" + hr.ToString("X"));
            _target = DesktopWindowTarget.FromAbi(targetPointer);

            _sprite = _compositor.CreateSpriteVisual();
            _target.Root = _sprite;

            _noiseSurface = CompositionNoiseSurface.Create(_compositor, NoiseBytes(), NoiseSize);
            _noiseBrush = _compositor.CreateSurfaceBrush(_noiseSurface);
            _noiseBrush.Stretch = CompositionStretch.None;
            _noiseBrush.HorizontalAlignmentRatio = 0;
            _noiseBrush.VerticalAlignmentRatio = 0;
            _noiseBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;

            _initialized = true;
            return true;
        }
        catch (Exception)
        {
            _failed = true;
            Dispose();
            return false;
        }
    }

    // The noise texture as BGRA: the tile (0..5 for -2..3 levels) with its offset down the panel built in
    private static byte[] NoiseBytes()
    {
        var tile = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Resources", "win10-noise.bin"));
        var bgra = new byte[NoiseSize * NoiseSize * 4];
        for (var y = 0; y < NoiseSize; y++)
        {
            for (var x = 0; x < NoiseSize; x++)
            {
                var value = tile[((y + NoiseOffsetY) & (NoiseSize - 1)) * NoiseSize + x];
                var o = (y * NoiseSize + x) * 4;
                bgra[o] = value;
                bgra[o + 1] = value;
                bgra[o + 2] = value;
                bgra[o + 3] = 255;
            }
        }
        return bgra;
    }

    private static readonly double[] LuminosityWeights = { 0.3, 0.59, 0.11 };

    // The effect graph of one look at one scale. Every source parameter is used once in a graph, so the backdrop gets a
    // parameter and a brush for each of its two uses.
    private CompositionEffectBrush CreateBrush(Look look, double scale)
    {
        var compositor = _compositor!;
        var sigma = (float)(30 * scale * BlurShare);

        CompositionEffects.Node BlurAndSaturate(string backdropName)
        {
            var blur = new CompositionEffects.Node(CompositionEffects.GaussianBlur,
                new object[] { sigma, 0u /* speed */, 1u /* hard border */ }, new CompositionEffectSourceParameter(backdropName));

            // Saturation as a color matrix: out_c = sum_i in_i * ((1 - s) * w_i + s * [i == c])
            var matrix = new float[20];
            for (var i = 0; i < 3; i++)
            {
                for (var c = 0; c < 3; c++)
                {
                    matrix[i * 4 + c] = (float)((1 - look.Saturation) * LuminosityWeights[i] + (i == c ? look.Saturation : 0));
                }
            }
            matrix[3 * 4 + 3] = 1;
            return new CompositionEffects.Node(CompositionEffects.ColorMatrix, new object[] { matrix, 1u, false }, blur);
        }

        // A gray of the tint's luminosity, blended with the saturated backdrop by its luminosity (Direct2D's blend takes the
        // color from its first source and the luminosity from its second)
        var gray = (float)look.LuminosityTarget;
        var tint = new CompositionEffects.Node(CompositionEffects.Flood, new object[] { new[] { gray, gray, gray, 1f } });
        var blended = new CompositionEffects.Node(CompositionEffects.Blend, new object[] { 23u /* luminosity */ }, tint, BlurAndSaturate("backdrop"));

        // (1 - b) * ((1 - a) * saturated + a * blended) + b * tint as one arithmetic composite: k2 * s1 + k3 * s2 + k4
        var gain = 1 - look.TintOpacity;
        var mixed = new CompositionEffects.Node(CompositionEffects.ArithmeticComposite,
            new object[]
            {
                new[] { 0f, (float)(gain * (1 - look.LuminosityOpacity)), (float)(gain * look.LuminosityOpacity), (float)(look.TintOpacity * look.TintOffset) },
                false
            },
            BlurAndSaturate("backdrop2"), blended);

        // The noise, repeated over the whole panel, with its base of 2 levels taken off
        var tiled = new CompositionEffects.Node(CompositionEffects.Border, new object[] { 1u /* wrap */, 1u }, new CompositionEffectSourceParameter("noise"));
        var noisy = new CompositionEffects.Node(CompositionEffects.ArithmeticComposite,
            new object[] { new[] { 0f, 1f, 1f, (float)(-2.0 / 255) }, true }, mixed, tiled);

        // Opaque again: over black, the premultiplied color is the color
        var black = new CompositionEffects.Node(CompositionEffects.Flood, new object[] { new[] { 0f, 0f, 0f, 1f } });
        var root = new CompositionEffects.Node(CompositionEffects.Composite, new object[] { 0u }, black, noisy);

        var brush = compositor.CreateEffectFactory(root.AsEffect()).CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateHostBackdropBrush());
        brush.SetSourceParameter("backdrop2", compositor.CreateHostBackdropBrush());
        brush.SetSourceParameter("noise", _noiseBrush!);
        return brush;
    }

    /// <summary>
    /// Puts the window right behind <paramref name="owner"/>, with its rectangle, showing the acrylic of a look. Call again
    /// whenever the owner moves, resizes or changes its place in the z-order.
    /// </summary>
    internal void Place(IntPtr owner, Look look)
    {
        if (!Initialize() || !GetWindowRect(owner, out var rect)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1) return;

        var brush = BrushFor(look, GetDpiForWindow(owner) / 96.0);
        if (brush == null)
        {
            Hide();
            return;
        }

        if (!ReferenceEquals(brush, _currentBrush))
        {
            _currentBrush = brush;
            _sprite!.Brush = brush;
        }

        _sprite!.Size = new Vector2(width, height);
        // Right after the owner in the z-order: behind it (and in the topmost band if it is in it)
        SetWindowPos(_hwnd, owner, rect.Left, rect.Top, width, height, SwpNoActivate | SwpShowWindow);
    }

    // The effect graph of a look at a scale, built once
    private CompositionEffectBrush? BrushFor(Look look, double scale)
    {
        var key = (look, (int)Math.Round(scale * 100));
        if (_brushes.TryGetValue(key, out var brush)) return brush;

        try
        {
            return _brushes[key] = CreateBrush(look, scale);
        }
        catch (Exception)
        {
            _failed = true;
            return null;
        }
    }

    /// <summary>
    /// Sets everything up ahead of the first time Flow is shown (the compositor, the window, the effect graphs of both
    /// layouts at the owner's scale), so showing it is not slower the first time.
    /// </summary>
    internal void Prepare(IntPtr owner)
    {
        if (!Initialize()) return;

        var scale = GetDpiForWindow(owner) / 96.0;
        BrushFor(StartLight, scale);
        BrushFor(SearchLight, scale);
    }

    internal void Hide()
    {
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SwHide);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _initialized = false;
    }
}
