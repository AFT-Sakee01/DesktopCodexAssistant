using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class AiQuickMenuForm : Form
{
    private readonly WidgetForm ownerForm;
    private readonly WidgetSettings workingSettings;
    private readonly UiFontCache fontCache = new UiFontCache();
    private SettingsFluentToggleSwitch aiBlockToggle;
    private SettingsFluentToggleSwitch quotaPlanToggle;
    private Button ctfmonRestartButton;
    private Label statusLabel;
    private bool applyingControls;
    private int ctfmonRestartRequestRunning;

    public AiQuickMenuForm(WidgetForm owner, WidgetSettings settings)
    {
        this.ownerForm = owner;
        this.workingSettings = settings == null ? WidgetSettings.CreateDefaults() : settings.Clone();
        this.workingSettings.Normalize();

        this.Text = "特殊设置";
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.FormBorderStyle = FormBorderStyle.None;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ClientSize = new Size(480, 308);
        this.MinimumSize = new Size(420, 280);
        this.MaximumSize = new Size(500, 900);
        this.BackColor = SettingsFluentResources.WindowBase;
        this.ForeColor = SettingsFluentResources.TextPrimary;
        this.Font = GetUiFont(10.0f);
        this.Padding = new Padding(10);
        ApplicationIcon.ApplyTo(this);

        BuildUi();
        LoadFromSettings();
        UpdateStatus("特殊设置已打开。");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundedRegion();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.fontCache.Dispose();
        }

        base.Dispose(disposing);
    }

    private Font GetUiFont(float size)
    {
        return GetUiFont(size, FontStyle.Regular);
    }

    private Font GetUiFont(float size, FontStyle style)
    {
        return this.fontCache.GetUiPoint(size, style);
    }

    private void ApplyRoundedRegion()
    {
        if (this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        using (GraphicsPath path = SettingsFluentResources.CreateRoundRectangle(
            new Rectangle(0, 0, this.Width, this.Height),
            12))
        {
            Region oldRegion = this.Region;
            this.Region = new Region(path);
            if (oldRegion != null)
            {
                oldRegion.Dispose();
            }
        }
    }

    private void BuildUi()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.ColumnCount = 1;
        root.RowCount = 5;
        root.BackColor = this.BackColor;
        root.Padding = new Padding(12, 10, 12, 10);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        this.Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);

        this.aiBlockToggle = new SettingsFluentToggleSwitch();
        this.aiBlockToggle.CheckedChanged += OnAiBlockToggleChanged;
        root.Controls.Add(BuildActionRow(
            "链接阻断",
            "阻断本程序的 OpenAI / ChatGPT / Claude 请求。",
            this.aiBlockToggle),
            0,
            1);

        this.quotaPlanToggle = new SettingsFluentToggleSwitch();
        this.quotaPlanToggle.CheckedChanged += OnQuotaPlanToggleChanged;
        root.Controls.Add(BuildActionRow(
            "额度计划",
            "只切换启用状态；阈值和 goal 列表在普通设置中调整。",
            this.quotaPlanToggle),
            0,
            2);

        this.ctfmonRestartButton = SettingsFluentResources.CreateCommandButton("重启", true, GetUiFont(9.2f, FontStyle.Bold));
        this.ctfmonRestartButton.AutoSize = false;
        this.ctfmonRestartButton.Width = 112;
        this.ctfmonRestartButton.Height = 42;
        this.ctfmonRestartButton.Margin = new Padding(0);
        this.ctfmonRestartButton.Click += delegate { BeginRestartCtfmonFromSpecialMenu(); };
        root.Controls.Add(BuildActionRow(
            "CTF 重启",
            "提权重启当前会话的 ctfmon.exe。",
            this.ctfmonRestartButton),
            0,
            3);

        this.statusLabel = new Label();
        this.statusLabel.Dock = DockStyle.Fill;
        this.statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        this.statusLabel.AutoEllipsis = true;
        this.statusLabel.ForeColor = SettingsFluentResources.TextTertiary;
        this.statusLabel.Font = GetUiFont(8.5f);
        this.statusLabel.Padding = new Padding(4, 0, 0, 0);
        root.Controls.Add(this.statusLabel, 0, 4);
    }

    private Control BuildHeader()
    {
        TableLayoutPanel header = new TableLayoutPanel();
        header.Dock = DockStyle.Fill;
        header.ColumnCount = 2;
        header.RowCount = 1;
        header.BackColor = this.BackColor;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));

        Label title = new Label();
        title.Dock = DockStyle.Fill;
        title.Text = "特殊设置";
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Font = GetUiFont(15.0f, FontStyle.Bold);
        title.ForeColor = SettingsFluentResources.TextPrimary;
        header.Controls.Add(title, 0, 0);

        Button closeButton = SettingsFluentResources.CreateCommandButton("×", false, GetUiFont(11.0f, FontStyle.Bold));
        closeButton.AutoSize = false;
        closeButton.Width = 34;
        closeButton.Height = 34;
        closeButton.Padding = new Padding(0);
        closeButton.Margin = new Padding(0, 4, 0, 0);
        closeButton.Click += delegate { this.Close(); };
        header.Controls.Add(closeButton, 1, 0);

        return header;
    }

    private Control BuildActionRow(string title, string hint, Control actionControl)
    {
        TableLayoutPanel row = new TableLayoutPanel();
        row.Dock = DockStyle.Fill;
        row.ColumnCount = 2;
        row.RowCount = 1;
        row.Margin = new Padding(0, 5, 0, 5);
        row.Padding = new Padding(14, 7, 12, 7);
        row.BackColor = SettingsFluentResources.CardRest;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        row.Paint += PaintRowBackground;

        Panel textPanel = new Panel();
        textPanel.Dock = DockStyle.Fill;
        textPanel.BackColor = Color.Transparent;

        Label titleLabel = new Label();
        titleLabel.Text = title;
        titleLabel.SetBounds(0, 0, 260, 23);
        titleLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        titleLabel.Font = GetUiFont(10.0f, FontStyle.Bold);
        titleLabel.ForeColor = SettingsFluentResources.TextPrimary;
        titleLabel.BackColor = Color.Transparent;
        textPanel.Controls.Add(titleLabel);

        Label hintLabel = new Label();
        hintLabel.Text = hint;
        hintLabel.SetBounds(0, 24, 280, 22);
        hintLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        hintLabel.Font = GetUiFont(8.2f);
        hintLabel.ForeColor = SettingsFluentResources.TextTertiary;
        hintLabel.BackColor = Color.Transparent;
        hintLabel.AutoEllipsis = true;
        textPanel.Controls.Add(hintLabel);

        row.Controls.Add(textPanel, 0, 0);

        Panel actionHost = new Panel();
        actionHost.Dock = DockStyle.Fill;
        actionHost.BackColor = Color.Transparent;
        actionControl.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        actionControl.Location = new Point(Math.Max(0, actionHost.Width - actionControl.Width), Math.Max(0, (48 - actionControl.Height) / 2));
        actionHost.Resize += delegate
        {
            actionControl.Location = new Point(
                Math.Max(0, actionHost.ClientSize.Width - actionControl.Width),
                Math.Max(0, (actionHost.ClientSize.Height - actionControl.Height) / 2));
        };
        actionHost.Controls.Add(actionControl);
        row.Controls.Add(actionHost, 1, 0);

        return row;
    }

    private static void PaintRowBackground(object sender, PaintEventArgs e)
    {
        Control row = sender as Control;
        if (row == null)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = SettingsFluentResources.CreateRoundRectangle(
            new Rectangle(0, 0, row.Width - 1, row.Height - 1),
            8))
        using (SolidBrush brush = new SolidBrush(SettingsFluentResources.CardRest))
        using (Pen pen = new Pen(SettingsFluentResources.StrokeColor, 1))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
    }

    private void LoadFromSettings()
    {
        this.applyingControls = true;
        try
        {
            this.aiBlockToggle.SetCheckedSilent(this.workingSettings.AiRequestProtectionManualBlockEnabled);
            this.quotaPlanToggle.SetCheckedSilent(this.workingSettings.CodexQuotaPlanEnabled);
        }
        finally
        {
            this.applyingControls = false;
        }
    }

    private void OnAiBlockToggleChanged(object sender, EventArgs e)
    {
        if (this.applyingControls || this.ownerForm == null || this.ownerForm.IsDisposed)
        {
            return;
        }

        bool enabled = this.aiBlockToggle.Checked;
        this.workingSettings.AiRequestProtectionManualBlockEnabled = enabled;
        if (this.ownerForm.SetAiRequestBlockingFromOperationPanel(enabled))
        {
            UpdateStatus(enabled ? "链接阻断已开启。" : "链接阻断已关闭。");
        }
    }

    private void OnQuotaPlanToggleChanged(object sender, EventArgs e)
    {
        if (this.applyingControls || this.ownerForm == null || this.ownerForm.IsDisposed)
        {
            return;
        }

        bool enabled = this.quotaPlanToggle.Checked;
        this.workingSettings.CodexQuotaPlanEnabled = enabled;
        if (this.ownerForm.SetCodexQuotaPlanFromOperationPanel(enabled))
        {
            UpdateStatus(enabled ? "额度计划已启用。" : "额度计划已关闭。");
        }
    }

    private void BeginRestartCtfmonFromSpecialMenu()
    {
        if (Interlocked.CompareExchange(ref this.ctfmonRestartRequestRunning, 1, 0) != 0)
        {
            UpdateStatus("CTF 重启已在执行。");
            return;
        }

        string correlationId = Guid.NewGuid().ToString("N");
        Stopwatch stopwatch = Stopwatch.StartNew();
        Program.LogInfo("special_menu_ctfmon_restart_requested correlation_id=" + correlationId);
        SetCtfmonRestartBusy(true);

        Task.Run(delegate
        {
            bool success = false;
            string detail = string.Empty;
            try
            {
                success = Program.RunElevatedCtfmonRestartHelper(correlationId, out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                Program.LogInfo(
                    "special_menu_ctfmon_restart_completed correlation_id=" +
                    correlationId +
                    ", success=" +
                    success.ToString() +
                    ", elapsed_ms=" +
                    stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    ", detail=" +
                    detail);
                Interlocked.Exchange(ref this.ctfmonRestartRequestRunning, 0);
            }

            TryUpdateCtfmonRestartResult(success);
        });
    }

    private void SetCtfmonRestartBusy(bool busy)
    {
        if (this.ctfmonRestartButton != null)
        {
            this.ctfmonRestartButton.Enabled = !busy;
            this.ctfmonRestartButton.Text = busy ? "等待" : "重启";
        }

        UpdateStatus(busy ? "正在等待管理员授权并重启 CTF..." : this.statusLabel.Text);
    }

    private void TryUpdateCtfmonRestartResult(bool success)
    {
        try
        {
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }

            this.BeginInvoke((MethodInvoker)delegate
            {
                if (this.IsDisposed)
                {
                    return;
                }

                SetCtfmonRestartBusy(false);
                UpdateStatus(success ? "CTF 输入服务已重启。" : "CTF 重启失败或已取消，详见日志。");
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateStatus(string text)
    {
        if (this.statusLabel != null)
        {
            this.statusLabel.Text = text ?? string.Empty;
        }
    }

    internal static void RunSelfTest()
    {
        using (AiQuickMenuForm form = new AiQuickMenuForm(null, WidgetSettings.CreateDefaults()))
        {
            AssertSelfTest(form.Width <= 500, "special settings window width must not exceed 500 px");
            AssertSelfTest(CountControls<SettingsFluentToggleSwitch>(form) == 2, "special settings should expose two toggles");
            AssertSelfTest(CountActionButtons(form) == 2, "special settings should expose close and CTF buttons only");
            AssertSelfTest(CountControls<ComboBox>(form) == 0, "quota plan detail combo boxes belong to normal settings");
            AssertSelfTest(CountControls<NumericUpDown>(form) == 0, "quota plan threshold boxes belong to normal settings");
            AssertSelfTest(CountControls<CheckedListBox>(form) == 0, "quota plan goal lists belong to normal settings");
        }
    }

    private static int CountActionButtons(Control root)
    {
        return CountControls<Button>(root);
    }

    private static int CountControls<TControl>(Control root) where TControl : Control
    {
        int count = root is TControl ? 1 : 0;
        for (int i = 0; i < root.Controls.Count; i++)
        {
            count += CountControls<TControl>(root.Controls[i]);
        }

        return count;
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Special settings self-test failed: " + message);
        }
    }
}
