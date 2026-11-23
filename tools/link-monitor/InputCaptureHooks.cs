using System.ComponentModel;
using System.Runtime.InteropServices;

// Global hooks run only while the user has explicitly enabled capture-only mode.
// The payload event is transient; this class does not persist or display input.
internal sealed class InputCaptureHooks : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseHWheel = 0x020E;

    private const byte KeyEventKind = 0x01;
    private const byte MouseMoveEventKind = 0x02;
    private const byte MouseButtonEventKind = 0x03;
    private const byte MouseWheelEventKind = 0x04;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookInfo
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private readonly HookProc _keyboardCallback;
    private readonly HookProc _mouseCallback;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _hasLastMousePoint;
    private int _lastMouseX;
    private int _lastMouseY;

    public event Action<byte[]>? EventCaptured;

    public InputCaptureHooks()
    {
        _keyboardCallback = KeyboardCallback;
        _mouseCallback = MouseCallback;
    }

    public void Start()
    {
        if (_keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero)
            throw new InvalidOperationException("Capture hooks are already installed.");

        _hasLastMousePoint = false;
        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, module, 0);
        if (_keyboardHook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard capture hook.");

        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            throw new Win32Exception(error, "Could not install the mouse capture hook.");
        }
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _hasLastMousePoint = false;
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            bool down = message == WmKeyDown || message == WmSysKeyDown;
            bool up = message == WmKeyUp || message == WmSysKeyUp;
            if (down || up)
            {
                try
                {
                    KeyboardHookInfo info = Marshal.PtrToStructure<KeyboardHookInfo>(lParam);
                    ushort scanCode = unchecked((ushort)info.ScanCode);
                    byte flags = (byte)((info.Flags & 0x01u) != 0u ? 1 : 0); // extended key
                    EventCaptured?.Invoke(new byte[]
                    {
                        KeyEventKind, (byte)scanCode, (byte)(scanCode >> 8), down ? (byte)1 : (byte)0, flags
                    });
                }
                catch { /* Never let a callback exception escape into Windows. */ }
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                int message = wParam.ToInt32();
                MouseHookInfo info = Marshal.PtrToStructure<MouseHookInfo>(lParam);
                switch (message)
                {
                    case WmMouseMove:
                        CaptureMove(info.Point.X, info.Point.Y);
                        break;
                    case WmLButtonDown: CaptureButton(1, true); break;
                    case WmLButtonUp: CaptureButton(1, false); break;
                    case WmRButtonDown: CaptureButton(2, true); break;
                    case WmRButtonUp: CaptureButton(2, false); break;
                    case WmMButtonDown: CaptureButton(3, true); break;
                    case WmMButtonUp: CaptureButton(3, false); break;
                    case WmXButtonDown: CaptureButton(XButton(info.MouseData), true); break;
                    case WmXButtonUp: CaptureButton(XButton(info.MouseData), false); break;
                    case WmMouseWheel: CaptureWheel(info.MouseData, 0); break;
                    case WmMouseHWheel: CaptureWheel(info.MouseData, 1); break;
                }
            }
            catch { /* Never let a callback exception escape into Windows. */ }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void CaptureMove(int x, int y)
    {
        if (!_hasLastMousePoint)
        {
            _lastMouseX = x;
            _lastMouseY = y;
            _hasLastMousePoint = true;
            return;
        }

        int dx = Math.Clamp(x - _lastMouseX, short.MinValue, short.MaxValue);
        int dy = Math.Clamp(y - _lastMouseY, short.MinValue, short.MaxValue);
        _lastMouseX = x;
        _lastMouseY = y;
        if (dx == 0 && dy == 0)
            return;

        short sx = (short)dx;
        short sy = (short)dy;
        EventCaptured?.Invoke(new byte[]
        {
            MouseMoveEventKind, (byte)sx, (byte)(sx >> 8), (byte)sy, (byte)(sy >> 8)
        });
    }

    private void CaptureButton(byte button, bool down) =>
        EventCaptured?.Invoke(new byte[] { MouseButtonEventKind, button, down ? (byte)1 : (byte)0 });

    private void CaptureWheel(uint mouseData, byte axis)
    {
        short delta = unchecked((short)(mouseData >> 16));
        EventCaptured?.Invoke(new byte[] { MouseWheelEventKind, (byte)delta, (byte)(delta >> 8), axis });
    }

    private static byte XButton(uint mouseData) =>
        (ushort)(mouseData >> 16) == 1 ? (byte)4 : (byte)5;

    public void Dispose() => Stop();
}
