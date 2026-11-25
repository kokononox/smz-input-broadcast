using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Concurrent;

namespace Smz.MirrorMaster;

internal sealed class MainForm : Form
{
    private const int WmInput = 0x00FF;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x534D;
    private const uint RidInput = 0x10000003;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RiKeyBreak = 0x0001;
    private const uint RiKeyE0 = 0x0002;
    private const uint RiKeyE1 = 0x0004;
    private const uint MouseMoveAbsolute = 0x0001;
    private const uint MouseVirtualDesktop = 0x0002;
    private const ushort LeftDown = 0x0001, LeftUp = 0x0002;
    private const ushort RightDown = 0x0004, RightUp = 0x0008;
    private const ushort MiddleDown = 0x0010, MiddleUp = 0x0020;
    private const ushort Button4Down = 0x0040, Button4Up = 0x0080;
    private const ushort Button5Down = 0x0100, Button5Up = 0x0200;
    private const ushort Wheel = 0x0400, HorizontalWheel = 0x0800;
    private const uint ModControl = 0x0002, ModShift = 0x0004;
    private const uint VkF12 = 0x7B;

    private readonly ComboBox _ports = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Button _refresh = new() { Text = "Refresh ports", AutoSize = true };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly Button _start = new() { Text = "Start broadcast", AutoSize = true, Enabled = false };
    private readonly Button _stop = new() { Text = "STOP / release", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Text = "Disconnected" };
    private readonly Label _warning = new()
    {
        AutoSize = true,
        MaximumSize = new Size(600, 0),
        Text = "Broadcast is off. When started, keyboard and mouse input is mirrored until stopped. Ctrl+Shift+F12 is the emergency stop."
    };

    private SerialPort? _serial;
    private MirrorTransport? _transport;
    private IntPtr _rawBuffer;
    private bool _capturing;
    private readonly bool[] _keysDown = new bool[256];
    private byte _modifiers;
    private byte _mouseButtons;
    private bool _absolutePositionReady;
    private int _absoluteX, _absoluteY;

