using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MonitorForm());
    }
}

internal sealed class MonitorForm : Form
{
    // 1 ms matches the full-speed HID endpoint polling interval; Windows and the link may deliver less.
    private const int CaptureIntervalMs = 1;
    private readonly ComboBox _ports = new();
    private readonly Button _refreshButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _syntheticButton = new();
    private readonly Button _captureOnlyButton = new();
    private readonly System.Threading.Timer _syntheticTimer;
    private readonly System.Threading.Timer _captureTimer;
    private readonly InputCaptureHooks _captureHooks = new();
    private readonly LinkedList<byte[]> _captureQueue = new();
    private readonly object _captureQueueLock = new();
    private readonly object _serialWriteLock = new();
    private readonly Label _usbStatus = new();
    private readonly Label _linkStatus = new();
    private readonly Label _counts = new();
    private readonly Label _rtt = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly StringBuilder _receiveBuffer = new();
    private readonly object _bufferLock = new();
    private SerialPort? _serial;
    private DateTime _lastReportUtc = DateTime.MinValue;
    private DateTime _connectedUtc = DateTime.MinValue;
    private bool _sawMasterReport;
    private volatile bool _syntheticTestRunning;
    private bool _syntheticTimerResolutionActive;
    private bool _captureTimerResolutionActive;
    private int _eventSequence;
    private int _syntheticSendInProgress;
    private int _captureSendInProgress;
    private volatile bool _captureOnlyRunning;
    private int _captureDropped;
    private int _captureSent;
    private long _lastCaptureHeartbeatTicks;
    private string _latestLinkStatus = "منتظر گزارش فریمور مادر";

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    private static extern uint TimeEndPeriod(uint period);

    public MonitorForm()
    {
        Text = "SMZ — آزمون اتصال Pico";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(700, 480);
        Size = new System.Drawing.Size(820, 570);
        Font = new System.Drawing.Font("Segoe UI", 9F);

        BuildUi();
        RefreshPorts();
        _timer.Interval = 500;
        _timer.Tick += (_, _) => CheckConnectionState();
        _timer.Start();
        _syntheticTimer = new System.Threading.Timer(SendSyntheticTestFrame, null, Timeout.Infinite, Timeout.Infinite);
        _captureTimer = new System.Threading.Timer(SendCaptureFrame, null, Timeout.Infinite, Timeout.Infinite);
        _captureHooks.EventCaptured += EnqueueCaptureEvent;
        FormClosing += (_, _) =>
        {
            Disconnect();
            _syntheticTimer.Dispose();
            _captureTimer.Dispose();
            _captureHooks.Dispose();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 0)
        };
        toolbar.Controls.Add(new Label { Text = "پورت Pico مادر:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _ports.Width = 130;
        _ports.DropDownStyle = ComboBoxStyle.DropDownList;
        toolbar.Controls.Add(_ports);
        _refreshButton.Text = "تازه‌سازی";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => RefreshPorts();
        toolbar.Controls.Add(_refreshButton);
        _connectButton.Text = "اتصال";
        _connectButton.Width = 95;
        _connectButton.Click += (_, _) => ToggleConnection();
        toolbar.Controls.Add(_connectButton);
        _syntheticButton.Text = "شروع آزمون ساختگی";
        _syntheticButton.AutoSize = true;
        _syntheticButton.Enabled = false;
        _syntheticButton.Click += (_, _) => ToggleSyntheticTest();
        toolbar.Controls.Add(_syntheticButton);
        _captureOnlyButton.Text = "شروع ارسال HID";
        _captureOnlyButton.AutoSize = true;
        _captureOnlyButton.Enabled = false;
        _captureOnlyButton.Click += (_, _) => ToggleCaptureOnly();
        toolbar.Controls.Add(_captureOnlyButton);
        root.Controls.Add(toolbar, 0, 0);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0, 4, 0, 4)
        };
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _usbStatus.Text = "USB: وصل نشده";
        _linkStatus.Text = "لینک بردبه‌برد: منتظر شروع";
        _counts.Text = "گزارش یک‌ثانیه‌ای: —";
        _rtt.Text = "RTT: —";
        foreach (Label statusLabel in new[] { _usbStatus, _linkStatus, _counts, _rtt })
        {
            statusLabel.AutoSize = false;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.AutoEllipsis = true;
            statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        }
        statusPanel.Controls.Add(_usbStatus, 0, 0);
        statusPanel.Controls.Add(_linkStatus, 0, 1);
        statusPanel.Controls.Add(_counts, 0, 2);
        statusPanel.Controls.Add(_rtt, 0, 3);
        statusPanel.Controls.Add(new Label
        {
            Text = "ارسال فقط با شروع دستی و جامپر GP14↔GND روی پیکوی کلاینت فعال می‌شود؛ با توقف یا قطع لینک، کلیدها آزاد می‌شوند.",
            AutoSize = true,
            ForeColor = System.Drawing.Color.DimGray,
            Margin = new Padding(0, 5, 0, 0)
        }, 0, 4);
        root.Controls.Add(statusPanel, 0, 1);

