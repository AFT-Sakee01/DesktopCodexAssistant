using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class ClaudeRadarModelMapEditorForm : Form
{
    private readonly DataGridView grid = new DataGridView();
    private readonly Dictionary<string, ClaudeRadarModelEntry> originalByKey =
        new Dictionary<string, ClaudeRadarModelEntry>(StringComparer.OrdinalIgnoreCase);

    public ClaudeRadarModelMapEditorForm()
    {
        this.Text = "Claude Radar 模型映射";
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ClientSize = new Size(820, 460);
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.ForeColor = Color.White;
        ApplicationIcon.ApplyTo(this);

        Label hint = new Label();
        hint.AutoSize = false;
        hint.Text = "编辑 display_name、rating_key、sort_order 和 enabled；source_key 来自网站，不允许在这里改名。";
        hint.ForeColor = DesignTokens.White(210);
        hint.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular);
        hint.Location = new Point(14, 12);
        hint.Size = new Size(this.ClientSize.Width - 28, 26);
        this.Controls.Add(hint);

        this.grid.Location = new Point(14, 44);
        this.grid.Size = new Size(this.ClientSize.Width - 28, this.ClientSize.Height - 98);
        this.grid.AllowUserToAddRows = false;
        this.grid.AllowUserToDeleteRows = false;
        this.grid.RowHeadersVisible = false;
        this.grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        this.grid.MultiSelect = false;
        this.grid.BackgroundColor = Color.FromArgb(24, 26, 31);
        this.grid.BorderStyle = BorderStyle.FixedSingle;
        this.grid.EnableHeadersVisualStyles = false;
        this.grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(34, 38, 45);
        this.grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        this.grid.DefaultCellStyle.BackColor = Color.FromArgb(28, 31, 37);
        this.grid.DefaultCellStyle.ForeColor = Color.White;
        this.grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(58, 91, 136);
        this.grid.DefaultCellStyle.SelectionForeColor = Color.White;
        BuildColumns();
        this.Controls.Add(this.grid);

        Button save = BuildButton("保存");
        save.Location = new Point(this.ClientSize.Width - 210, this.ClientSize.Height - 42);
        save.Click += delegate { SaveAndClose(); };
        this.Controls.Add(save);

        Button cancel = BuildButton("取消");
        cancel.Location = new Point(this.ClientSize.Width - 104, this.ClientSize.Height - 42);
        cancel.Click += delegate { this.DialogResult = DialogResult.Cancel; Close(); };
        this.Controls.Add(cancel);

        LoadRows();
    }

    private void BuildColumns()
    {
        this.grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "SourceKey",
            HeaderText = "source_key",
            ReadOnly = true,
            Width = 92
        });
        this.grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "DisplayName",
            HeaderText = "display_name",
            Width = 220
        });
        this.grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "RatingKey",
            HeaderText = "rating_key",
            Width = 180
        });
        this.grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "SortOrder",
            HeaderText = "sort_order",
            Width = 86
        });
        this.grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "enabled",
            Width = 72
        });
        this.grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "status",
            ReadOnly = true,
            Width = 120
        });
    }

    private static Button BuildButton(string text)
    {
        Button button = new Button();
        button.Text = text;
        button.Size = new Size(88, 30);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(38, 43, 51);
        button.ForeColor = Color.White;
        button.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold);
        return button;
    }

    private void LoadRows()
    {
        this.grid.Rows.Clear();
        this.originalByKey.Clear();
        List<ClaudeRadarModelEntry> entries = ClaudeRadarReader.LoadModelMap();
        for (int i = 0; i < entries.Count; i++)
        {
            ClaudeRadarModelEntry entry = entries[i];
            if (entry == null || string.IsNullOrWhiteSpace(entry.SourceKey))
            {
                continue;
            }

            this.originalByKey[entry.SourceKey] = entry.Clone();
            this.grid.Rows.Add(
                entry.SourceKey,
                entry.DisplayName,
                entry.RatingKey,
                entry.SortOrder.ToString(CultureInfo.InvariantCulture),
                entry.Enabled,
                entry.Status);
        }
    }

    private void SaveAndClose()
    {
        try
        {
            List<ClaudeRadarModelEntry> entries = new List<ClaudeRadarModelEntry>();
            for (int i = 0; i < this.grid.Rows.Count; i++)
            {
                DataGridViewRow row = this.grid.Rows[i];
                string sourceKey = Convert.ToString(row.Cells["SourceKey"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sourceKey))
                {
                    continue;
                }

                ClaudeRadarModelEntry original;
                if (!this.originalByKey.TryGetValue(sourceKey, out original))
                {
                    original = new ClaudeRadarModelEntry
                    {
                        SourceKey = sourceKey,
                        LastSeenUtc = DateTime.UtcNow,
                        Color = Color.Empty
                    };
                }

                int sortOrder;
                if (!int.TryParse(Convert.ToString(row.Cells["SortOrder"].Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out sortOrder))
                {
                    throw new InvalidOperationException(sourceKey + " 的 sort_order 不是整数。");
                }

                entries.Add(new ClaudeRadarModelEntry
                {
                    SourceKey = sourceKey,
                    DisplayName = Convert.ToString(row.Cells["DisplayName"].Value, CultureInfo.InvariantCulture) ?? string.Empty,
                    RatingKey = Convert.ToString(row.Cells["RatingKey"].Value, CultureInfo.InvariantCulture) ?? string.Empty,
                    SortOrder = sortOrder,
                    Enabled = row.Cells["Enabled"].Value is bool && (bool)row.Cells["Enabled"].Value,
                    HistoricalOnly = original.HistoricalOnly,
                    Status = original.Status,
                    LastSeenUtc = original.LastSeenUtc,
                    MissingSuccessCount = original.MissingSuccessCount,
                    Color = original.Color
                });
            }

            ClaudeRadarReader.SaveModelMap(entries);
            this.DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
