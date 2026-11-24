using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MouseSettingsForm());
    }
}

internal sealed class MouseSettingsForm : Form
{
    private readonly Label _status = new();
    private readonly Button _readButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _applyButton = new();

    public MouseSettingsForm()
    {
        Text = "SMZ — همسان‌سازی تنظیمات ماوس";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(620, 300);
        Size = new System.Drawing.Size(720, 350);
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new System.Drawing.Font("Segoe UI", 10F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        _readButton.Text = "خواندن تنظیمات این سیستم";
        _readButton.AutoSize = true;
        _readButton.Click += (_, _) => ReadAndDisplay();
        buttons.Controls.Add(_readButton);

        _exportButton.Text = "ذخیره برای انتقال از مادر";
        _exportButton.AutoSize = true;
        _exportButton.Click += (_, _) => ExportSettings();
        buttons.Controls.Add(_exportButton);

        _applyButton.Text = "اعمال فایل روی این سیستم";
        _applyButton.AutoSize = true;
        _applyButton.Click += (_, _) => ApplySettings();
        buttons.Controls.Add(_applyButton);
        root.Controls.Add(buttons, 0, 0);

        _status.Dock = DockStyle.Fill;
        _status.AutoSize = false;
        _status.BorderStyle = BorderStyle.FixedSingle;
        _status.TextAlign = System.Drawing.ContentAlignment.TopRight;
        _status.Padding = new Padding(12);
        _status.Text = "این ابزار فقط تنظیمات ماوس ویندوزِ کاربر فعلی را می‌خواند یا با تأیید شما تغییر می‌دهد.\r\n\r\nروی سیستم مادر، «ذخیره برای انتقال از مادر» را بزنید. فایل JSON را به کلاینت منتقل کنید، همین ابزار را آنجا اجرا کنید و «اعمال فایل روی این سیستم» را بزنید. پیش از تغییر، پشتیبان تنظیمات فعلی کلاینت روی Desktop ذخیره می‌شود. هیچ سرویس پس‌زمینه یا ارتباط شبکه‌ای ایجاد نمی‌شود.";
        root.Controls.Add(_status, 0, 1);

        var note = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = System.Drawing.Color.DimGray,
            Text = "این ابزار سرعت نشانگر و شتاب/آستانه‌های ویندوز را همسان می‌کند؛ DPI سخت‌افزاری ماوس را تغییر نمی‌دهد. برای بازگردانی، فایل پشتیبان کلاینت را با همین ابزار اعمال کنید.",
            TextAlign = System.Drawing.ContentAlignment.MiddleRight
        };
        root.Controls.Add(note, 0, 2);
    }

    private void ReadAndDisplay()
    {
        try
        {
            MouseSettings settings = MouseSettings.Read();
            _status.Text = FormatSettings("تنظیمات فعلی این سیستم:", settings);
        }
        catch (Exception ex)
        {
            ShowError("خواندن تنظیمات ناموفق بود", ex);
        }
    }

    private void ExportSettings()
    {
        try
        {
            MouseSettings settings = MouseSettings.Read();
            using var dialog = new SaveFileDialog
            {
                Title = "ذخیرهٔ تنظیمات ماوس سیستم مادر",
                FileName = "smz-mouse-settings.json",
                Filter = "فایل تنظیمات SMZ (*.json)|*.json",
                AddExtension = true,
                DefaultExt = "json",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var file = new MouseSettingsFile
            {
                Version = 1,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                PointerSpeed = settings.PointerSpeed,
                Threshold1 = settings.Threshold1,
                Threshold2 = settings.Threshold2,
                Acceleration = settings.Acceleration
            };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(file, JsonOptions));
            _status.Text = FormatSettings("فایل تنظیمات ذخیره شد:", settings) + "\r\n\r\n" + dialog.FileName;
        }
        catch (Exception ex)
        {
            ShowError("ذخیرهٔ تنظیمات ناموفق بود", ex);
        }
    }

    private void ApplySettings()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "انتخاب فایل تنظیمات صادرشده از سیستم مادر",
            Filter = "فایل تنظیمات SMZ (*.json)|*.json|همهٔ فایل‌ها (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            MouseSettingsFile file = JsonSerializer.Deserialize<MouseSettingsFile>(File.ReadAllText(dialog.FileName), JsonOptions)
                ?? throw new InvalidDataException("فایل تنظیمات خالی یا نامعتبر است.");
            if (file.Version != 1 || file.PointerSpeed is < 1 or > 20 ||
                file.Threshold1 < 0 || file.Threshold2 < 0 || file.Acceleration is < 0 or > 2)
                throw new InvalidDataException("نسخه یا مقادیر فایل تنظیمات پشتیبانی نمی‌شوند.");

