using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class SettingsFluentResources
{
    public static readonly Color WindowBase = DesignTokens.SettingsWarmTheme.WindowBase;
    public static readonly Color CardRest = DesignTokens.SettingsWarmTheme.CardRest;
    public static readonly Color CardHover = DesignTokens.SettingsWarmTheme.CardHover;
    public static readonly Color StrokeColor = DesignTokens.SettingsWarmTheme.DividerLines;
    public static readonly Color DividerColor = DesignTokens.SettingsWarmTheme.DividerLines;
    public static readonly Color ControlBg = DesignTokens.SettingsWarmTheme.InputBackground;
    public static readonly Color ControlBorder = DesignTokens.SettingsWarmTheme.DividerLines;
    public static readonly Color TextPrimary = DesignTokens.SettingsWarmTheme.TextPrimary;
    public static readonly Color TextSecondary = DesignTokens.SettingsWarmTheme.TextSecondary;
    public static readonly Color TextTertiary = DesignTokens.SettingsWarmTheme.TextMuted;
    public static readonly Color Accent = DesignTokens.SettingsWarmTheme.Accent;
    public static readonly Color AccentHover = DesignTokens.SettingsWarmTheme.AccentHover;
    public static readonly Color AccentPressed = DesignTokens.SettingsWarmTheme.AccentPressed;

    public static Button CreateCommandButton(string text, bool primary, Font font)
    {
        Button button = new Button();
        button.Text = text;
        button.AutoSize = true;
        button.Padding = new Padding(24, 0, 24, 0);
        button.Height = 54;
        button.Margin = new Padding(0, 0, 12, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = font;
        button.Cursor = Cursors.Hand;
        button.BackColor = WindowBase;
        button.ForeColor = primary ? Color.Black : TextSecondary;

        Color backBase = primary ? Accent : DesignTokens.SettingsWarmTheme.ButtonRest;
        Color backHover = primary ? AccentHover : DesignTokens.SettingsWarmTheme.ButtonHover;
        Color backDown = primary ? AccentPressed : DesignTokens.SettingsWarmTheme.ButtonPressed;
        Color borderColor = primary ? Accent : ControlBorder;
        bool hover = false;
        bool down = false;

        button.MouseEnter += delegate { hover = true; button.Invalidate(); };
        button.MouseLeave += delegate { hover = false; button.Invalidate(); };
        button.MouseDown += delegate { down = true; button.Invalidate(); };
        button.MouseUp += delegate { down = false; button.Invalidate(); };
        button.Paint += delegate(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(button.Parent != null ? button.Parent.BackColor : WindowBase);
            Color currentBack = down ? backDown : (hover ? backHover : backBase);
            using (GraphicsPath path = CreateRoundRectangle(new Rectangle(0, 0, button.Width - 1, button.Height - 1), 6))
            {
                using (SolidBrush brush = new SolidBrush(currentBack))
                {
                    e.Graphics.FillPath(brush, path);
                }

                using (Pen pen = new Pen(borderColor, 1))
                {
                    e.Graphics.DrawPath(pen, path);
                }
            }

            TextRenderer.DrawText(
                e.Graphics,
                button.Text,
                button.Font,
                button.ClientRectangle,
                button.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        return button;
    }

    public static ComboBox CreateComboBox(Font font, int width)
    {
        ComboBox combo = new ComboBox();
        combo.Width = width;
        combo.Height = 54;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = ControlBg;
        combo.ForeColor = TextSecondary;
        combo.Font = font;
        return combo;
    }

    public static NumericUpDown CreatePercentBox(Font font, int width)
    {
        NumericUpDown box = new NumericUpDown();
        box.Width = width;
        box.Height = 54;
        box.Minimum = WidgetSettings.MinCodexQuotaPlanThresholdPercent;
        box.Maximum = WidgetSettings.MaxCodexQuotaPlanThresholdPercent;
        box.BackColor = ControlBg;
        box.ForeColor = TextSecondary;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = font;
        return box;
    }

    public static CheckedListBox CreateCheckedListBox(Font font)
    {
        CheckedListBox list = new CheckedListBox();
        list.CheckOnClick = true;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.BackColor = ControlBg;
        list.ForeColor = TextPrimary;
        list.Font = font;
        list.HorizontalScrollbar = true;
        list.IntegralHeight = false;
        list.Height = 160;
        list.Width = 520;
        return list;
    }

    public static GraphicsPath CreateRoundRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static int GetWrappedTextHeight(string text, Font font, int width, int verticalPadding)
    {
        int safeWidth = Math.Max(80, width);
        Size measured = TextRenderer.MeasureText(
            string.IsNullOrEmpty(text) ? " " : text,
            font,
            new Size(safeWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return Math.Max(GetSingleLineHeight(font, verticalPadding), measured.Height + verticalPadding);
    }

    private static int GetSingleLineHeight(Font font, int verticalPadding)
    {
        return Math.Max(24, TextRenderer.MeasureText("测量文字 Ag", font).Height + verticalPadding);
    }
}

internal sealed class SettingsFluentToggleSwitch : Control
{
    private bool isChecked;
    private float animProgress;
    private readonly Timer animTimer;
    private bool hover;

    public event EventHandler CheckedChanged;

    public SettingsFluentToggleSwitch()
    {
        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        this.Size = new Size(70, 35);
        this.BackColor = Color.Transparent;
        this.Cursor = Cursors.Hand;
        this.animProgress = 0.0f;
        this.animTimer = new Timer();
        this.animTimer.Interval = 16;
        this.animTimer.Tick += OnAnimTick;
    }

    public bool Checked
    {
        get { return this.isChecked; }
        set
        {
            if (this.isChecked == value)
            {
                return;
            }

            this.isChecked = value;
            StartAnimation();
            EventHandler handler = this.CheckedChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }

    public void SetCheckedSilent(bool value)
    {
        this.isChecked = value;
        this.animProgress = value ? 1.0f : 0.0f;
        this.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle trackRect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
        using (GraphicsPath path = SettingsFluentResources.CreateRoundRectangle(trackRect, this.Height / 2))
        {
            if (this.isChecked)
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(trackRect, SettingsFluentResources.Accent, SettingsFluentResources.AccentHover, 0f))
                {
                    g.FillPath(brush, path);
                }
            }
            else
            {
                Color trackColor = this.hover ? DesignTokens.SettingsWarmTheme.ToggleTrackHover : DesignTokens.SettingsWarmTheme.ToggleTrackOff;
                using (SolidBrush brush = new SolidBrush(trackColor))
                {
                    g.FillPath(brush, path);
                }

                using (Pen pen = new Pen(SettingsFluentResources.ControlBorder, 1.2f))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        float knobDiameter = this.Height - 8;
        float knobMinX = 4;
        float knobMaxX = this.Width - knobDiameter - 4;
        float knobX = knobMinX + (knobMaxX - knobMinX) * this.animProgress;
        float knobY = 4;

        using (SolidBrush shadow = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
        {
            g.FillEllipse(shadow, knobX + 0.5f, knobY + 0.8f, knobDiameter, knobDiameter);
        }

        using (SolidBrush knobBrush = new SolidBrush(DesignTokens.SettingsWarmTheme.ToggleKnob))
        {
            g.FillEllipse(knobBrush, knobX, knobY, knobDiameter, knobDiameter);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        this.Checked = !this.isChecked;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        this.hover = true;
        this.Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        this.hover = false;
        this.Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && this.animTimer != null)
        {
            this.animTimer.Stop();
            this.animTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void StartAnimation()
    {
        this.animTimer.Start();
    }

    private void OnAnimTick(object sender, EventArgs e)
    {
        float target = this.isChecked ? 1.0f : 0.0f;
        const float Step = 0.18f;
        if (Math.Abs(this.animProgress - target) < Step)
        {
            this.animProgress = target;
            this.animTimer.Stop();
        }
        else
        {
            this.animProgress += this.isChecked ? Step : -Step;
        }

        this.Invalidate();
    }
}

internal sealed class SettingsFluentGroupCard : Panel
{
    private readonly List<SettingsFluentRow> rows = new List<SettingsFluentRow>();
    private bool layoutInProgress;

    public SettingsFluentGroupCard()
    {
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        this.BackColor = Color.Transparent;
        this.Margin = new Padding(0, 0, 0, 14);
    }

    public void AddRow(SettingsFluentRow row)
    {
        this.rows.Add(row);
        this.Controls.Add(row);
    }

    public void LayoutRows()
    {
        if (this.layoutInProgress)
        {
            return;
        }

        this.layoutInProgress = true;
        try
        {
            int y = 0;
            bool first = true;
            for (int i = 0; i < this.rows.Count; i++)
            {
                SettingsFluentRow row = this.rows[i];
                row.ShowTopDivider = !first;
                int height = row.ComputeDesiredHeight(this.Width);
                row.SetBounds(0, y, this.Width, height);
                y += height;
                first = false;
            }

            this.Height = Math.Max(1, y);
            UpdateClipRegion();
        }
        finally
        {
            this.layoutInProgress = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
        using (GraphicsPath path = SettingsFluentResources.CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
        using (SolidBrush background = new SolidBrush(SettingsFluentResources.CardRest))
        {
            g.FillPath(background, path);
        }

        using (GraphicsPath accent = SettingsFluentResources.CreateRoundRectangle(new Rectangle(0, 0, 3, this.Height), 1))
        using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 0, 3, this.Height), SettingsFluentResources.Accent, SettingsFluentResources.AccentHover, 90f))
        {
            g.FillPath(brush, accent);
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (!this.layoutInProgress)
        {
            LayoutRows();
        }
    }

    private void UpdateClipRegion()
    {
        if (this.Width > 1 && this.Height > 1)
        {
            using (GraphicsPath path = SettingsFluentResources.CreateRoundRectangle(new Rectangle(0, 0, this.Width, this.Height), DesignTokens.Radius.SettingsCard))
            {
                Region old = this.Region;
                this.Region = new Region(path);
                if (old != null)
                {
                    old.Dispose();
                }
            }
        }
    }
}

internal sealed class SettingsFluentRow : Panel
{
    private const int CompactLayoutWidthThreshold = 700;
    private const int CompactLayoutRemainingTextThreshold = 320;
    private readonly Control valueControl;
    private bool hover;

    public SettingsFluentRow(Control valueControl, Font titleFont, Font hintFont)
    {
        this.valueControl = valueControl;
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        this.BackColor = Color.Transparent;
        this.Padding = new Padding(
            DesignTokens.Spacing.SettingsCardPaddingX,
            DesignTokens.Spacing.SettingsCardPaddingY,
            DesignTokens.Spacing.SettingsCardPaddingX,
            DesignTokens.Spacing.SettingsCardPaddingY);

        this.TitleLabel = new Label();
        this.TitleLabel.Font = titleFont;
        this.TitleLabel.ForeColor = SettingsFluentResources.TextPrimary;
        this.TitleLabel.BackColor = Color.Transparent;
        this.TitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        this.TitleLabel.AutoSize = false;

        this.HintLabel = new Label();
        this.HintLabel.Font = hintFont;
        this.HintLabel.ForeColor = SettingsFluentResources.TextTertiary;
        this.HintLabel.BackColor = Color.Transparent;
        this.HintLabel.TextAlign = ContentAlignment.TopLeft;
        this.HintLabel.AutoSize = false;

        this.Controls.Add(this.TitleLabel);
        this.Controls.Add(this.HintLabel);
        this.Controls.Add(valueControl);
    }

    public bool ShowTopDivider { get; set; }
    public Label TitleLabel { get; private set; }
    public Label HintLabel { get; private set; }

    public int ComputeDesiredHeight(int width)
    {
        int controlWidth = Math.Min(this.valueControl.Width, Math.Max(44, width - this.Padding.Left - this.Padding.Right));
        bool compact = ShouldUseCompactLayout(width, controlWidth);
        if (compact)
        {
            int textWidth = Math.Max(120, width - this.Padding.Left - this.Padding.Right);
            int titleHeight = SettingsFluentResources.GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
            int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : SettingsFluentResources.GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
            int controlTop = this.Padding.Top + titleHeight + hintHeight + 8;
            return Math.Max(80, controlTop + this.valueControl.Height + this.Padding.Bottom);
        }

        int controlLeft = width - this.Padding.Right - controlWidth;
        int left = this.Padding.Left;
        int textWidthWide = Math.Max(120, controlLeft - left - 24);
        int titleHeightWide = SettingsFluentResources.GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidthWide, 6);
        int hintHeightWide = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : SettingsFluentResources.GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidthWide, 4);
        int textHeight = titleHeightWide + hintHeightWide;
        int contentHeight = Math.Max(textHeight, this.valueControl.Height);
        return Math.Max(60, this.Padding.Top + contentHeight + this.Padding.Bottom);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (this.hover)
        {
            using (SolidBrush brush = new SolidBrush(SettingsFluentResources.CardHover))
            {
                e.Graphics.FillRectangle(brush, this.ClientRectangle);
            }
        }

        if (this.ShowTopDivider)
        {
            int x1 = this.Padding.Left;
            int x2 = this.Width - this.Padding.Right;
            using (Pen pen = new Pen(SettingsFluentResources.DividerColor))
            {
                e.Graphics.DrawLine(pen, x1, 0, x2, 0);
            }
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutChildren();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control.MouseEnter += delegate { OnChildMouseEnter(); };
        e.Control.MouseLeave += delegate { OnChildMouseLeave(); };
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        this.hover = true;
        this.Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!this.ClientRectangle.Contains(this.PointToClient(Cursor.Position)))
        {
            this.hover = false;
            this.Invalidate();
        }

        base.OnMouseLeave(e);
    }

    private void LayoutChildren()
    {
        int left = this.Padding.Left;
        int top = this.Padding.Top;
        int right = this.Width - this.Padding.Right;
        int controlWidth = Math.Min(this.valueControl.Width, Math.Max(44, this.Width - this.Padding.Left - this.Padding.Right));
        bool compact = ShouldUseCompactLayout(this.Width, controlWidth);

        if (compact)
        {
            int textWidth = Math.Max(120, right - left);
            int titleHeight = SettingsFluentResources.GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
            int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : SettingsFluentResources.GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
            int controlTop = top + titleHeight + hintHeight + 8;
            this.TitleLabel.SetBounds(left, top, textWidth, titleHeight);
            this.HintLabel.SetBounds(left, top + titleHeight, textWidth, hintHeight);
            this.valueControl.SetBounds(left, controlTop, controlWidth, this.valueControl.Height);
            return;
        }

        int controlLeft = right - controlWidth;
        int wideTextWidth = Math.Max(120, controlLeft - left - 24);
        int titleHeightWide = SettingsFluentResources.GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, wideTextWidth, 6);
        int hintHeightWide = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : SettingsFluentResources.GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, wideTextWidth, 4);
        int textHeight = titleHeightWide + hintHeightWide;
        int contentHeight = Math.Max(textHeight, this.valueControl.Height);
        int wideControlTop = top + Math.Max(0, (contentHeight - this.valueControl.Height) / 2);
        this.valueControl.SetBounds(controlLeft, wideControlTop, controlWidth, this.valueControl.Height);
        this.TitleLabel.SetBounds(left, top, wideTextWidth, titleHeightWide);
        this.HintLabel.SetBounds(left, top + titleHeightWide, wideTextWidth, hintHeightWide);
    }

    private bool ShouldUseCompactLayout(int width, int controlWidth)
    {
        int availableWidth = width - this.Padding.Left - this.Padding.Right;
        return width < CompactLayoutWidthThreshold || availableWidth - controlWidth < CompactLayoutRemainingTextThreshold;
    }

    private void OnChildMouseEnter()
    {
        if (!this.hover)
        {
            this.hover = true;
            this.Invalidate();
        }
    }

    private void OnChildMouseLeave()
    {
        if (!this.ClientRectangle.Contains(this.PointToClient(Cursor.Position)))
        {
            this.hover = false;
            this.Invalidate();
        }
    }
}
