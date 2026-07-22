using System;
using System.Drawing;
using System.Windows.Forms;

// Full-screen warning shown only when the egress IP is positively identified as mainland China
// (or the existing GFW probe positively reports wall-inside) while the guard is on. Unknown
// egress still blocks this app's AI traffic, but does not raise this warning. It is a plain top-most
// form rather than a layered widget: the whole point is to be unmissable, so it deliberately does
// not blend into the desktop.
//
// It never locks the machine — the user asked for a warning, not a lockout — so it stays
// dismissable via a temporary-hide button. WidgetForm owns the show/hide decision and the
// re-show cooldown; this form only renders and reports the hide request.
internal sealed class ChinaEgressWarningForm : Form
{
    private readonly Label reasonLabel;
    private bool closingFromOwner;

    public event EventHandler HideForCooldownRequested;

    public ChinaEgressWarningForm()
    {
        ApplicationIcon.ApplyTo(this);
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(122, 16, 24);
        this.Text = "中国大陆网络警告";
        this.AccessibleName = "ChinaEgressWarning";
        this.KeyPreview = true;

        TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent,
            Padding = new Padding(48)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // Top and bottom spacer rows center the block vertically without hand-computed offsets.
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        for (int i = 0; i < 3; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        Label title = new Label
        {
            Text = "⚠ 处于中国大陆网络",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Color.White,
            Font = new Font(this.Font.FontFamily, 34f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 24)
        };

        Label body = new Label
        {
            Text = "已阻止本程序访问 Anthropic 与 OpenAI。\n" +
                   "从中国大陆 IP 访问这些服务会违反其用户协议。\n" +
                   "请勿手动打开 Claude、ChatGPT 或相关工具。\n" +
                   "断网或改接境外网络后，本警告会自动消失。",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(255, 224, 224),
            Font = new Font(this.Font.FontFamily, 16f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 16)
        };

        this.reasonLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(255, 190, 190),
            Font = new Font(this.Font.FontFamily, 12f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 28)
        };

        Button hideButton = new Button
        {
            Text = "暂时隐藏（60 秒）",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(158, 34, 44),
            Font = new Font(this.Font.FontFamily, 12f, FontStyle.Regular),
            Padding = new Padding(18, 8, 18, 8),
            Margin = new Padding(0, 8, 0, 0),
            TabStop = false
        };
        hideButton.FlatAppearance.BorderColor = Color.FromArgb(220, 120, 128);
        hideButton.Click += delegate { RaiseHideForCooldown(); };

        // Stack title / body / (reason + button) — reason and button share a sub-panel so both
        // sit in the same auto-sized row beneath the copy.
        FlowLayoutPanel bottom = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.TopDown,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        bottom.Controls.Add(this.reasonLabel);
        FlowLayoutPanel buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        buttonRow.Controls.Add(hideButton);
        bottom.Controls.Add(buttonRow);

        layout.Controls.Add(title, 0, 1);
        layout.Controls.Add(body, 0, 2);
        layout.Controls.Add(bottom, 0, 3);
        this.Controls.Add(layout);
    }

    protected override bool ShowWithoutActivation
    {
        // Prominent but not focus-stealing: the user can keep interacting with whatever they need
        // to disconnect the network.
        get { return true; }
    }

    public void ShowReason(string reason)
    {
        this.reasonLabel.Text = string.IsNullOrWhiteSpace(reason) ? string.Empty : "检测依据：" + reason.Trim();
        Rectangle bounds = Screen.PrimaryScreen == null ? new Rectangle(0, 0, 1280, 800) : Screen.PrimaryScreen.Bounds;
        this.Bounds = bounds;
        if (!this.Visible)
        {
            Show();
        }

        // Re-assert top-most without activating, so a later-shown window does not bury the warning.
        NativeMethods.SetWindowPos(
            this.Handle,
            NativeMethods.HWND_TOPMOST,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE);
    }

    public void CloseFromOwner()
    {
        this.closingFromOwner = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!this.closingFromOwner && e.CloseReason == CloseReason.UserClosing)
        {
            // Alt+F4/system-close is equivalent to the explicit temporary-hide action. It must
            // not permanently defeat the warning while the confirmed mainland condition remains.
            e.Cancel = true;
            RaiseHideForCooldown();
            return;
        }

        base.OnFormClosing(e);
    }

    private void RaiseHideForCooldown()
    {
        EventHandler handler = this.HideForCooldownRequested;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }
}