            MouseSettings target = MouseSettings.Read();
            var source = new MouseSettings(file.PointerSpeed, file.Threshold1, file.Threshold2, file.Acceleration);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string backupPath = Path.Combine(desktop, $"SMZ-mouse-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            string message = FormatSettings("تنظیمات فعلی کلاینت:", target) + "\r\n\r\n" +
                             FormatSettings("تنظیماتی که از فایل اعمال می‌شوند:", source) + "\r\n\r\n" +
                             $"با تأیید، پشتیبان فعلی در این مسیر ذخیره و تنظیمات ویندوزِ کاربر فعلی تغییر می‌کند:\r\n{backupPath}\r\n\r\nادامه می‌دهی؟";
            if (MessageBox.Show(this, message, "تأیید تغییر تنظیمات ماوس", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var backup = new MouseSettingsFile
            {
                Version = 1,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                PointerSpeed = target.PointerSpeed,
                Threshold1 = target.Threshold1,
                Threshold2 = target.Threshold2,
                Acceleration = target.Acceleration
            };
            File.WriteAllText(backupPath, JsonSerializer.Serialize(backup, JsonOptions));

            try
            {
                source.Apply();
                MouseSettings verified = MouseSettings.Read();
                if (verified != source)
                    throw new InvalidOperationException("تنظیمات پس از اعمال با مقادیر فایل برابر نشدند.");
                _status.Text = FormatSettings("تنظیمات اعمال و بررسی شد:", verified) + "\r\n\r\nپشتیبان قبلی:\r\n" + backupPath;
                MessageBox.Show(this, "تنظیمات اعمال شد. اگر نتیجه مطلوب نبود، فایل پشتیبان را با همین ابزار اعمال کن.", "انجام شد", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                try { target.Apply(); } catch { /* Preserve the original error; backup file is available too. */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            ShowError("اعمال تنظیمات ناموفق بود", ex);
        }
    }

    private static string FormatSettings(string title, MouseSettings settings) =>
        $"{title}\r\nسرعت نشانگر: {settings.PointerSpeed} از ۲۰\r\n" +
        $"شتاب ماوس: {settings.Acceleration} | آستانهٔ ۱: {settings.Threshold1} | آستانهٔ ۲: {settings.Threshold2}";

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal sealed class MouseSettingsFile
{
    public int Version { get; set; }
    public DateTimeOffset ExportedAtUtc { get; set; }
    public int PointerSpeed { get; set; }
    public int Threshold1 { get; set; }
    public int Threshold2 { get; set; }
    public int Acceleration { get; set; }
}

internal readonly record struct MouseSettings(int PointerSpeed, int Threshold1, int Threshold2, int Acceleration)
{
    private const uint SpiGetMouse = 0x0003;
    private const uint SpiSetMouse = 0x0004;
    private const uint SpiGetMouseSpeed = 0x0070;
    private const uint SpiSetMouseSpeed = 0x0071;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseAccelerationParameters
    {
        public int Threshold1;
        public int Threshold2;
        public int Acceleration;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoMouse(uint action, uint param, ref MouseAccelerationParameters value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoSpeed(uint action, uint param, ref int value, uint flags);

    public static MouseSettings Read()
    {
        var acceleration = new MouseAccelerationParameters();
        if (!SystemParametersInfoMouse(SpiGetMouse, 0, ref acceleration, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows mouse acceleration settings could not be read.");

        int speed = 0;
        if (!SystemParametersInfoSpeed(SpiGetMouseSpeed, 0, ref speed, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows pointer speed could not be read.");

        return new MouseSettings(speed, acceleration.Threshold1, acceleration.Threshold2, acceleration.Acceleration);
    }

    public void Apply()
    {
        var acceleration = new MouseAccelerationParameters
        {
            Threshold1 = Threshold1,
            Threshold2 = Threshold2,
            Acceleration = Acceleration
        };
        uint flags = SpifUpdateIniFile | SpifSendChange;
        if (!SystemParametersInfoMouse(SpiSetMouse, 0, ref acceleration, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows mouse acceleration settings could not be changed.");

        int speed = PointerSpeed;
        if (!SystemParametersInfoSpeed(SpiSetMouseSpeed, 0, ref speed, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows pointer speed could not be changed.");
    }
}
