using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CapCutBatchClient
{
    public sealed class CategoryBatchForm : Form
    {
        private readonly AppConfig config;
        private readonly ComboBox categoryBox;
        private readonly TextBox videoBox;
        private readonly TextBox audioBox;
        private readonly TextBox outputBox;
        private readonly TextBox hancaiSampleBox;
        private readonly TextBox entityNormalSampleBox;
        private readonly TextBox entityPuSampleBox;
        private readonly ComboBox subtitleStyleBox;
        private readonly DataGridView grid;
        private readonly TextBox issuesBox;
        private readonly Button importButton;
        private CategoryScanResult scanResult;
        private string loadedCategory;

        public List<VideoTask> ImportedTasks { get; private set; }

        public CategoryBatchForm(AppConfig config)
        {
            this.config = config;
            ImportedTasks = new List<VideoTask>();
            Text = "类目整批扫描";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1160, 760);
            MinimumSize = new Size(980, 680);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(248, 250, 252);

            var fields = new TableLayoutPanel {
                Dock = DockStyle.Top, Height = 230, Padding = new Padding(16, 12, 16, 8),
                ColumnCount = 4, RowCount = 7, BackColor = Color.White
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (int index = 0; index < 7; index++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 14.28F));

            categoryBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            categoryBox.Items.AddRange(CategoryCatalog.Categories.Cast<object>().ToArray());
            videoBox = MakePathBox();
            audioBox = MakePathBox();
            outputBox = MakePathBox();
            hancaiSampleBox = MakePathBox();
            entityNormalSampleBox = MakePathBox();
            entityPuSampleBox = MakePathBox();
            subtitleStyleBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            subtitleStyleBox.Items.AddRange(new object[] {
                "CapCut 默认",
                "公司标准字幕（ZY Modern / 10 / 0725公司预设，草稿直改）"
            });
            if (subtitleStyleBox.Items.Count > 1)
                subtitleStyleBox.Items[1] =
                    "Company subtitle (ZY Modern / 10 / 0725 preset, direct draft)";
            subtitleStyleBox.SelectedIndex = String.Equals(
                config.DefaultSubtitleStylePreset, "CompanyStandard", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            AddPathRow(fields, 0, "处理类目", categoryBox, null, "一次只处理一个类目");
            AddPathRow(fields, 1, "生成视频目录", videoBox, MakeBrowseButton(videoBox, "选择本类目的生成视频目录"), "");
            AddPathRow(fields, 2, "对应配音目录", audioBox, MakeBrowseButton(audioBox, "选择本类目的配音根目录"), "");
            AddPathRow(fields, 3, "成片输出根目录", outputBox, MakeBrowseButton(outputBox, "选择 CapCut 成片输出根目录"), "");
            AddPathRow(fields, 4, "ExampleBrandC 样品库", hancaiSampleBox, MakeBrowseButton(hancaiSampleBox, "选择 ExampleBrandC 样品库"), "ProjectA/ProjectC/ProjectD/SPC");
            AddPathRow(fields, 5, "ProjectB普通库", entityNormalSampleBox, MakeBrowseButton(entityNormalSampleBox, "选择ProjectB普通系列样品库"), "非 PU 材料");
            AddPathRow(fields, 6, "ProjectB PU 库", entityPuSampleBox, MakeBrowseButton(entityPuSampleBox, "选择ProjectB PU 系列样品库"), "PU石/PU软石");

            var actionPanel = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.White };
            var scanButton = new Button { Text = "扫描类目", Width = 112, Height = 34, Left = 16, Top = 8 };
            actionPanel.Controls.Add(scanButton);
            var styleLabel = new Label { Text = "字幕样式", AutoSize = true, Left = 150, Top = 17 };
            subtitleStyleBox.SetBounds(218, 10, 250, 30);
            actionPanel.Controls.Add(styleLabel);
            actionPanel.Controls.Add(subtitleStyleBox);

            grid = new DataGridView {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false, BackgroundColor = Color.White
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "类目", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Material", HeaderText = "材料", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Language", HeaderText = "语言", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Task", HeaderText = "配音任务", Width = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Audio", HeaderText = "配音", Width = 310 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "视频数", Width = 70 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Outputs", HeaderText = "输出编号", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            issuesBox = new TextBox {
                Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(255, 251, 235),
                ForeColor = Color.FromArgb(146, 64, 14)
            };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Color.White };
            var cancelButton = new Button { Text = "取消", Width = 96, Height = 34, Top = 10, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            importButton = new Button {
                Text = "加入处理队列", Width = 132, Height = 34, Top = 10,
                Enabled = false, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White
            };
            cancelButton.Left = ClientSize.Width - 250;
            importButton.Left = ClientSize.Width - 146;
            cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            importButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bottom.Controls.Add(cancelButton);
            bottom.Controls.Add(importButton);

            Controls.Add(grid);
            Controls.Add(issuesBox);
            Controls.Add(bottom);
            Controls.Add(actionPanel);
            Controls.Add(fields);

            categoryBox.SelectedIndexChanged += delegate { OnCategoryChanged(); };
            scanButton.Click += delegate { ScanCategory(); };
            importButton.Click += delegate {
                SavePaths();
                ImportedTasks = scanResult == null ? new List<VideoTask>() : scanResult.Tasks;
                DialogResult = DialogResult.OK;
                Close();
            };
            cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            hancaiSampleBox.Text = config.ExampleBrandCSampleDirectory ?? "";
            entityNormalSampleBox.Text = config.EntityNormalSampleDirectory ?? "";
            entityPuSampleBox.Text = config.EntityPuSampleDirectory ?? "";
            outputBox.Text = config.OutputDirectory ?? "";
            int selected = Array.IndexOf(CategoryCatalog.Categories, config.LastCategoryName);
            categoryBox.SelectedIndex = selected < 0 ? 0 : selected;
        }

        private static TextBox MakePathBox()
        {
            return new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }

        private static void AddPathRow(
            TableLayoutPanel panel, int row, string label, Control field, Button browse, string hint)
        {
            panel.Controls.Add(MakeLabel(label), 0, row);
            panel.Controls.Add(field, 1, row);
            if (browse != null) panel.Controls.Add(browse, 2, row);
            panel.Controls.Add(MakeLabel(hint), 3, row);
        }

        private Button MakeBrowseButton(TextBox target, string description)
        {
            var button = new Button { Text = "选择目录", Dock = DockStyle.Fill };
            button.Click += delegate {
                using (var dialog = new FolderBrowserDialog {
                    Description = description,
                    SelectedPath = target.Text
                }) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
                }
            };
            return button;
        }

        private void OnCategoryChanged()
        {
            if (!String.IsNullOrWhiteSpace(loadedCategory)) {
                config.CategoryVideoDirectories[loadedCategory] = videoBox.Text.Trim();
                config.CategoryAudioDirectories[loadedCategory] = audioBox.Text.Trim();
            }
            loadedCategory = Convert.ToString(categoryBox.SelectedItem);
            string video;
            string audio;
            videoBox.Text = config.CategoryVideoDirectories.TryGetValue(loadedCategory, out video) ? video : "";
            audioBox.Text = config.CategoryAudioDirectories.TryGetValue(loadedCategory, out audio) ? audio : "";
            bool entity = String.Equals(loadedCategory, "ProjectB", StringComparison.OrdinalIgnoreCase);
            hancaiSampleBox.Enabled = !entity;
            entityNormalSampleBox.Enabled = entity;
            entityPuSampleBox.Enabled = entity;
            grid.Rows.Clear();
            issuesBox.Clear();
            importButton.Enabled = false;
        }

        private void ScanCategory()
        {
            try {
                SavePaths();
                string style = subtitleStyleBox.SelectedIndex <= 0 ? "Default" : "CompanyStandard";
                scanResult = new CategoryBatchScanner().Scan(
                    loadedCategory, videoBox.Text, audioBox.Text, outputBox.Text,
                    hancaiSampleBox.Text, entityNormalSampleBox.Text, entityPuSampleBox.Text, style);
                grid.Rows.Clear();
                foreach (CategoryMediaGroup group in scanResult.Groups) {
                    List<VideoTask> groupTasks = scanResult.Tasks
                        .Where(task => task.GroupId != null &&
                                       task.ProjectName == group.Category &&
                                       task.MaterialName == group.Material &&
                                       task.LanguageName == group.Language &&
                                       task.VoiceTaskNumber == group.TaskNumber)
                        .ToList();
                    if (groupTasks.Count == 0) continue;
                    grid.Rows.Add(
                        group.Category, group.Material, group.Language, group.TaskNumber,
                        Path.GetFileName(group.AudioPath), groupTasks.Count,
                        groupTasks.First().OutputPrefix + groupTasks.Min(task => task.Sequence) + " – " +
                        groupTasks.Last().OutputPrefix + groupTasks.Max(task => task.Sequence) + ".mp4");
                }
                issuesBox.Text = scanResult.Issues.Count == 0
                    ? "扫描完成：没有发现匹配错误。"
                    : String.Join(Environment.NewLine, scanResult.Issues.ToArray());
                importButton.Enabled = scanResult.Tasks.Count > 0 && scanResult.Issues.Count == 0;
            } catch (Exception ex) {
                scanResult = null;
                importButton.Enabled = false;
                issuesBox.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "无法扫描类目", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SavePaths()
        {
            if (!String.IsNullOrWhiteSpace(loadedCategory)) {
                config.CategoryVideoDirectories[loadedCategory] = videoBox.Text.Trim();
                config.CategoryAudioDirectories[loadedCategory] = audioBox.Text.Trim();
                config.LastCategoryName = loadedCategory;
            }
            config.OutputDirectory = outputBox.Text.Trim();
            config.ExampleBrandCSampleDirectory = hancaiSampleBox.Text.Trim();
            config.EntityNormalSampleDirectory = entityNormalSampleBox.Text.Trim();
            config.EntityPuSampleDirectory = entityPuSampleBox.Text.Trim();
            config.DefaultSubtitleStylePreset = subtitleStyleBox.SelectedIndex <= 0 ? "Default" : "CompanyStandard";
            config.Save(AppPaths.ConfigFile);
        }
    }
}
