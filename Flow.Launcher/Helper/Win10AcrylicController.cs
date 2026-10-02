using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Flow.Launcher.Helper;

/// <summary>
/// Gives the main window the acrylic of the native Windows 10 Start menu or search panel exactly: renders it in software
/// (<see cref="Win10AcrylicRenderer"/>) from a capture of what is behind the window, and puts the result in the
/// <c>Win10StartBackground</c> brush the window's background uses.
/// </summary>
internal sealed class Win10AcrylicController
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;

    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private int _version;
    private bool _running;
    private bool _again;
    private SystemAcrylic.Panel _panel;
    private bool _exact;
    private ulong _lastHash;

    internal Win10AcrylicController(Window window)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync(false);
        };
    }

    /// <summary>
    /// Whether the software rendering can replace the system acrylic: only the light mode is measured, and the window must
    /// not be layered (a layered window can't be left out of its own capture, see <c>SetWindowDisplayAffinity</c>).
    /// </summary>
    internal bool CanRender
    {
        get
        {
            if (!SystemAcrylic.LightMode || DebugValue("software") == "0") return false;
            var hwnd = new WindowInteropHelper(_window).Handle;
            return hwnd != IntPtr.Zero && (GetWindowLong(hwnd, -20) & 0x80000) == 0;
        }
    }

    /// <summary>
    /// Renders for the window's current position and size, soon (a burst of calls is one rendering). exact: with the
    /// full resolution blur, for the first rendering after the window is shown. force: also when the backdrop did not change.
    /// </summary>
    internal void Request(SystemAcrylic.Panel panel, bool exact, bool force)
    {
        _panel = panel;
        _exact |= exact;
        if (force) _lastHash = 0;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Renders right away, from the UI thread, e.g. while the window is still cloaked.</summary>
    internal void RequestNow(SystemAcrylic.Panel panel, bool exact, bool force)
    {
        _panel = panel;
        _exact |= exact;
        if (force) _lastHash = 0;
        _debounce.Stop();
        _ = RunAsync(false);
    }

    private async Task RunAsync(bool recursion)
    {
        if (_running)
        {
            _again = true;
            return;
        }

        _running = true;
        try
        {
            do
            {
                _again = false;
                var exact = _exact;
                _exact = false;
                await RenderOnceAsync(_panel, exact);
            } while (_again);
        }
        catch (Exception)
        {
            // Keep the previous rendering (or the system acrylic if there is none yet)
        }
        finally
        {
            _running = false;
        }
    }

    private bool _rendered;

    /// <summary>Whether a software rendering is the window's background.</summary>
    internal bool HasRendered => _rendered;

    private async Task RenderOnceAsync(SystemAcrylic.Panel panel, bool exact)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w < 16 || h < 16) return;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var look = panel == SystemAcrylic.Panel.Start ? Win10AcrylicRenderer.StartLight : Win10AcrylicRenderer.SearchLight;
        var insets = panel == SystemAcrylic.Panel.Start ? Win10AcrylicRenderer.StartInsets : Win10AcrylicRenderer.SearchInsets;
        var version = ++_version;

        // A visible window must not be in its own backdrop; a cloaked one is not composed anyway
        var excludeSelf = _window.IsVisible;
        var result = await Task.Run(() =>
        {
            var backdrop = Capture(hwnd, rect.Left, rect.Top, w, h, excludeSelf);
            if (backdrop == null) return null;
            var hash = Hash(backdrop);
            if (hash == _lastHash) return Array.Empty<byte>();
            _lastHash = hash;
            return Win10AcrylicRenderer.Render(backdrop, w, h, look, insets, scale, exact ? 1 : 2);
        });
        if (result == null || version != _version) return;
        if (result.Length == 0) return; // backdrop unchanged

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, result, w * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        Application.Current.Resources["Win10StartBackground"] = brush;
        if (!_rendered)
        {
            // The Start panel's own background must not hide it
            var faint = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 255, 255, 255));
            faint.Freeze();
            Application.Current.Resources["Win10StartPanelBackground"] = faint;
        }
        _rendered = true;
    }

    /// <summary>Forgets the rendering state, e.g. when the system acrylic takes over (the next rendering applies again).</summary>
    internal void Reset()
    {
        _lastHash = 0;
        _rendered = false;
        _version++;
    }

    private static ulong Hash(byte[] data)
    {
        ulong hash = 14695981039346656037;
        for (var i = 0; i < data.Length; i += 61)
        {
            hash ^= data[i];
            hash *= 1099511628211;
        }
        return hash;
    }

    private static byte[]? Capture(IntPtr hwnd, int x, int y, int w, int h, bool excludeSelf)
    {
        try
        {
            if (excludeSelf)
            {
                var ok = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
                var error = Marshal.GetLastWin32Error();
                GetWindowDisplayAffinity(hwnd, out var now);
                var logDir = DebugPath();
                if (logDir != null)
                {
                    var exStyle = GetWindowLong(hwnd, -20);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "log.txt"), $"{DateTime.Now:HH:mm:ss.fff} SetWindowDisplayAffinity ok={ok} err={error} affinity now=0x{now:X} exStyle=0x{exStyle:X}{Environment.NewLine}");
                }
                // The compositor takes a frame to leave the window out
                Thread.Sleep(DebugSetting("delay", 10));
            }

            try
            {
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
                }

                var debugDir = DebugPath();
                if (debugDir != null) bmp.Save(System.IO.Path.Combine(debugDir, $"capture-{(excludeSelf ? "excl" : "plain")}-{DateTime.Now:HHmmss-fff}.png"), System.Drawing.Imaging.ImageFormat.Png);

                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    var buffer = new byte[w * h * 4];
                    for (var row = 0; row < h; row++)
                    {
                        Marshal.Copy(data.Scan0 + row * data.Stride, buffer, row * w * 4, w * 4);
                    }
                    return buffer;
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            finally
            {
                if (excludeSelf) SetWindowDisplayAffinity(hwnd, WdaNone);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Developer switch: a file %TEMP%low-acrylic-debug.txt with "key=value" lines (delay=<ms to wait after excluding the
    // window from capture>, dir=<folder to save every capture to>)
    private static string? DebugValue(string key)
    {
        try
        {
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flow-acrylic-debug.txt");
            if (!System.IO.File.Exists(file)) return null;
            foreach (var line in System.IO.File.ReadAllLines(file))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim() == key) return parts[1].Trim();
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static int DebugSetting(string key, int defaultValue) => int.TryParse(DebugValue(key), out var v) ? v : defaultValue;

    private static string? DebugPath()
    {
        var dir = DebugValue("dir");
        return dir != null && System.IO.Directory.Exists(dir) ? dir : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);
}
