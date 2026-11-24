using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Keyboard hooks and mouse Raw Input are installed only while the user has
// explicitly enabled capture mode. Payloads are transient and are not persisted.
internal sealed class InputCaptureHooks : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmInput = 0x00FF;

    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageMouse = 0x02;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;

    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort MouseLeftButtonDown = 0x0001;
    private const ushort MouseLeftButtonUp = 0x0002;
    private const ushort MouseRightButtonDown = 0x0004;
    private const ushort MouseRightButtonUp = 0x0008;
    private const ushort MouseMiddleButtonDown = 0x0010;
    private const ushort MouseMiddleButtonUp = 0x0020;
    private const ushort MouseButton4Down = 0x0040;
    private const ushort MouseButton4Up = 0x0080;
    private const ushort MouseButton5Down = 0x0100;
    private const ushort MouseButton5Up = 0x0200;
    private const ushort MouseWheel = 0x0400;
    private const ushort MouseHWheel = 0x0800;

    private const byte KeyEventKind = 0x01;
    private const byte MouseMoveEventKind = 0x02;
    private const byte MouseButtonEventKind = 0x03;
    private const byte MouseWheelEventKind = 0x04;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

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
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    // RAWMOUSE's button flags and button data share a four-byte union.
    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    private readonly HookProc _keyboardCallback;
    private IntPtr _keyboardHook;
    private RawInputMessageWindow? _rawInputWindow;
    private bool _rawInputRegistered;

    public event Action<byte[]>? EventCaptured;

    public InputCaptureHooks()
    {
        _keyboardCallback = KeyboardCallback;
    }

    public void Start()
    {
        if (_keyboardHook != IntPtr.Zero || _rawInputWindow is not null)
            throw new InvalidOperationException("Input capture is already active.");

        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, module, 0);
        if (_keyboardHook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard capture hook.");

        try
        {
            _rawInputWindow = new RawInputMessageWindow(this);
            var devices = new[]
            {
                new RawInputDevice
                {
                    UsagePage = HidUsagePageGeneric,
                    Usage = HidUsageMouse,
                    Flags = RidevInputSink,
                    Target = _rawInputWindow.Handle
                }
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the mouse Raw Input device.");

            _rawInputRegistered = true;
        }
        catch
        {
            _rawInputWindow?.Destroy();
            _rawInputWindow = null;
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            throw;
        }
    }

    public void Stop()
    {
        if (_rawInputRegistered)
        {
            var devices = new[]
            {
                new RawInputDevice
                {
                    UsagePage = HidUsagePageGeneric,
                    Usage = HidUsageMouse,
                    Flags = RidevRemove,
                    Target = IntPtr.Zero
                }
            };
            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
            _rawInputRegistered = false;
        }

        _rawInputWindow?.Destroy();
        _rawInputWindow = null;

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
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

    private void ProcessRawInput(IntPtr rawInput)
    {
        if (!_rawInputRegistered || rawInput == IntPtr.Zero)
            return;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            uint size = 0;
            uint result = GetRawInputData(rawInput, RidInput, IntPtr.Zero, ref size, headerSize);
            if (result != 0 || size < headerSize + (uint)Marshal.SizeOf<RawMouse>() || size > 4096)
                return;

            buffer = Marshal.AllocHGlobal((int)size);
            uint allocatedSize = size;
            result = GetRawInputData(rawInput, RidInput, buffer, ref size, headerSize);
            if (result != allocatedSize)
                return;

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeMouse || header.Size < headerSize + (uint)Marshal.SizeOf<RawMouse>())
                return;

            RawMouse mouse = Marshal.PtrToStructure<RawMouse>(IntPtr.Add(buffer, (int)headerSize));
            CaptureRawMouse(mouse);
        }
        catch { /* Ignore malformed input; never interrupt the Windows message loop. */ }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    private void CaptureRawMouse(RawMouse mouse)
    {
        // Relative Raw Input is device movement before pointer speed/acceleration.
        // Absolute devices use a different coordinate model and are intentionally ignored here.
        if ((mouse.Flags & MouseMoveAbsolute) == 0)
            CaptureRelativeMove(mouse.LastX, mouse.LastY);

        ushort flags = mouse.ButtonFlags;
        if ((flags & MouseLeftButtonDown) != 0) CaptureButton(1, true);
        if ((flags & MouseLeftButtonUp) != 0) CaptureButton(1, false);
        if ((flags & MouseRightButtonDown) != 0) CaptureButton(2, true);
        if ((flags & MouseRightButtonUp) != 0) CaptureButton(2, false);
        if ((flags & MouseMiddleButtonDown) != 0) CaptureButton(3, true);
        if ((flags & MouseMiddleButtonUp) != 0) CaptureButton(3, false);
        if ((flags & MouseButton4Down) != 0) CaptureButton(4, true);
        if ((flags & MouseButton4Up) != 0) CaptureButton(4, false);
        if ((flags & MouseButton5Down) != 0) CaptureButton(5, true);
        if ((flags & MouseButton5Up) != 0) CaptureButton(5, false);
        if ((flags & MouseWheel) != 0) CaptureWheel(unchecked((short)mouse.ButtonData), 0);
        if ((flags & MouseHWheel) != 0) CaptureWheel(unchecked((short)mouse.ButtonData), 1);
    }

    private void CaptureRelativeMove(int dx, int dy)
    {
        dx = Math.Clamp(dx, short.MinValue, short.MaxValue);
        dy = Math.Clamp(dy, short.MinValue, short.MaxValue);
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

    private void CaptureWheel(short delta, byte axis) =>
        EventCaptured?.Invoke(new byte[] { MouseWheelEventKind, (byte)delta, (byte)(delta >> 8), axis });

    public void Dispose() => Stop();

    private sealed class RawInputMessageWindow : NativeWindow
    {
        private readonly InputCaptureHooks _owner;

        public RawInputMessageWindow(InputCaptureHooks owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "SMZ Raw Mouse Input" });
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Raw Input message window.");
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmInput)
                _owner.ProcessRawInput(message.LParam);

            // Let the default window procedure perform the required WM_INPUT cleanup.
            base.WndProc(ref message);
        }

        public void Destroy()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }
}
