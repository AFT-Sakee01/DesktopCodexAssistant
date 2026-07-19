using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// State is kept on the settings-row button so preview/save can round-trip the target list without
// teaching the generic button editor about the row schema. The canonical parser remains the only
// authority for validation and for the disabled-target traffic boundary.
internal sealed class NetworkProbeTargetEditorState
{
    internal NetworkProbeTargetEditorState(bool cloud, string[] values)
    {
        this.Cloud = cloud;
        this.SetValues(values);
    }

    internal bool Cloud { get; private set; }
    internal string[] Values { get; private set; }

    internal void SetValues(string[] values)
    {
        this.Values = this.Cloud
            ? NetworkProbeTargetSettings.NormalizeCloudTargets(values)
            : NetworkProbeTargetSettings.NormalizeFixedPingTargets(values);
    }

    internal string GetButtonText()
    {
        List<NetworkProbeTargetDefinition> rows = this.Cloud
            ? NetworkProbeTargetSettings.ParseCloudTargets(this.Values)
            : NetworkProbeTargetSettings.ParseFixedPingTargets(this.Values);
        int enabled = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Enabled)
            {
                enabled++;
            }
        }

        return "配置 " + rows.Count + " 项 · 启用 " + enabled;
    }
}

internal sealed class NetworkProbeTargetEditorForm : Form
{
    private static readonly Color WindowBack = DesignTokens.SettingsWarmTheme.WindowBase;
    private static readonly Color CardBack = DesignTokens.SettingsWarmTheme.CardRest;
    private static readonly Color InputBack = DesignTokens.SettingsWarmTheme.InputBackground;
    private static readonly Color Border = DesignTokens.SettingsWarmTheme.DividerLines;
    private static readonly Color TextPrimary = DesignTokens.SettingsWarmTheme.TextPrimary;
    private static readonly Color TextSecondary = DesignTokens.SettingsWarmTheme.TextSecondary;
    private static readonly Color TextMuted = DesignTokens.SettingsWarmTheme.TextMuted;
    private static readonly Color Accent = DesignTokens.SettingsWarmTheme.Accent;

    private readonly bool cloud;
    private readonly CheckedListBox targetList;
    private readonly TextBox nameText;
    private readonly TextBox targetText;
    private readonly Button removeButton;
    private readonly List<NetworkProbeTargetDefinition> rows;