        root.Controls.Add(new Label { Text = "خروجی سریال Pico مادر (115200، USB CDC):", AutoSize = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 2);
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = false;
        _log.Font = new System.Drawing.Font("Consolas", 9F);
        root.Controls.Add(_log, 0, 3);
    }

    private void RefreshPorts()
    {
        string? previous = _ports.SelectedItem as string;
        _ports.Items.Clear();
        string[] found;
        try
        {
            found = SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            AppendLog("خطا در فهرست پورت‌ها: " + ex.Message);
            return;
        }

        foreach (string port in found)
            _ports.Items.Add(port);

        if (previous is not null && _ports.Items.Contains(previous))
            _ports.SelectedItem = previous;
        else if (_ports.Items.Count > 0)
            _ports.SelectedIndex = 0;

        if (found.Length == 0)
            SetUsbStatus("USB: هیچ پورت COM پیدا نشد", System.Drawing.Color.DarkOrange);
        else if (_serial is null || !_serial.IsOpen)
            SetUsbStatus("USB: Pico را انتخاب و متصل کنید", System.Drawing.Color.DimGray);
    }

    private void ToggleConnection()
    {
        if (_serial is not null && _serial.IsOpen)
        {
            Disconnect();
            return;
        }

        if (_ports.SelectedItem is not string portName)
        {
            MessageBox.Show(this, "ابتدا پورت COM مربوط به Pico مادر را انتخاب کنید.", "پورت انتخاب نشده", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var serial = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = false,
                Encoding = Encoding.ASCII,
                ReadTimeout = 500,
                WriteTimeout = 500
            };
            serial.DataReceived += PortDataReceived;
            serial.Open();
            _serial = serial;
            _sawMasterReport = false;
            _lastReportUtc = DateTime.MinValue;
            _connectedUtc = DateTime.UtcNow;
            _latestLinkStatus = "منتظر گزارش فریمور مادر";
            _connectButton.Text = "قطع اتصال";
            _ports.Enabled = false;
            _refreshButton.Enabled = false;
            _syntheticButton.Enabled = true;
            _captureOnlyButton.Enabled = true;
            SetUsbStatus($"USB: به {portName} وصل است", System.Drawing.Color.DarkGreen);
            SetLinkStatus(_latestLinkStatus, System.Drawing.Color.DimGray);
            AppendLog($"--- اتصال به {portName} با 115200 baud ---");
        }
        catch (Exception ex)
        {
            AppendLog("بازکردن پورت ناموفق بود: " + ex.Message);
            MessageBox.Show(this, "پورت باز نشد. بررسی کنید برنامهٔ Serial Monitor دیگری آن را اشغال نکرده باشد.\n\n" + ex.Message,
                "خطای اتصال COM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Disconnect();
        }
    }

    private void ToggleSyntheticTest()
    {
        if (_syntheticTestRunning)
        {
            StopSyntheticTest(true);
            return;
        }
        if (_serial is null || !_serial.IsOpen)
        {
            MessageBox.Show(this, "ابتدا به پورت COM پیکوی مادر وصل شوید.", "اتصال لازم است", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _syntheticTestRunning = true;
        _syntheticButton.Text = "توقف آزمون";
        _captureOnlyButton.Enabled = false;
        AppendLog("--- آزمون دستی فریم ساختگی آغاز شد؛ ورودی واقعی خوانده نمی‌شود ---");
        // WinForms timers can be coalesced to ~32 Hz by the default Windows timer resolution.
        // Request 1 ms precision only while this explicit synthetic test is active.
        uint timerResult = TimeBeginPeriod(1);
        _syntheticTimerResolutionActive = timerResult == 0;
        if (!_syntheticTimerResolutionActive)
            AppendLog($"زمان‌بندی دقیق ویندوز فعال نشد (کد {timerResult})؛ آزمون با دقت پیش‌فرض ادامه می‌یابد.");
        AppendLog("--- زمان‌بندی نسخهٔ جدید: Timer پس‌زمینه، هدف 50Hz ---");
        _syntheticTimer.Change(0, 20);
    }

    private void StopSyntheticTest(bool writeLog)
    {
        bool wasRunning = _syntheticTestRunning;
        _syntheticTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _syntheticTestRunning = false;
        if (_syntheticTimerResolutionActive)
        {
            TimeEndPeriod(1);
            _syntheticTimerResolutionActive = false;
        }
        _syntheticButton.Text = "شروع آزمون ساختگی";
        _captureOnlyButton.Enabled = _serial is not null && _serial.IsOpen;
        if (writeLog && wasRunning)
            AppendLog("--- آزمون فریم ساختگی متوقف شد ---");
    }

    private void SendSyntheticTestFrame(object? state)
    {
        if (!_syntheticTestRunning || Interlocked.Exchange(ref _syntheticSendInProgress, 1) != 0)
            return;

        try
        {
            SerialPort? serial = _serial;
            if (serial is null || !serial.IsOpen)
            {
                QueueSyntheticTestStop("پورت سریال بسته شد؛ آزمون ساختگی متوقف شد.");
                return;
            }

            ushort sequence = unchecked((ushort)Interlocked.Increment(ref _eventSequence));
            uint timestampUs = unchecked((uint)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency));
            byte[] frame = BuildSyntheticTestFrame(sequence, timestampUs);
            lock (_serialWriteLock)
                serial.Write(frame, 0, frame.Length);
        }
        catch (Exception ex)
        {
            QueueSyntheticTestStop("ارسال فریم آزمایشی متوقف شد: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _syntheticSendInProgress, 0);
        }
    }

    private void QueueSyntheticTestStop(string message)
    {
        _syntheticTestRunning = false;
        try { _syntheticTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                StopSyntheticTest(false);
                AppendLog(message);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private static byte[] BuildSyntheticTestFrame(ushort sequence, uint timestampUs)
    {
        byte[] payload =
        {
            0x7E, (byte)sequence, (byte)(sequence >> 8), 0, 0, 0xA5, 0x5A, 0xC3
        };
        return BuildEventFrame(sequence, timestampUs, payload);
    }

    private static byte[] BuildEventFrame(ushort sequence, uint timestampUs, byte[] payload)
    {
        const int headerSize = 11;
        if (payload.Length > 16)
            throw new ArgumentOutOfRangeException(nameof(payload));

        byte[] frame = new byte[headerSize + payload.Length + 2];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = 1;
        frame[3] = 1;
        frame[4] = (byte)sequence;
        frame[5] = (byte)(sequence >> 8);
        frame[6] = (byte)payload.Length;
        frame[7] = (byte)timestampUs;
        frame[8] = (byte)(timestampUs >> 8);
        frame[9] = (byte)(timestampUs >> 16);
        frame[10] = (byte)(timestampUs >> 24);
        Array.Copy(payload, 0, frame, headerSize, payload.Length);
        ushort crc = ComputeCrc16(frame, 2, 9 + payload.Length);
        frame[headerSize + payload.Length] = (byte)crc;
        frame[headerSize + payload.Length + 1] = (byte)(crc >> 8);
        return frame;
    }

    private void ToggleCaptureOnly()
    {
        if (_captureOnlyRunning)
        {
            StopCaptureOnly(true);
            return;
        }
        if (_serial is null || !_serial.IsOpen)
        {
            MessageBox.Show(this, "ابتدا به پورت COM پیکوی مادر وصل شوید.", "اتصال لازم است", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_syntheticTestRunning)
        {
            MessageBox.Show(this, "ابتدا آزمون ساختگی را متوقف کنید.", "آزمون در حال اجراست", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string consent = "تا وقتی ارسال HID روشن است، برنامه ورودی کلید و ماوس این ویندوز را می‌خواند و به پیکوی کلاینت می‌فرستد. اگر جامپر فیزیکی GP14 به GND وصل باشد، پیکوی کلاینت آن ورودی را مثل صفحه‌کلید و ماوس USB به رایانهٔ مقصد می‌فرستد. محتوا ذخیره یا در گزارش نمایش داده نمی‌شود. فقط روی دستگاه‌هایی ادامه بده که مجاز به کنترلشان هستی و قبل از شروع مطمئن شو رایانهٔ مقصد آماده است. ادامه می‌دهی؟";
        if (MessageBox.Show(this, consent, "تأیید دریافت ورودی", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        lock (_captureQueueLock)
            _captureQueue.Clear();
        _captureDropped = 0;
        _captureSent = 0;
        Interlocked.Exchange(ref _lastCaptureHeartbeatTicks, Stopwatch.GetTimestamp());
        _captureOnlyRunning = true;
        try
        {
            _captureHooks.Start();
            uint timerResult = TimeBeginPeriod(1);
            _captureTimerResolutionActive = timerResult == 0;
            _captureOnlyButton.Text = "توقف ارسال HID";
            _syntheticButton.Enabled = false;
            AppendLog("--- ارسال HID روشن شد؛ اجرای ورودی فقط با جامپر فیزیکی کلاینت فعال است ---");
            AppendLog($"--- نرخ هدف ارسال: حداکثر {1000 / CaptureIntervalMs}Hz (بازهٔ {CaptureIntervalMs}ms؛ نرخ واقعی به زمان‌بندی Windows، USB و لینک بستگی دارد) ---");
            _captureTimer.Change(0, CaptureIntervalMs);
        }
        catch (Exception ex)
        {
            _captureOnlyRunning = false;
            _captureHooks.Stop();
            if (_captureTimerResolutionActive)
            {
                TimeEndPeriod(1);
                _captureTimerResolutionActive = false;
            }
            _captureOnlyButton.Text = "شروع ارسال HID";
            _syntheticButton.Enabled = true;
            MessageBox.Show(this, "فعال‌سازی ارسال ورودی ناموفق بود: " + ex.Message, "خطای ارسال HID", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EnqueueCaptureEvent(byte[] payload)
    {
        if (!_captureOnlyRunning)
            return;
        lock (_captureQueueLock)
        {
            if (!_captureOnlyRunning)
                return;
            if (payload.Length == 5 && payload[0] == 0x02 && _captureQueue.Count > 0 && _captureQueue.Last!.Value[0] == 0x02)
            {
                byte[] last = _captureQueue.Last.Value;
                short oldDx = unchecked((short)(last[1] | (last[2] << 8)));
                short oldDy = unchecked((short)(last[3] | (last[4] << 8)));
                short newDx = unchecked((short)(payload[1] | (payload[2] << 8)));
                short newDy = unchecked((short)(payload[3] | (payload[4] << 8)));
                int dx = Math.Clamp((int)oldDx + newDx, short.MinValue, short.MaxValue);
                int dy = Math.Clamp((int)oldDy + newDy, short.MinValue, short.MaxValue);
                last[1] = (byte)dx;
                last[2] = (byte)(dx >> 8);
                last[3] = (byte)dy;
                last[4] = (byte)(dy >> 8);
                return;
            }
            if (_captureQueue.Count >= 256)
            {
                Interlocked.Increment(ref _captureDropped);
                return;
            }
            _captureQueue.AddLast(payload);
        }
    }

    private void SendCaptureFrame(object? state)
    {
        // Timer callbacks can overlap at a 1 ms period. Skip a tick rather than
        // queueing concurrent serial writes; raw mouse deltas remain coalesced.
        if (Interlocked.Exchange(ref _captureSendInProgress, 1) != 0)
            return;

        try
        {
            lock (_serialWriteLock)
            {
                if (!_captureOnlyRunning)
                    return;

                byte[]? payload = null;
                lock (_captureQueueLock)
                {
                    if (_captureQueue.Count > 0)
                    {
                        payload = _captureQueue.First!.Value;
                        _captureQueue.RemoveFirst();
                    }
                }
                if (payload is null)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    long lastTicks = Interlocked.Read(ref _lastCaptureHeartbeatTicks);
                    if ((nowTicks - lastTicks) * 1000L / Stopwatch.Frequency < 100L)
                        return;
                    payload = new byte[] { 0x05 }; // heartbeat keeps client watchdog alive
                }

                SerialPort? serial = _serial;
                if (serial is null || !serial.IsOpen)
                {
                    QueueCaptureOnlyStop("پورت سریال بسته شد؛ ارسال HID متوقف شد.");
                    return;
                }

                try
                {
                    ushort sequence = unchecked((ushort)Interlocked.Increment(ref _eventSequence));
                    uint timestampUs = unchecked((uint)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency));
                    byte[] frame = BuildEventFrame(sequence, timestampUs, payload);
                    serial.Write(frame, 0, frame.Length);
                    Interlocked.Exchange(ref _lastCaptureHeartbeatTicks, Stopwatch.GetTimestamp());
                    Interlocked.Increment(ref _captureSent);
                }
                catch (Exception ex)
                {
                    QueueCaptureOnlyStop("ارسال HID متوقف شد: " + ex.Message);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _captureSendInProgress, 0);
        }
    }

    private void QueueCaptureOnlyStop(string message)
    {
        _captureOnlyRunning = false;
        try { _captureTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                StopCaptureOnly(false);
                AppendLog(message);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void StopCaptureOnly(bool writeLog)
    {
        bool wasRunning = _captureOnlyRunning;
        _captureOnlyRunning = false;
        try { _captureTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        _captureHooks.Stop();
        lock (_serialWriteLock) { }
        lock (_captureQueueLock)
            _captureQueue.Clear();
        if (_captureTimerResolutionActive)
        {
            TimeEndPeriod(1);
            _captureTimerResolutionActive = false;
        }
        _captureOnlyButton.Text = "شروع ارسال HID";
        _syntheticButton.Enabled = _serial is not null && _serial.IsOpen;
        if (writeLog && wasRunning)
            AppendLog($"--- ارسال HID متوقف شد: {_captureSent} فریم؛ {_captureDropped} مورد از صف کنار گذاشته شد؛ کلاینت با timeout کلیدها را آزاد می‌کند ---");
    }

    private static ushort ComputeCrc16(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private void PortDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var serial = _serial;
        if (serial is null || !serial.IsOpen)
            return;

        string chunk;
        try { chunk = serial.ReadExisting(); }
        catch { return; }

        var completedLines = new List<string>();
        lock (_bufferLock)
        {
            foreach (char ch in chunk)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n')
                {
                    if (_receiveBuffer.Length > 0)
                    {
                        completedLines.Add(_receiveBuffer.ToString());
                        _receiveBuffer.Clear();
                    }
                }
                else if (_receiveBuffer.Length < 512)
                {
                    _receiveBuffer.Append(ch);
                }
                else
                {
                    _receiveBuffer.Clear();
                }
            }
        }

        if (completedLines.Count == 0 || IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                foreach (string line in completedLines)
                    HandleLine(line);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void HandleLine(string line)
    {
        AppendLog(line);
        if (!line.StartsWith("MASTER,", StringComparison.Ordinal))
            return;

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in line.Split(',').Skip(1))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0)
                continue;
            if (long.TryParse(part[(equals + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                values[part[..equals]] = value;
        }

        long tx = Get(values, "tx");
        long ack = Get(values, "ack");
        long timeout = Get(values, "timeout");
        long crc = Get(values, "crc");
        long min = Get(values, "rtt_us_min");
        long avg = Get(values, "rtt_us_avg");
        long max = Get(values, "rtt_us_max");

        _counts.Text = $"گزارش حدود یک‌ثانیه‌ای: ارسال {tx} | پاسخ {ack} | timeout {timeout} | CRC {crc}";
        _rtt.Text = ack > 0 ? $"زمان رفت‌وبرگشت (RTT): کمینه {min} µs | میانگین {avg} µs | بیشینه {max} µs" : "RTT: هنوز پاسخ معتبر دریافت نشده";
        _lastReportUtc = DateTime.UtcNow;
        _sawMasterReport = true;

        if (ack == 0 && tx > 0)
        {
            _latestLinkStatus = "پیکوی مادر وصل است؛ از کلاینت ACK نرسیده";
            SetLinkStatus(_latestLinkStatus, System.Drawing.Color.Firebrick);
        }
        else if (timeout > 0 || crc > 0)
        {
            _latestLinkStatus = "لینک برقرار است، اما خطا/timeout دیده شد";
            SetLinkStatus(_latestLinkStatus, System.Drawing.Color.DarkOrange);
        }
        else if (ack > 0)
        {
            _latestLinkStatus = "لینک Pico مادر ↔ کلاینت پاسخ‌گو است";
            SetLinkStatus(_latestLinkStatus, System.Drawing.Color.DarkGreen);
        }
        else
        {
            _latestLinkStatus = "منتظر نمونهٔ PING/ACK";
            SetLinkStatus(_latestLinkStatus, System.Drawing.Color.DimGray);
        }
    }

    private void CheckConnectionState()
    {
        if (_serial is null || !_serial.IsOpen)
            return;

        try
        {
            if (!SerialPort.GetPortNames().Contains(_serial.PortName, StringComparer.OrdinalIgnoreCase))
            {
                AppendLog("--- پورت USB دیگر در ویندوز دیده نمی‌شود ---");
                Disconnect();
                SetUsbStatus("USB: اتصال Pico قطع شد", System.Drawing.Color.Firebrick);
                SetLinkStatus("لینک قابل بررسی نیست", System.Drawing.Color.Firebrick);
                return;
            }
        }
        catch { }

        if (!_sawMasterReport && (DateTime.UtcNow - _connectedUtc).TotalSeconds > 3)
        {
            SetLinkStatus("COM باز است، اما هنوز گزارشی از فریمور مادر نرسیده", System.Drawing.Color.DarkOrange);
            return;
        }

        if (_sawMasterReport && (DateTime.UtcNow - _lastReportUtc).TotalSeconds > 3)
        {
            SetLinkStatus("پورت COM باز است، اما گزارش فریمور متوقف شده", System.Drawing.Color.DarkOrange);
        }
    }

    private static long Get(Dictionary<string, long> values, string key) => values.TryGetValue(key, out long value) ? value : 0;

    private void Disconnect()
    {
        StopCaptureOnly(false);
        StopSyntheticTest(false);
        _syntheticButton.Enabled = false;
        _captureOnlyButton.Enabled = false;
        var serial = _serial;
        _serial = null;
        if (serial is not null)
        {
            try { serial.DataReceived -= PortDataReceived; } catch { }
            try { if (serial.IsOpen) serial.Close(); } catch { }
            serial.Dispose();
        }
        _connectButton.Text = "اتصال";
        _ports.Enabled = true;
        _refreshButton.Enabled = true;
        if (!IsDisposed)
        {
            SetUsbStatus("USB: وصل نشده", System.Drawing.Color.DimGray);
            SetLinkStatus("لینک بردبه‌برد: منتظر شروع", System.Drawing.Color.DimGray);
        }
    }

    private void SetUsbStatus(string text, System.Drawing.Color color)
    {
        _usbStatus.Text = text;
        _usbStatus.ForeColor = color;
    }

    private void SetLinkStatus(string text, System.Drawing.Color color)
    {
        _linkStatus.Text = "وضعیت لینک: " + text;
        _linkStatus.ForeColor = color;
    }

    private void AppendLog(string text)
    {
        if (IsDisposed)
            return;
        _log.AppendText(text + Environment.NewLine);
        if (_log.Lines.Length > 150)
        {
            string[] lines = _log.Lines;
            _log.Lines = lines.Skip(lines.Length - 100).ToArray();
        }
    }
}
