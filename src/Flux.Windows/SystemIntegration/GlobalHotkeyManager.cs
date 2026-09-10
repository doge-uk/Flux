using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Flux.Windows.SystemIntegration;

public sealed class GlobalHotkeyManager : IDisposable
{
    private const int HotkeyId = 0x464C;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private IntPtr _handle;

    public event EventHandler? Pressed;

    public bool Register(Window window, int modifiers, int key)
    {
        Dispose();
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        return RegisterHotKey(_handle, HotkeyId, (uint)modifiers, (uint)key);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotkeyId);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}