    public MainForm()
    {
        Text = "SMZ Mirror Master";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 230);
        ClientSize = new Size(700, 245);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var connection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        connection.Controls.Add(new Label { Text = "Pico master COM:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        connection.Controls.Add(_ports);
        connection.Controls.Add(_refresh);
        connection.Controls.Add(_connect);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(_start);
        actions.Controls.Add(_stop);
        root.Controls.Add(connection, 0, 0);
        root.Controls.Add(actions, 0, 1);
        root.Controls.Add(_status, 0, 2);
        root.Controls.Add(_warning, 0, 3);
        Controls.Add(root);

        _refresh.Click += (_, _) => RefreshPorts();
        _connect.Click += (_, _) => ToggleConnection();
        _start.Click += (_, _) => StartBroadcast();
        _stop.Click += (_, _) => StopBroadcast();
        Shown += (_, _) => RefreshPorts();
        FormClosing += (_, _) => Disconnect();
        _rawBuffer = Marshal.AllocHGlobal(256);
    }

    private void RefreshPorts()
    {
        string? selected = _ports.SelectedItem as string;
        _ports.Items.Clear();
        try
        {
            foreach (string name in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                _ports.Items.Add(name);
        }
        catch (Exception ex)
        {
            SetStatus("Could not enumerate COM ports: " + ex.Message);
        }
        if (selected is not null && _ports.Items.Contains(selected)) _ports.SelectedItem = selected;
        else if (_ports.Items.Count > 0) _ports.SelectedIndex = 0;
    }

    private void ToggleConnection()
    {
        if (_serial is not null)
        {
            Disconnect();
            return;
        }
        if (_ports.SelectedItem is not string portName)
        {
            SetStatus("Select the Pico master COM port first.");
            return;
        }
        try
        {
            var serial = new SerialPort(portName, 1_000_000, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,
                RtsEnable = false,
                Handshake = Handshake.None,
                ReadTimeout = 100,
                WriteTimeout = 250
            };
            serial.Open();
            _serial = serial;
            _transport = new MirrorTransport(serial, SetStatus);
            _connect.Text = "Disconnect";
            _ports.Enabled = _refresh.Enabled = false;
            _start.Enabled = true;
            SetStatus($"Connected to {portName}; broadcast is OFF.");
        }
        catch (Exception ex)
        {
            SetStatus("Connect failed: " + ex.Message);
            _serial?.Dispose();
            _serial = null;
        }
    }

    private void StartBroadcast()
    {
        if (_transport is null || _serial?.IsOpen != true || _capturing) return;
        var answer = MessageBox.Show(
            "This will mirror live keyboard and mouse input to the connected client until stopped. Start only for your authorized test setup.\n\nStart broadcasting?",
            "Confirm input broadcast", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        if (!RegisterRawInput())
        {
            SetStatus("Could not register Windows Raw Input; broadcast was not started.");
            return;
        }
        Array.Clear(_keysDown);
        _modifiers = 0;
        _mouseButtons = 0;
        _absolutePositionReady = false;
        _transport.Start();
        _capturing = true;
        bool hotkeyOk = RegisterHotKey(Handle, HotkeyId, ModControl | ModShift, VkF12);
        _start.Enabled = false;
        _stop.Enabled = true;
        SetStatus(hotkeyOk
            ? "BROADCAST ON — Ctrl+Shift+F12 or STOP / release ends it."
            : "BROADCAST ON — emergency hotkey unavailable; use STOP / release.");
    }

    private void StopBroadcast()
    {
        if (!_capturing) return;
        _capturing = false;
        UnregisterRawInput();
        UnregisterHotKey(Handle, HotkeyId);
        Array.Clear(_keysDown);
        _modifiers = 0;
        _mouseButtons = 0;
        _absolutePositionReady = false;
        _transport?.StopAndRelease();
        _start.Enabled = _serial?.IsOpen == true;
        _stop.Enabled = false;
        SetStatus("Broadcast OFF; release state sent. Client watchdog is the fallback.");
    }

    private void Disconnect()
    {
        StopBroadcast();
        if (_capturing) return;
        _transport?.Dispose();
        _transport = null;
        _serial?.Dispose();
        _serial = null;
        _connect.Text = "Connect";
        _ports.Enabled = _refresh.Enabled = true;
        _start.Enabled = _stop.Enabled = false;
        if (!IsDisposed) SetStatus("Disconnected.");
    }

    private void SetStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => _status.Text = text)); }
            catch (InvalidOperationException) { }
            return;
        }
        _status.Text = text;
    }

    private bool RegisterRawInput()
    {
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevInputSink, Target = Handle },
            new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = Handle }
        };
        return RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private static void UnregisterRawInput()
    {
        var devices = new[]
        {
            new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevRemove, Target = IntPtr.Zero },
            new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevRemove, Target = IntPtr.Zero }
        };
        RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            StopBroadcast();
            return;
        }
        if (m.Msg == WmInput && _capturing)
            ProcessRawInput(m.LParam);
        base.WndProc(ref m);
    }

    private void ProcessRawInput(IntPtr rawHandle)
    {
        if (_rawBuffer == IntPtr.Zero) return;
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint first = GetRawInputData(rawHandle, RidInput, IntPtr.Zero, ref size, headerSize);
        if (first != 0 || size < headerSize || size > 256) return;
        uint read = GetRawInputData(rawHandle, RidInput, _rawBuffer, ref size, headerSize);
        if (read == uint.MaxValue || read < headerSize) return;
        RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(_rawBuffer);
        IntPtr data = IntPtr.Add(_rawBuffer, (int)headerSize);
        if (header.Type == 0u)
            ProcessMouse(Marshal.PtrToStructure<RawMouse>(data));
        else if (header.Type == 1u)
            ProcessKeyboard(Marshal.PtrToStructure<RawKeyboard>(data));
    }

    private void ProcessKeyboard(RawKeyboard key)
    {
        if (_transport is null || key.MakeCode == 0xFF) return;
        byte usage = HidUsageFromScanCode(key.MakeCode, key.Flags);
        if (usage == 0) return;
        bool down = (key.Flags & RiKeyBreak) == 0;
        if (usage >= 0xE0 && usage <= 0xE7)
        {
            byte mask = (byte)(1 << (usage - 0xE0));
            byte next = down ? (byte)(_modifiers | mask) : (byte)(_modifiers & ~mask);
            if (next == _modifiers) return;
            _modifiers = next;
        }
        else
        {
            if (_keysDown[usage] == down) return;
            _keysDown[usage] = down;
        }
        _transport.Enqueue(BuildStateSnapshot());
    }

    private void ProcessMouse(RawMouse mouse)
    {
        if (_transport is null) return;
        int dx = mouse.LastX, dy = mouse.LastY;
        if ((mouse.Flags & MouseMoveAbsolute) != 0)
        {
            bool virtualDesktop = (mouse.Flags & MouseVirtualDesktop) != 0;
            int left = virtualDesktop ? GetSystemMetrics(76) : 0;
            int top = virtualDesktop ? GetSystemMetrics(77) : 0;
            int width = Math.Max(1, GetSystemMetrics(virtualDesktop ? 78 : 0));
            int height = Math.Max(1, GetSystemMetrics(virtualDesktop ? 79 : 1));
            int absoluteX = left + (int)((long)mouse.LastX * (width - 1) / 65535);
            int absoluteY = top + (int)((long)mouse.LastY * (height - 1) / 65535);
            if (_absolutePositionReady)
            {
                dx = absoluteX - _absoluteX;
                dy = absoluteY - _absoluteY;
            }
            else
            {
                dx = dy = 0;
                _absolutePositionReady = true;
            }
            _absoluteX = absoluteX;
            _absoluteY = absoluteY;
        }
        if (dx != 0 || dy != 0)
            _transport.Enqueue(MakeMouseMove(dx, dy));

        bool buttonsChanged = false;
        ApplyButton(mouse.ButtonFlags, LeftDown, LeftUp, 1, ref buttonsChanged);
        ApplyButton(mouse.ButtonFlags, RightDown, RightUp, 2, ref buttonsChanged);
        ApplyButton(mouse.ButtonFlags, MiddleDown, MiddleUp, 3, ref buttonsChanged);
        ApplyButton(mouse.ButtonFlags, Button4Down, Button4Up, 4, ref buttonsChanged);
        ApplyButton(mouse.ButtonFlags, Button5Down, Button5Up, 5, ref buttonsChanged);
        if (buttonsChanged) _transport.Enqueue(BuildStateSnapshot());

        if ((mouse.ButtonFlags & Wheel) != 0)
            _transport.Enqueue(MakeWheel(unchecked((short)mouse.ButtonData), 0));
        if ((mouse.ButtonFlags & HorizontalWheel) != 0)
            _transport.Enqueue(MakeWheel(unchecked((short)mouse.ButtonData), 1));
    }

    private void ApplyButton(ushort flags, ushort downFlag, ushort upFlag, int button,
                             ref bool changed)
    {
        byte mask = (byte)(1 << (button - 1));
        if ((flags & downFlag) != 0 && (_mouseButtons & mask) == 0)
        {
            _mouseButtons |= mask;
            changed = true;
        }
        if ((flags & upFlag) != 0 && (_mouseButtons & mask) != 0)
        {
            _mouseButtons &= (byte)~mask;
            changed = true;
        }
    }

    private byte[] BuildStateSnapshot()
    {
        byte[] payload = new byte[9];
        payload[0] = 0x07;
        payload[1] = _modifiers;
        int slot = 0;
        for (int usage = 1; usage < 0xE0 && slot < 6; usage++)
            if (_keysDown[usage]) payload[2 + slot++] = (byte)usage;
        payload[8] = _mouseButtons;
        return payload;
    }

    private static byte[] MakeMouseMove(int dx, int dy)
    {
        short x = (short)Math.Clamp(dx, -32767, 32767);
        short y = (short)Math.Clamp(dy, -32767, 32767);
        return new byte[] { 0x02, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8) };
    }

    private static byte[] MakeWheel(short delta, byte axis)
        => new byte[] { 0x04, (byte)delta, (byte)(delta >> 8), axis };

    private static byte HidUsageFromScanCode(ushort scanCode, uint flags)
    {
        byte code = (byte)(scanCode & 0xFF);
        bool extended = (flags & RiKeyE0) != 0;
        if ((flags & RiKeyE1) != 0)
        {
            if (code == 0x1D) return 0x48; // Pause
            return 0;
        }
        if (extended)
        {
            return code switch
            {
                0x1C => 0x58, 0x1D => 0xE4, 0x35 => 0x54, 0x38 => 0xE6,
                0x47 => 0x4A, 0x48 => 0x52, 0x49 => 0x4B, 0x4B => 0x50,
                0x4D => 0x4F, 0x4F => 0x4D, 0x50 => 0x51, 0x51 => 0x4E,
                0x52 => 0x49, 0x53 => 0x4C, 0x5B => 0xE3, 0x5C => 0xE7,
                0x5D => 0x65, _ => 0
            };
        }
        if (code >= 0x02 && code <= 0x0B)
        {
            ReadOnlySpan<byte> digits = [0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27];
            return digits[code - 0x02];
        }
        if (code >= 0x10 && code <= 0x19)
        {
            ReadOnlySpan<byte> row = [0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13];
            return row[code - 0x10];
        }
        if (code >= 0x1E && code <= 0x26)
        {
            ReadOnlySpan<byte> row = [0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F];
            return row[code - 0x1E];
        }
        if (code >= 0x2C && code <= 0x32)
        {
            ReadOnlySpan<byte> row = [0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10];
            return row[code - 0x2C];
        }
        if (code >= 0x3B && code <= 0x44) return (byte)(0x3A + code - 0x3B);
        return code switch
        {
            0x01 => 0x29, 0x0C => 0x2D, 0x0D => 0x2E, 0x0E => 0x2A, 0x0F => 0x2B,
            0x1A => 0x2F, 0x1B => 0x30, 0x1C => 0x28, 0x1D => 0xE0,
            0x27 => 0x33, 0x28 => 0x34, 0x29 => 0x35, 0x2A => 0xE1, 0x2B => 0x31,
            0x33 => 0x36, 0x34 => 0x37, 0x35 => 0x38, 0x36 => 0xE5, 0x37 => 0x55,
            0x38 => 0xE2, 0x39 => 0x2C, 0x3A => 0x39, 0x45 => 0x53, 0x46 => 0x47,
            0x47 => 0x5F, 0x48 => 0x60, 0x49 => 0x61, 0x4A => 0x56, 0x4B => 0x5C,
            0x4C => 0x5D, 0x4D => 0x5E, 0x4E => 0x57, 0x4F => 0x59, 0x50 => 0x5A,
            0x51 => 0x5B, 0x52 => 0x62, 0x53 => 0x63, 0x57 => 0x44, 0x58 => 0x45,
            _ => 0
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disconnect();
            if (_rawBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_rawBuffer);
                _rawBuffer = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey;
        public uint Message; public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data,
                                               ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

internal sealed class MirrorTransport : IDisposable
{
    private const int MaxQueue = 4096;
    private const int MaxBatchBytes = 16384;
    private readonly SerialPort _serial;
    private readonly Action<string> _status;
    private readonly ConcurrentQueue<QueuedFrame> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _releaseSent = new(false);
    private readonly Thread _writer;
    private volatile bool _running = true;
    private volatile bool _active;
    private int _generation;
    private int _sequence;
    private int _queuedCount;
    private int _forceRelease;
    private int _faultReported;

    private readonly record struct QueuedFrame(byte[] Bytes, int Generation);

    public MirrorTransport(SerialPort serial, Action<string> status)
    {
        _serial = serial;
        _status = status;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SMZ mirror serial writer" };
        _writer.Start();
    }

    public void Start()
    {
        while (_queue.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _queuedCount, 0);
        int generation = Interlocked.Increment(ref _generation);
        _releaseSent.Reset();
        _active = true;
        EnqueueCore(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0 }, generation);
        _wake.Set();
    }

    public void Enqueue(byte[] payload)
    {
        if (!_active || payload.Length == 0 || payload.Length > 16) return;
        int generation = Volatile.Read(ref _generation);
        EnqueueCore(payload, generation);
    }

    private void EnqueueCore(byte[] payload, int generation)
    {
        if (!_active && payload[0] != 0x07) return;
        int count = Interlocked.Increment(ref _queuedCount);
        if (count > MaxQueue)
        {
            Interlocked.Decrement(ref _queuedCount);
            _status("Queue limit reached; broadcast stopped. Client watchdog releases held input.");
            StopAndRelease();
            return;
        }
        _queue.Enqueue(new QueuedFrame(BuildFrame(payload), generation));
        _wake.Set();
    }

    public void StopAndRelease()
    {
        _active = false;
        Interlocked.Increment(ref _generation);
        while (_queue.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _queuedCount, 0);
        Interlocked.Exchange(ref _forceRelease, 1);
        _releaseSent.Reset();
        _wake.Set();
    }

    private byte[] BuildFrame(byte[] payload)
    {
        byte[] frame = new byte[11 + payload.Length + 2];
        frame[0] = 0xA5; frame[1] = 0x5A; frame[2] = 1; frame[3] = 1;
        ushort sequence = unchecked((ushort)Interlocked.Increment(ref _sequence));
        frame[4] = (byte)sequence; frame[5] = (byte)(sequence >> 8);
        frame[6] = (byte)payload.Length;
        uint timestamp = TimestampMicroseconds();
        frame[7] = (byte)timestamp; frame[8] = (byte)(timestamp >> 8);
        frame[9] = (byte)(timestamp >> 16); frame[10] = (byte)(timestamp >> 24);
        Buffer.BlockCopy(payload, 0, frame, 11, payload.Length);
        ushort crc = Crc16(frame, 2, 9 + payload.Length);
        frame[11 + payload.Length] = (byte)crc;
        frame[12 + payload.Length] = (byte)(crc >> 8);
        return frame;
    }

    private static uint TimestampMicroseconds()
    {
        long ticks = Stopwatch.GetTimestamp();
        long frequency = Stopwatch.Frequency;
        long us = (ticks / frequency) * 1_000_000L +
                  ((ticks % frequency) * 1_000_000L) / frequency;
        return unchecked((uint)us);
    }

    private static ushort Crc16(byte[] bytes, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= (ushort)(bytes[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private void WriterLoop()
    {
        byte[] batch = new byte[MaxBatchBytes];
        long nextHeartbeat = Stopwatch.GetTimestamp();
        while (_running)
        {
            if (Interlocked.Exchange(ref _forceRelease, 0) != 0)
            {
                try
                {
                    byte[] release = BuildFrame(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0 });
                    if (_serial.IsOpen) _serial.Write(release, 0, release.Length);
                    _releaseSent.Set();
                }
                catch (Exception ex)
                {
                    _releaseSent.Set();
                    ReportFault("Release frame could not be sent: " + ex.Message);
                }
                nextHeartbeat = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                continue;
            }

            int used = 0;
            int generation = Volatile.Read(ref _generation);
            int taken = 0;
            while (taken < 256 && _queue.TryDequeue(out QueuedFrame item))
            {
                Interlocked.Decrement(ref _queuedCount);
                taken++;
                if (!_active || item.Generation != generation) continue;
                if (used + item.Bytes.Length > batch.Length) break;
                Buffer.BlockCopy(item.Bytes, 0, batch, used, item.Bytes.Length);
                used += item.Bytes.Length;
            }

            long now = Stopwatch.GetTimestamp();
            if (_active && now >= nextHeartbeat)
            {
                byte[] heartbeat = BuildFrame(new byte[] { 0x05 });
                if (used + heartbeat.Length <= batch.Length)
                {
                    Buffer.BlockCopy(heartbeat, 0, batch, used, heartbeat.Length);
                    used += heartbeat.Length;
                }
                nextHeartbeat = now + Stopwatch.Frequency / 10;
            }

            if (used != 0)
            {
                try
                {
                    if (_serial.IsOpen) _serial.Write(batch, 0, used);
                    else throw new IOException("COM port is closed.");
                }
                catch (Exception ex)
                {
                    _active = false;
                    ReportFault("Serial write failed; broadcasting stopped: " + ex.Message);
                }
            }
            else
            {
                int waitMs = _active ? 5 : 50;
                _wake.WaitOne(waitMs);
            }
        }
    }

    private void ReportFault(string message)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) == 0) _status(message);
    }

    public void Dispose()
    {
        StopAndRelease();
        _releaseSent.Wait(300);
        _running = false;
        _wake.Set();
        _writer.Join(500);
        _wake.Dispose();
        _releaseSent.Dispose();
    }
}
