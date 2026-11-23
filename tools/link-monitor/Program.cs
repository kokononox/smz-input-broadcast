using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
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
    private readonly ComboBox _ports = new();
    private readonly Button _refreshButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _syntheticButton = new();
    private readonly System.Windows.Forms.Timer _syntheticTimer = new();
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
    private bool _syntheticTestRunning;
    private ushort _syntheticSequence;
    private string _latestLinkStatus = "منتظر گزارش فریمور مادر";

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
        _syntheticTimer.Interval = 20;
        _syntheticTimer.Tick += (_, _) => SendSyntheticTestFrame();
        FormClosing += (_, _) => Disconnect();
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
            Text = "دکمهٔ آزمون فقط فریم ساختگی می‌فرستد؛ ورودی واقعی موس/صفحه‌کلید ضبط یا ارسال نمی‌شود.",
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

        _syntheticSequence = 0;
        _syntheticTestRunning = true;
        _syntheticButton.Text = "توقف آزمون";
        AppendLog("--- آزمون دستی فریم ساختگی آغاز شد؛ ورودی واقعی خوانده نمی‌شود ---");
        _syntheticTimer.Start();
    }

    private void StopSyntheticTest(bool writeLog)
    {
        bool wasRunning = _syntheticTestRunning;
        _syntheticTimer.Stop();
        _syntheticTestRunning = false;
        _syntheticButton.Text = "شروع آزمون ساختگی";
        if (writeLog && wasRunning)
            AppendLog("--- آزمون فریم ساختگی متوقف شد ---");
    }

    private void SendSyntheticTestFrame()
    {
        SerialPort? serial = _serial;
        if (serial is null || !serial.IsOpen)
        {
            StopSyntheticTest(true);
            return;
        }

        try
        {
            _syntheticSequence++;
            uint timestampUs = unchecked((uint)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency));
            byte[] frame = BuildSyntheticTestFrame(_syntheticSequence, timestampUs);
            serial.Write(frame, 0, frame.Length);
        }
        catch (Exception ex)
        {
            StopSyntheticTest(false);
            AppendLog("ارسال فریم آزمایشی متوقف شد: " + ex.Message);
        }
    }

    private static byte[] BuildSyntheticTestFrame(ushort sequence, uint timestampUs)
    {
        const int payloadLength = 8;
        const int headerSize = 11;
        byte[] frame = new byte[headerSize + payloadLength + 2];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = 1;
        frame[3] = 1;
        frame[4] = (byte)sequence;
        frame[5] = (byte)(sequence >> 8);
        frame[6] = payloadLength;
        frame[7] = (byte)timestampUs;
        frame[8] = (byte)(timestampUs >> 8);
        frame[9] = (byte)(timestampUs >> 16);
        frame[10] = (byte)(timestampUs >> 24);
        frame[11] = 0x7E;
        frame[12] = (byte)sequence;
        frame[13] = (byte)(sequence >> 8);
        frame[14] = 0;
        frame[15] = 0;
        frame[16] = 0xA5;
        frame[17] = 0x5A;
        frame[18] = 0xC3;
        ushort crc = ComputeCrc16(frame, 2, 9 + payloadLength);
        frame[19] = (byte)crc;
        frame[20] = (byte)(crc >> 8);
        return frame;
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
        StopSyntheticTest(false);
        _syntheticButton.Enabled = false;
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