    internal NetworkProbeTargetEditorForm(bool cloud, string[] values)
    {
        this.cloud = cloud;
        this.rows = cloud
            ? NetworkProbeTargetSettings.ParseCloudTargets(values)
            : NetworkProbeTargetSettings.ParseFixedPingTargets(values);

        this.Text = cloud ? "云服务检测目标" : "固定站点 Ping";
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MinimizeBox = false;
        this.MaximizeBox = false;
        this.ShowInTaskbar = false;
        this.ClientSize = new Size(760, 610);
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.BackColor = WindowBack;
        this.ForeColor = TextPrimary;
        this.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

        Label title = CreateLabel(
            cloud ? "选择需要检测的云服务" : "选择需要持续 Ping 的站点",
            new Font(this.Font.FontFamily, 15.0f, FontStyle.Bold),
            TextPrimary);

        int margin = 30;
        int contentWidth = 700;
        int y = 24;
        int titleHeight = MeasureSingleLineHeight(title.Font, 8);
        title.SetBounds(margin, y, contentWidth, titleHeight);

        Label explanation = CreateLabel(
            cloud
                ? "取消勾选后不会再向该服务发送检测请求；可在下方用显示名称和 IP/主机新增 ICMP 检测。"
                : "默认包含 Google、百度和 Yahoo；可取消勾选、删除或新增 IP/主机。",
            this.Font,
            TextMuted);
        y = title.Bottom + 3;
        int explanationHeight = MeasureWrappedHeight(explanation.Text, explanation.Font, contentWidth - 4, 4);
        explanation.SetBounds(margin + 2, y, contentWidth - 4, explanationHeight);

        this.targetList = new CheckedListBox();
        y = explanation.Bottom + 10;
        this.targetList.SetBounds(margin, y, contentWidth, 272);
        this.targetList.BackColor = CardBack;
        this.targetList.ForeColor = TextSecondary;
        this.targetList.BorderStyle = BorderStyle.FixedSingle;
        this.targetList.CheckOnClick = true;
        this.targetList.IntegralHeight = false;
        this.targetList.Font = new Font(this.Font.FontFamily, 10.0f, FontStyle.Regular, GraphicsUnit.Point);
        this.targetList.SelectedIndexChanged += delegate { RefreshRemoveButton(); };

        Label addTitle = CreateLabel("新增检测目标", new Font(this.Font.FontFamily, 10.0f, FontStyle.Bold), TextPrimary);
        y = this.targetList.Bottom + 18;
        int addTitleHeight = MeasureSingleLineHeight(addTitle.Font, 4);
        addTitle.SetBounds(margin, y, 180, addTitleHeight);

        Label nameLabel = CreateLabel("显示名称", new Font(this.Font.FontFamily, 8.0f), TextMuted);
        y = addTitle.Bottom + 4;
        int fieldLabelHeight = MeasureSingleLineHeight(nameLabel.Font, 2);
        nameLabel.SetBounds(margin, y, 220, fieldLabelHeight);

        Label targetLabel = CreateLabel("IP 或主机名", new Font(this.Font.FontFamily, 8.0f), TextMuted);
        targetLabel.SetBounds(margin + 232, y, 300, fieldLabelHeight);

        this.nameText = CreateTextBox();
        int inputTop = Math.Max(nameLabel.Bottom, targetLabel.Bottom) + 3;
        int inputHeight = Math.Max(36, MeasureSingleLineHeight(this.nameText.Font, 10));
        this.nameText.SetBounds(margin, inputTop, 220, inputHeight);

        this.targetText = CreateTextBox();
        this.targetText.SetBounds(margin + 232, inputTop, 300, inputHeight);

        Button addButton = CreateButton("新增", true);
        int commandHeight = Math.Max(38, MeasureSingleLineHeight(addButton.Font, 12));
        addButton.SetBounds(margin + 544, inputTop, 156, commandHeight);
        addButton.Click += delegate { AddTarget(); };

        this.removeButton = CreateButton("删除所选自定义项", false);
        y = Math.Max(Math.Max(this.nameText.Bottom, this.targetText.Bottom), addButton.Bottom) + 12;
        this.removeButton.SetBounds(margin, y, 220, commandHeight);
        this.removeButton.Enabled = false;
        this.removeButton.Click += delegate { RemoveSelectedTarget(); };

        Button cancel = CreateButton("取消", false);
        int footerTop = this.removeButton.Bottom + 17;
        int footerHeight = Math.Max(40, MeasureSingleLineHeight(cancel.Font, 14));
        cancel.SetBounds(margin + 444, footerTop, 120, footerHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Button save = CreateButton("确定", true);
        save.SetBounds(margin + 580, footerTop, 120, footerHeight);
        save.Click += delegate
        {
            CaptureChecks();
            this.DialogResult = DialogResult.OK;
            this.Close();
        };

        this.AcceptButton = save;
        this.CancelButton = cancel;
        this.Controls.Add(title);
        this.Controls.Add(explanation);
        this.Controls.Add(this.targetList);
        this.Controls.Add(addTitle);
        this.Controls.Add(nameLabel);
        this.Controls.Add(targetLabel);
        this.Controls.Add(this.nameText);
        this.Controls.Add(this.targetText);
        this.Controls.Add(addButton);
        this.Controls.Add(this.removeButton);
        this.Controls.Add(cancel);
        this.Controls.Add(save);
        this.ClientSize = new Size(760, save.Bottom + 18);
        PopulateList();
    }

    internal string[] GetValues()
    {
        CaptureChecks();
        string[] values = new string[this.rows.Count];
        for (int i = 0; i < this.rows.Count; i++)
        {
            NetworkProbeTargetDefinition row = this.rows[i];
            values[i] = row.BuiltIn
                ? "builtin|" + row.Key + "|" + (row.Enabled ? "1" : "0")
                : (this.cloud ? "custom|" : "target|") +
                    NetworkProbeTargetSettings.NormalizeDisplayName(row.DisplayName) + "|" +
                    NetworkProbeTargetSettings.NormalizeTarget(row.Target) + "|" +
                    (row.Enabled ? "1" : "0");
        }

        return this.cloud
            ? NetworkProbeTargetSettings.NormalizeCloudTargets(values)
            : NetworkProbeTargetSettings.NormalizeFixedPingTargets(values);
    }

    private void PopulateList()
    {
        this.targetList.Items.Clear();
        for (int i = 0; i < this.rows.Count; i++)
        {
            NetworkProbeTargetDefinition row = this.rows[i];
            string detail = row.BuiltIn ? "官方状态" : row.Target;
            this.targetList.Items.Add(row.DisplayName + "    " + detail, row.Enabled);
        }

        RefreshRemoveButton();
    }

    private void CaptureChecks()
    {
        for (int i = 0; i < this.rows.Count && i < this.targetList.Items.Count; i++)
        {
            this.rows[i].Enabled = this.targetList.GetItemChecked(i);
        }
    }

    private void AddTarget()
    {
        CaptureChecks();
        string name = NetworkProbeTargetSettings.NormalizeDisplayName(this.nameText.Text);
        string target = NetworkProbeTargetSettings.NormalizeTarget(this.targetText.Text);
        if (name.Length == 0 || target.Length == 0)
        {
            MessageBox.Show(this, "请输入显示名称和有效的 IP 或主机名。", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int customCount = 0;
        for (int i = 0; i < this.rows.Count; i++)
        {
            if (!this.rows[i].BuiltIn)
            {
                customCount++;
            }

            if (!string.IsNullOrEmpty(this.rows[i].Target) &&
                string.Equals(this.rows[i].Target, target, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "这个目标已经存在。", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        int limit = this.cloud ? NetworkProbeTargetSettings.MaxCloudCustomTargets : NetworkProbeTargetSettings.MaxFixedPingTargets;
        if (customCount >= limit)
        {
            MessageBox.Show(this, "最多可配置 " + limit + " 个自定义目标。", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        this.rows.Add(new NetworkProbeTargetDefinition
        {
            Key = (this.cloud ? "custom:" : "ping:") + target.ToLowerInvariant(),
            DisplayName = name,
            Target = target,
            Enabled = true,
            BuiltIn = false
        });
        this.nameText.Clear();
        this.targetText.Clear();
        PopulateList();
        this.targetList.SelectedIndex = this.targetList.Items.Count - 1;
    }

    private void RemoveSelectedTarget()
    {
        int index = this.targetList.SelectedIndex;
        if (index < 0 || index >= this.rows.Count || this.rows[index].BuiltIn)
        {
            return;
        }

        CaptureChecks();
        this.rows.RemoveAt(index);
        PopulateList();
    }

    private void RefreshRemoveButton()
    {
        int index = this.targetList.SelectedIndex;
        this.removeButton.Enabled = index >= 0 && index < this.rows.Count && !this.rows[index].BuiltIn;
    }

    private TextBox CreateTextBox()
    {
        return new TextBox
        {
            BackColor = InputBack,
            ForeColor = TextSecondary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = this.Font
        };
    }

    private Button CreateButton(string text, bool primary)
    {
        Button button = new Button();
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Accent : CardBack;
        button.ForeColor = primary ? Color.White : TextSecondary;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.BorderSize = 1;
        button.Font = new Font(this.Font.FontFamily, 9.0f, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        return button;
    }

    private static Label CreateLabel(string text, Font font, Color color)
    {
        return new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false
        };
    }

    private static int MeasureSingleLineHeight(Font font, int padding)
    {
        return TextRenderer.MeasureText(
            "Ag国",
            font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height + padding;
    }

    private static int MeasureWrappedHeight(string text, Font font, int width, int padding)
    {
        return TextRenderer.MeasureText(
            text ?? string.Empty,
            font,
            new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height + padding;
    }
}
