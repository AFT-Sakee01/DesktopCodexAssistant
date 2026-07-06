using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class AiQuickMenuForm : Form
{
    private readonly WidgetForm ownerForm;
    private readonly WidgetSettings workingSettings;
    private readonly UiFontCache fontCache = new UiFontCache();
    private FlowLayoutPanel contentStack;
    private Panel scrollPanel;
    private SettingsFluentToggleSwitch aiBlockToggle;
    private SettingsFluentToggleSwitch planEnabledToggle;
    private ComboBox weeklyComparisonComboBox;
    private NumericUpDown weeklyThresholdBox;
    private ComboBox fiveHourComparisonComboBox;
    private NumericUpDown fiveHourThresholdBox;
    private ComboBox resumeConditionComboBox;
    private SettingsFluentToggleSwitch autoResumeToggle;
    private CheckedListBox pauseGoalList;
    private CheckedListBox resumeGoalList;
    private Label summaryLabel;
    private Label statusLabel;
    private Button refreshButton;
    private bool applyingControls;
    private List<CodexGoalInfo> displayedGoals = new List<CodexGoalInfo>();

    public AiQuickMenuForm(WidgetForm owner, WidgetSettings settings)
    {
        this.ownerForm = owner;
        this.workingSettings = settings == null ? WidgetSettings.CreateDefaults() : settings.Clone();
        this.workingSettings.Normalize();

        this.Text = "AI 快速选单";
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ClientSize = new Size(780, 760);
        this.MinimumSize = new Size(680, 620);
        this.BackColor = SettingsFluentResources.WindowBase;
        this.ForeColor = SettingsFluentResources.TextPrimary;
        this.Font = GetUiFont(10.0f);

        BuildUi();
        LoadFromSettings();
        PopulateGoalLists(CodexQuotaGoalPlanner.LoadKnownGoals());
        UpdateSummary();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutCards();
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

    private void BuildUi()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.ColumnCount = 1;
        root.RowCount = 3;
        root.BackColor = this.BackColor;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        this.Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);

        this.scrollPanel = new Panel();
        this.scrollPanel.Dock = DockStyle.Fill;
        this.scrollPanel.AutoScroll = true;
        this.scrollPanel.Padding = new Padding(24, 0, 18, 0);
        this.scrollPanel.BackColor = this.BackColor;
        this.scrollPanel.Resize += delegate { LayoutCards(); };
        root.Controls.Add(this.scrollPanel, 0, 1);

        this.contentStack = new FlowLayoutPanel();
        this.contentStack.Dock = DockStyle.Top;
        this.contentStack.FlowDirection = FlowDirection.TopDown;
        this.contentStack.WrapContents = false;
        this.contentStack.AutoSize = true;
        this.contentStack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        this.contentStack.BackColor = this.BackColor;
        this.scrollPanel.Controls.Add(this.contentStack);

        this.contentStack.Controls.Add(BuildAiBlockCard());
        this.contentStack.Controls.Add(BuildTriggerCard());
        this.contentStack.Controls.Add(BuildPauseGoalCard());
        this.contentStack.Controls.Add(BuildResumeCard());

        root.Controls.Add(BuildFooter(), 0, 2);
    }

    private Control BuildHeader()
    {
        Panel header = new Panel();
        header.Dock = DockStyle.Fill;
        header.Padding = new Padding(28, 18, 28, 8);
        header.BackColor = this.BackColor;

        Label title = new Label();
        title.Text = "AI 快速选单";
        title.Font = GetUiFont(18.0f, FontStyle.Bold);
        title.ForeColor = SettingsFluentResources.TextPrimary;
        title.SetBounds(28, 18, 680, 32);
        title.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

        Label subtitle = new Label();
        subtitle.Text = "快速切换 AI 阻断，并按 Codex 额度条件暂停或恢复选中的 goal。";
        subtitle.Font = GetUiFont(9.5f);
        subtitle.ForeColor = SettingsFluentResources.TextTertiary;
        subtitle.SetBounds(28, 54, 700, 24);
        subtitle.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        return header;
    }

    private Control BuildFooter()
    {
        TableLayoutPanel footer = new TableLayoutPanel();
        footer.Dock = DockStyle.Fill;
        footer.ColumnCount = 2;
        footer.RowCount = 1;
        footer.Padding = new Padding(24, 8, 16, 12);
        footer.BackColor = this.BackColor;
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        this.statusLabel = new Label();
        this.statusLabel.Dock = DockStyle.Fill;
        this.statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        this.statusLabel.ForeColor = SettingsFluentResources.TextTertiary;
        this.statusLabel.Font = GetUiFont(9.0f);
        footer.Controls.Add(this.statusLabel, 0, 0);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;
        buttons.BackColor = this.BackColor;

        Button closeButton = SettingsFluentResources.CreateCommandButton("关闭", false, GetUiFont(9.5f, FontStyle.Bold));
        closeButton.Click += delegate { this.Close(); };
        Button saveButton = SettingsFluentResources.CreateCommandButton("保存", true, GetUiFont(9.5f, FontStyle.Bold));
        saveButton.Click += delegate { SaveQuotaPlan(); };
        this.refreshButton = SettingsFluentResources.CreateCommandButton("刷新 goal", false, GetUiFont(9.5f, FontStyle.Bold));
        this.refreshButton.Click += delegate { RefreshGoals(); };

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(this.refreshButton);
        footer.Controls.Add(buttons, 1, 0);
        return footer;
    }

    private SettingsFluentGroupCard BuildAiBlockCard()
    {
        SettingsFluentGroupCard card = new SettingsFluentGroupCard();
        this.aiBlockToggle = new SettingsFluentToggleSwitch();
        this.aiBlockToggle.CheckedChanged += OnAiBlockToggleChanged;
        card.AddRow(CreateRow(
            "AI 阻断",
            "开启后，本程序会阻断发往 OpenAI、ChatGPT、Claude 和 Anthropic 的相关请求，并按手动阻断策略尝试停止正在运行的 AI 工具。",
            this.aiBlockToggle));
        return card;
    }

    private SettingsFluentGroupCard BuildTriggerCard()
    {
        SettingsFluentGroupCard card = new SettingsFluentGroupCard();

        this.planEnabledToggle = new SettingsFluentToggleSwitch();
        this.planEnabledToggle.CheckedChanged += delegate { UpdateSummary(); };
        card.AddRow(CreateRow(
            "Codex 额度计划",
            "启用后，主窗口维护 tick 会读取本地 quota.ini 剩余额度快照，并在后台通过 codex app-server 调整 goal 状态。",
            this.planEnabledToggle));

        card.AddRow(CreateRow(
            "当周额度",
            "周额度剩余百分比满足此条件时，参与截断判定。",
            BuildConditionControl(out this.weeklyComparisonComboBox, out this.weeklyThresholdBox)));

        card.AddRow(CreateRow(
            "5小时额度",
            "5 小时额度剩余百分比满足此条件时，参与截断判定。",
            BuildConditionControl(out this.fiveHourComparisonComboBox, out this.fiveHourThresholdBox)));

        this.summaryLabel = CreateValueLabel();
        card.AddRow(CreateRow(
            "计划摘要",
            "保存后生效。截断只改 goal 状态，不自动结束 Codex 或 Claude 进程。",
            this.summaryLabel));

        return card;
    }

    private SettingsFluentGroupCard BuildPauseGoalCard()
    {
        SettingsFluentGroupCard card = new SettingsFluentGroupCard();
        this.pauseGoalList = SettingsFluentResources.CreateCheckedListBox(GetUiFont(9.0f));
        this.pauseGoalList.ItemCheck += delegate { BeginInvoke((MethodInvoker)UpdateSummary); };
        card.AddRow(CreateRow(
            "截断 goal",
            "常驻 goal 列表。达到触发条件时，将勾选项设置为 usageLimited。",
            this.pauseGoalList));
        return card;
    }

    private SettingsFluentGroupCard BuildResumeCard()
    {
        SettingsFluentGroupCard card = new SettingsFluentGroupCard();

        this.autoResumeToggle = new SettingsFluentToggleSwitch();
        this.autoResumeToggle.CheckedChanged += delegate { UpdateResumeListEnabled(); UpdateSummary(); };
        card.AddRow(CreateRow(
            "恢复上次暂停",
            "开启时，额度恢复后自动启用本程序上次因额度计划暂停的 goal；关闭后使用下方恢复列表。",
            this.autoResumeToggle));

        this.resumeConditionComboBox = CreateResumeConditionComboBox();
        this.resumeConditionComboBox.SelectedIndexChanged += delegate { UpdateSummary(); };
        card.AddRow(CreateRow(
            "恢复额度类型",
            "选择额度恢复后自动启用 goal 时看周额度、5 小时额度，还是两者都恢复。",
            this.resumeConditionComboBox));

        this.resumeGoalList = SettingsFluentResources.CreateCheckedListBox(GetUiFont(9.0f));
        this.resumeGoalList.ItemCheck += delegate { BeginInvoke((MethodInvoker)UpdateSummary); };
        card.AddRow(CreateRow(
            "恢复 goal",
            "仅在关闭“恢复上次暂停”时使用。额度恢复后，将勾选项设置为 active。",
            this.resumeGoalList));

        return card;
    }

    private SettingsFluentRow CreateRow(string title, string hint, Control valueControl)
    {
        SettingsFluentRow row = new SettingsFluentRow(valueControl, GetUiFont(10.0f), GetUiFont(8.5f));
        row.TitleLabel.Text = title;
        row.HintLabel.Text = hint;
        row.BackColor = Color.Transparent;
        return row;
    }

    private Control BuildConditionControl(out ComboBox comparisonBox, out NumericUpDown thresholdBox)
    {
        Panel panel = new Panel();
        panel.Width = 282;
        panel.Height = 54;
        panel.BackColor = Color.Transparent;

        comparisonBox = CreateComparisonComboBox();
        comparisonBox.SetBounds(0, 4, 112, 44);
        comparisonBox.SelectedIndexChanged += delegate { UpdateSummary(); };
        panel.Controls.Add(comparisonBox);

        thresholdBox = SettingsFluentResources.CreatePercentBox(GetUiFont(9.5f), 88);
        thresholdBox.SetBounds(124, 4, 88, 44);
        thresholdBox.ValueChanged += delegate { UpdateSummary(); };
        panel.Controls.Add(thresholdBox);

        Label percent = new Label();
        percent.Text = "%";
        percent.Font = GetUiFont(9.5f, FontStyle.Bold);
        percent.ForeColor = SettingsFluentResources.TextSecondary;
        percent.BackColor = Color.Transparent;
        percent.TextAlign = ContentAlignment.MiddleLeft;
        percent.SetBounds(222, 4, 36, 44);
        panel.Controls.Add(percent);
        return panel;
    }

    private Label CreateValueLabel()
    {
        Label label = new Label();
        label.Width = 520;
        label.Height = 54;
        label.AutoEllipsis = true;
        label.Font = GetUiFont(9.0f);
        label.ForeColor = SettingsFluentResources.TextSecondary;
        label.BackColor = Color.Transparent;
        label.TextAlign = ContentAlignment.MiddleLeft;
        return label;
    }

    private ComboBox CreateComparisonComboBox()
    {
        ComboBox combo = SettingsFluentResources.CreateComboBox(GetUiFont(9.5f), 112);
        combo.Items.Add(new ComparisonItem("小于", CodexQuotaPlanComparison.LessThan));
        combo.Items.Add(new ComparisonItem("大于", CodexQuotaPlanComparison.GreaterThan));
        combo.SelectedIndex = 0;
        return combo;
    }

    private ComboBox CreateResumeConditionComboBox()
    {
        ComboBox combo = SettingsFluentResources.CreateComboBox(GetUiFont(9.5f), 260);
        combo.Items.Add(new ResumeConditionItem("周额度与 5小时额度", CodexQuotaPlanResumeConditionMode.Both));
        combo.Items.Add(new ResumeConditionItem("仅周额度", CodexQuotaPlanResumeConditionMode.WeeklyOnly));
        combo.Items.Add(new ResumeConditionItem("仅 5小时额度", CodexQuotaPlanResumeConditionMode.FiveHourOnly));
        combo.SelectedIndex = 0;
        return combo;
    }

    private void LayoutCards()
    {
        if (this.scrollPanel == null || this.contentStack == null)
        {
            return;
        }

        int width = Math.Max(420, this.scrollPanel.ClientSize.Width - this.scrollPanel.Padding.Left - this.scrollPanel.Padding.Right - 18);
        this.contentStack.Width = width;
        for (int i = 0; i < this.contentStack.Controls.Count; i++)
        {
            SettingsFluentGroupCard card = this.contentStack.Controls[i] as SettingsFluentGroupCard;
            if (card != null)
            {
                card.Width = width;
                card.LayoutRows();
            }
        }
    }

    private void LoadFromSettings()
    {
        this.applyingControls = true;
        try
        {
            this.aiBlockToggle.SetCheckedSilent(this.workingSettings.AiRequestProtectionManualBlockEnabled);
            this.planEnabledToggle.SetCheckedSilent(this.workingSettings.CodexQuotaPlanEnabled);
            SelectComparison(this.weeklyComparisonComboBox, this.workingSettings.CodexQuotaPlanWeeklyComparison);
            this.weeklyThresholdBox.Value = this.workingSettings.CodexQuotaPlanWeeklyThresholdPercent;
            SelectComparison(this.fiveHourComparisonComboBox, this.workingSettings.CodexQuotaPlanFiveHourComparison);
            this.fiveHourThresholdBox.Value = this.workingSettings.CodexQuotaPlanFiveHourThresholdPercent;
            SelectResumeCondition(this.resumeConditionComboBox, this.workingSettings.CodexQuotaPlanResumeConditionMode);
            this.autoResumeToggle.SetCheckedSilent(this.workingSettings.CodexQuotaPlanAutoResumePausedGoals);
        }
        finally
        {
            this.applyingControls = false;
        }

        UpdateResumeListEnabled();
    }

    private void PopulateGoalLists(List<CodexGoalInfo> goals)
    {
        this.displayedGoals = MergeGoals(goals, this.workingSettings.CodexQuotaPlanPauseGoalIds, this.workingSettings.CodexQuotaPlanResumeGoalIds);
        PopulateGoalList(this.pauseGoalList, this.displayedGoals, this.workingSettings.CodexQuotaPlanPauseGoalIds);
        PopulateGoalList(this.resumeGoalList, this.displayedGoals, this.workingSettings.CodexQuotaPlanResumeGoalIds);
    }

    private void PopulateGoalList(CheckedListBox list, List<CodexGoalInfo> goals, string selectedIds)
    {
        HashSet<string> selected = BuildGoalIdSet(selectedIds);
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            for (int i = 0; i < goals.Count; i++)
            {
                GoalListItem item = new GoalListItem(goals[i]);
                list.Items.Add(item, selected.Contains(item.ThreadId));
            }
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private void RefreshGoals()
    {
        if (this.refreshButton == null || !this.refreshButton.Enabled)
        {
            return;
        }

        this.refreshButton.Enabled = false;
        this.statusLabel.Text = "正在通过 codex app-server 刷新 goal 列表...";
        Task.Run(delegate
        {
            string error;
            List<CodexGoalInfo> goals = CodexAppServerGoalController.ListGoals(out error);
            if (goals.Count > 0)
            {
                CodexQuotaGoalPlanner.SaveKnownGoals(goals);
            }

            if (this.IsDisposed)
            {
                return;
            }

            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    this.refreshButton.Enabled = true;
                    if (goals.Count > 0)
                    {
                        PreserveSelectionsToWorkingSettings();
                        PopulateGoalLists(goals);
                        this.statusLabel.Text = "已刷新 " + goals.Count.ToString(CultureInfo.InvariantCulture) + " 个 Codex goal。";
                    }
                    else
                    {
                        this.statusLabel.Text = string.IsNullOrWhiteSpace(error)
                            ? "没有发现可管理的 Codex goal。"
                            : "刷新失败：" + error;
                    }

                    UpdateSummary();
                });
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void SaveQuotaPlan()
    {
        PreserveSelectionsToWorkingSettings();
        if (this.ownerForm != null && !this.ownerForm.IsDisposed)
        {
            this.ownerForm.SaveSettings(this.workingSettings);
        }

        CodexQuotaGoalPlanner.SaveKnownGoals(this.displayedGoals);
        this.statusLabel.Text = "额度计划已保存。";
        UpdateSummary();
    }

    private void PreserveSelectionsToWorkingSettings()
    {
        this.workingSettings.AiRequestProtectionManualBlockEnabled = this.aiBlockToggle.Checked;
        this.workingSettings.CodexQuotaPlanEnabled = this.planEnabledToggle.Checked;
        this.workingSettings.CodexQuotaPlanWeeklyComparison = GetSelectedComparison(this.weeklyComparisonComboBox);
        this.workingSettings.CodexQuotaPlanWeeklyThresholdPercent = (int)this.weeklyThresholdBox.Value;
        this.workingSettings.CodexQuotaPlanFiveHourComparison = GetSelectedComparison(this.fiveHourComparisonComboBox);
        this.workingSettings.CodexQuotaPlanFiveHourThresholdPercent = (int)this.fiveHourThresholdBox.Value;
        this.workingSettings.CodexQuotaPlanResumeConditionMode = GetSelectedResumeCondition(this.resumeConditionComboBox);
        this.workingSettings.CodexQuotaPlanAutoResumePausedGoals = this.autoResumeToggle.Checked;
        this.workingSettings.CodexQuotaPlanPauseGoalIds = GoalIdsFromList(this.pauseGoalList);
        this.workingSettings.CodexQuotaPlanResumeGoalIds = GoalIdsFromList(this.resumeGoalList);
        this.workingSettings.Normalize();
    }

    private void OnAiBlockToggleChanged(object sender, EventArgs e)
    {
        if (this.applyingControls || this.ownerForm == null || this.ownerForm.IsDisposed)
        {
            return;
        }

        bool enabled = this.aiBlockToggle.Checked;
        this.workingSettings.AiRequestProtectionManualBlockEnabled = enabled;
        this.ownerForm.SetAiRequestBlockingFromOperationPanel(enabled);
        this.statusLabel.Text = enabled ? "AI 阻断已开启。" : "AI 阻断已关闭。";
    }

    private void UpdateResumeListEnabled()
    {
        if (this.resumeGoalList != null)
        {
            this.resumeGoalList.Enabled = !this.autoResumeToggle.Checked;
            this.resumeGoalList.BackColor = this.resumeGoalList.Enabled
                ? SettingsFluentResources.ControlBg
                : SettingsFluentResources.CardRest;
            this.resumeGoalList.ForeColor = this.resumeGoalList.Enabled
                ? SettingsFluentResources.TextPrimary
                : SettingsFluentResources.TextTertiary;
        }
    }

    private void UpdateSummary()
    {
        if (this.summaryLabel == null ||
            this.weeklyThresholdBox == null ||
            this.fiveHourThresholdBox == null)
        {
            return;
        }

        string weekly = GetComparisonText(GetSelectedComparison(this.weeklyComparisonComboBox));
        string fiveHour = GetComparisonText(GetSelectedComparison(this.fiveHourComparisonComboBox));
        int pauseCount = this.pauseGoalList == null ? 0 : this.pauseGoalList.CheckedItems.Count;
        int resumeCount = this.resumeGoalList == null ? 0 : this.resumeGoalList.CheckedItems.Count;
        string resumeTarget = this.autoResumeToggle != null && this.autoResumeToggle.Checked
            ? "自动启用上次暂停的 goal"
            : "启用恢复列表 " + resumeCount.ToString(CultureInfo.InvariantCulture) + " 个 goal";
        this.summaryLabel.Text =
            "触发截断：当周额度 " + weekly + " " + ((int)this.weeklyThresholdBox.Value).ToString(CultureInfo.InvariantCulture) +
            "% 且 5小时额度 " + fiveHour + " " + ((int)this.fiveHourThresholdBox.Value).ToString(CultureInfo.InvariantCulture) +
            "% 时，暂停 " + pauseCount.ToString(CultureInfo.InvariantCulture) + " 个 goal。恢复启用：按 " +
            GetResumeConditionText(GetSelectedResumeCondition(this.resumeConditionComboBox)) + " 判定，" + resumeTarget + "。";
    }

    private static List<CodexGoalInfo> MergeGoals(List<CodexGoalInfo> goals, string pauseIds, string resumeIds)
    {
        List<CodexGoalInfo> merged = new List<CodexGoalInfo>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (goals != null)
        {
            for (int i = 0; i < goals.Count; i++)
            {
                AddGoal(merged, seen, goals[i]);
            }
        }

        AddPlaceholderGoals(merged, seen, pauseIds);
        AddPlaceholderGoals(merged, seen, resumeIds);
        return merged;
    }

    private static void AddPlaceholderGoals(List<CodexGoalInfo> goals, HashSet<string> seen, string ids)
    {
        string normalized = WidgetSettings.NormalizeGoalIdList(ids);
        if (normalized.Length == 0)
        {
            return;
        }

        string[] parts = normalized.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            AddGoal(goals, seen, new CodexGoalInfo
            {
                ThreadId = parts[i],
                Objective = "常驻 goal",
                Status = "saved"
            });
        }
    }

    private static void AddGoal(List<CodexGoalInfo> goals, HashSet<string> seen, CodexGoalInfo goal)
    {
        if (goal == null)
        {
            return;
        }

        string id = WidgetSettings.NormalizeGoalIdList(goal.ThreadId);
        if (id.Length == 0 || seen.Contains(id))
        {
            return;
        }

        seen.Add(id);
        goal.ThreadId = id;
        goals.Add(goal);
    }

    private static HashSet<string> BuildGoalIdSet(string ids)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string normalized = WidgetSettings.NormalizeGoalIdList(ids);
        if (normalized.Length == 0)
        {
            return set;
        }

        string[] parts = normalized.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            set.Add(parts[i]);
        }

        return set;
    }

    private static string GoalIdsFromList(CheckedListBox list)
    {
        List<string> ids = new List<string>();
        if (list != null)
        {
            for (int i = 0; i < list.CheckedItems.Count; i++)
            {
                GoalListItem item = list.CheckedItems[i] as GoalListItem;
                if (item != null)
                {
                    ids.Add(item.ThreadId);
                }
            }
        }

        return WidgetSettings.NormalizeGoalIdList(string.Join("|", ids.ToArray()));
    }

    private static void SelectComparison(ComboBox combo, CodexQuotaPlanComparison comparison)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            ComparisonItem item = combo.Items[i] as ComparisonItem;
            if (item != null && item.Value == comparison)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static void SelectResumeCondition(ComboBox combo, CodexQuotaPlanResumeConditionMode mode)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            ResumeConditionItem item = combo.Items[i] as ResumeConditionItem;
            if (item != null && item.Value == mode)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static CodexQuotaPlanComparison GetSelectedComparison(ComboBox combo)
    {
        ComparisonItem item = combo == null ? null : combo.SelectedItem as ComparisonItem;
        return item == null ? CodexQuotaPlanComparison.LessThan : item.Value;
    }

    private static CodexQuotaPlanResumeConditionMode GetSelectedResumeCondition(ComboBox combo)
    {
        ResumeConditionItem item = combo == null ? null : combo.SelectedItem as ResumeConditionItem;
        return item == null ? CodexQuotaPlanResumeConditionMode.Both : item.Value;
    }

    private static string GetComparisonText(CodexQuotaPlanComparison comparison)
    {
        return comparison == CodexQuotaPlanComparison.GreaterThan ? "大于" : "小于";
    }

    private static string GetResumeConditionText(CodexQuotaPlanResumeConditionMode mode)
    {
        if (mode == CodexQuotaPlanResumeConditionMode.WeeklyOnly)
        {
            return "周额度";
        }

        if (mode == CodexQuotaPlanResumeConditionMode.FiveHourOnly)
        {
            return "5小时额度";
        }

        return "周额度与 5小时额度";
    }

    private sealed class ComparisonItem
    {
        public ComparisonItem(string text, CodexQuotaPlanComparison value)
        {
            this.Text = text;
            this.Value = value;
        }

        public string Text { get; private set; }
        public CodexQuotaPlanComparison Value { get; private set; }

        public override string ToString()
        {
            return this.Text;
        }
    }

    private sealed class ResumeConditionItem
    {
        public ResumeConditionItem(string text, CodexQuotaPlanResumeConditionMode value)
        {
            this.Text = text;
            this.Value = value;
        }

        public string Text { get; private set; }
        public CodexQuotaPlanResumeConditionMode Value { get; private set; }

        public override string ToString()
        {
            return this.Text;
        }
    }

    private sealed class GoalListItem
    {
        private readonly CodexGoalInfo goal;

        public GoalListItem(CodexGoalInfo goal)
        {
            this.goal = goal;
        }

        public string ThreadId
        {
            get { return this.goal == null ? string.Empty : this.goal.ThreadId; }
        }

        public override string ToString()
        {
            return this.goal == null ? string.Empty : this.goal.DisplayText;
        }
    }
}
