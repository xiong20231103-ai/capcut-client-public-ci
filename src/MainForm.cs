using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CapCutBatchClient
{
    public sealed class MainForm : Form
    {
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(new[] {
            ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".flv", ".webm", ".ts", ".mts"
        }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(new[] {
            ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma"
        }, StringComparer.OrdinalIgnoreCase);

        private readonly AppConfig config;
        private readonly TaskStore store;
        private readonly AppLogger logger;
        private readonly List<VideoTask> tasks;
        private readonly DataGridView grid;
        private readonly TextBox outputBox;
        private readonly TextBox executableBox;
        private readonly TextBox projectBox;
        private readonly TextBox materialBox;
        private readonly TextBox languageBox;
        private readonly TextBox prefixBox;
        private readonly TextBox sharedAudioBox;
        private readonly TextBox sampleBox;
        private readonly NumericUpDown startSequenceBox;
        private readonly ComboBox subtitleStyleBox;
        private readonly Button styleCalibrationButton;
        private readonly TextBox logBox;
        private readonly Label summaryLabel;
        private readonly Button startButton;
        private readonly Button pauseButton;
        private readonly Button stopButton;
        private readonly Button retryButton;
        private readonly Button rerunCompletedButton;
        private readonly Button addButton;
        private readonly Button categoryButton;
        private readonly Button audioButton;
        private readonly Button sampleButton;
        private readonly Button removeButton;
        private readonly CheckBox recycleManualSourceBox;
        private readonly bool diagnosticFirstCaptionQNoOp;
        private readonly bool autoExitAfterTerminalHandoff;
        private readonly System.Windows.Forms.Timer elapsedTimer;
        private BatchRunner runner;
        private bool autoExitScheduled;
        private bool handoffWindowHiddenForAutomation;
        private bool closePending;

        public MainForm() : this(false, "", false)
        {
        }

        public MainForm(bool autoStart) : this(autoStart, "", false)
        {
        }

        public MainForm(bool autoStart, string handoffPath) : this(autoStart, handoffPath, false)
        {
        }

        public MainForm(bool autoStart, string handoffPath, bool diagnosticFirstCaptionQNoOp)
            : this(autoStart, false, handoffPath, diagnosticFirstCaptionQNoOp, "", "", "", "", false)
        {
        }

        public MainForm(bool autoStart, bool retryFailedOnStart, string handoffPath, bool diagnosticFirstCaptionQNoOp,
            string localFirstCaptionTestCase, string localFirstCaptionTestPack,
            string localSampleTestTaskFile, string localSampleOverridePath,
            bool localSampleWithoutSample)
        {
            this.diagnosticFirstCaptionQNoOp = diagnosticFirstCaptionQNoOp;
            // A workbench handoff owns this window. Leaving it open after a
            // terminal batch allowed the next project to create a second client
            // against the same task store. Manual and isolated tools stay open.
            autoExitAfterTerminalHandoff = autoStart && !String.IsNullOrWhiteSpace(handoffPath) &&
                !RuntimeModes.IsolatedLabRuntime;
            config = AppConfig.LoadOrCreate(AppPaths.ConfigFile);
            RuntimeModes.ApplySubtitleStyleLabDefaults(config);
            if (!String.IsNullOrWhiteSpace(localFirstCaptionTestCase) &&
                config.ApplyLocalVisibleBoundsFirstCaptionCalibration()) {
                config.Save(AppPaths.ConfigFile);
            }
            LocalSampleAppendTestRun localSampleRun = String.IsNullOrWhiteSpace(localSampleTestTaskFile)
                ? null
                : LocalSampleAppendTestFactory.Create(localSampleTestTaskFile, config,
                    localSampleOverridePath, localSampleWithoutSample);
            store = new TaskStore(localSampleRun == null ? AppPaths.TaskFile : localSampleRun.StorePath);
            logger = new AppLogger();
            logger.Info("客户端构建身份: version=" + ClientRuntimeIdentity.Version +
                "; buildHash=" + ClientRuntimeIdentity.BuildHash + "; exe=" + ClientRuntimeIdentity.ExecutablePath);
            tasks = localSampleRun == null ? store.Load() : localSampleRun.Tasks;
            if (localSampleRun == null) RestoreInterruptedTasks();
            if (localSampleRun == null && RuntimeModes.NormalizeWaitingIsolatedLabTasks(tasks)) {
                store.Save(tasks);
                logger.Info("隔离内测已将等待任务统一为 LabZYModern 草稿直改模式，并清除可恢复的停止提示。");
            }
            if (localSampleRun == null && !String.IsNullOrWhiteSpace(localFirstCaptionTestCase)) {
                string normalizedLocalCase = localFirstCaptionTestCase.Trim().ToUpperInvariant();
                tasks.RemoveAll(task =>
                    task.GetState() == TaskState.Waiting &&
                    String.Equals(task.ProjectName, "首帧测试", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(task.MaterialName, normalizedLocalCase, StringComparison.OrdinalIgnoreCase));
                tasks.Add(LocalFirstCaptionTestFactory.Create(
                    localFirstCaptionTestCase, localFirstCaptionTestPack, config));
                store.Save(tasks);
            }
            HandoffImportResult handoffResult = null;
            if (localSampleRun == null && !String.IsNullOrWhiteSpace(handoffPath)) {
                handoffResult = HandoffManifestImporter.Import(handoffPath, config, tasks);
                store.Save(tasks);
            }
            if (localSampleRun != null) {
                store.Save(tasks);
                logger.Info("本机样品追加隔离测试已创建；不会写入工作台、接收器、Dashboard或上传服务");
            }

            Text = "CapCut / 剪映自动字幕批处理" +
                (RuntimeModes.SubtitleStyleLab ? "【隔离字幕样式实验】" : "") +
                (diagnosticFirstCaptionQNoOp ? "【诊断：Q 不生效】" : "") +
                (String.IsNullOrWhiteSpace(localFirstCaptionTestCase)
                    ? ""
                    : "【本机首帧测试 " + localFirstCaptionTestCase.ToUpperInvariant() + "】");
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 940);
            MinimumSize = new Size(1080, 820);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 248, 251);
            AllowDrop = true;

            var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Color.White };
            header.Controls.Add(new Label {
                Text = "自动字幕批处理", AutoSize = true, Location = new Point(22, 13),
                Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), ForeColor = Color.FromArgb(15, 23, 42)
            });
            header.Controls.Add(new Label {
                Text = "拖入视频，依次导入剪映、识别字幕并导出", AutoSize = true, Location = new Point(24, 43),
                ForeColor = Color.FromArgb(100, 116, 139)
            });
            var calibrateButton = MakeButton("校准按钮", 102, 36);
            var firstCaptionCalibrationButton = MakeButton("校准首帧", 102, 36);
            var configButton = MakeButton("打开配置", 102, 36);
            configButton.Anchor = calibrateButton.Anchor = firstCaptionCalibrationButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            configButton.Location = new Point(990, 16);
            firstCaptionCalibrationButton.Location = new Point(878, 16);
            calibrateButton.Location = new Point(766, 16);
            header.Controls.Add(calibrateButton); header.Controls.Add(firstCaptionCalibrationButton); header.Controls.Add(configButton);
            header.Resize += delegate {
                configButton.Left = header.ClientSize.Width - configButton.Width - 14;
                firstCaptionCalibrationButton.Left = configButton.Left - firstCaptionCalibrationButton.Width - 10;
                calibrateButton.Left = firstCaptionCalibrationButton.Left - calibrateButton.Width - 10;
            };

            var settings = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(22, 12, 22, 8) };
            var settingsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            settingsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            settingsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            settingsGrid.Controls.Add(MakeLabel("输出目录"), 0, 0);
            outputBox = MakeTextBox(config.OutputDirectory);
            settingsGrid.Controls.Add(outputBox, 1, 0);
            var browseOutput = MakeButton("选择目录", 92, 30);
            settingsGrid.Controls.Add(browseOutput, 3, 0);
            settingsGrid.Controls.Add(MakeLabel("剪映程序"), 0, 1);
            executableBox = MakeTextBox(config.CapCutPath);
            executableBox.PlaceholderTextCompat("留空时自动查找剪映专业版或 CapCut");
            settingsGrid.Controls.Add(executableBox, 1, 1);
            var browseExe = MakeButton("选择程序", 92, 30);
            settingsGrid.Controls.Add(browseExe, 3, 1);
            settings.Controls.Add(settingsGrid);

            var groupSettings = new Panel { Dock = DockStyle.Top, Height = 202, Padding = new Padding(22, 6, 22, 8), BackColor = Color.White };
            var groupGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 4 };
            groupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            groupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            groupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            groupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            groupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            groupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            groupGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            groupGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            groupGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            groupGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

            projectBox = MakeTextBox(config.DefaultProjectName);
            materialBox = MakeTextBox(config.DefaultMaterialName);
            languageBox = MakeTextBox(config.DefaultLanguageName);
            prefixBox = MakeTextBox(config.DefaultOutputPrefix);
            sharedAudioBox = MakeTextBox(config.SharedAudioPath);
            sampleBox = MakeTextBox(config.SampleDirectory);
            sampleBox.ReadOnly = true;
            startSequenceBox = new NumericUpDown {
                Minimum = 1, Maximum = 9999, Value = Math.Max(1, config.StartSequence),
                Dock = DockStyle.Fill, Margin = new Padding(3, 5, 8, 4)
            };
            audioButton = MakeButton("选择配音", 88, 30);
            sampleButton = MakeButton("选择样品文件夹", 108, 30);
            subtitleStyleBox = new ComboBox {
                Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(3, 5, 8, 4)
            };
            subtitleStyleBox.Items.AddRange(new object[] {
                "CapCut 默认",
                "公司标准字幕（ZY Modern / 10 / 0725公司预设，草稿直改）"
            });
            if (RuntimeModes.SubtitleStyleLab) {
                subtitleStyleBox.Items.Clear();
                subtitleStyleBox.Items.Add("隔离实验：ZY Modern / 10 / 0725公司预设（已锁定）");
                subtitleStyleBox.SelectedIndex = 0;
                subtitleStyleBox.Enabled = false;
            } else {
                subtitleStyleBox.SelectedIndex = String.Equals(config.DefaultSubtitleStylePreset, "CompanyStandard", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }
            styleCalibrationButton = MakeButton("校准样式", 88, 30);

            groupGrid.Controls.Add(MakeLabel("项目"), 0, 0);
            groupGrid.Controls.Add(projectBox, 1, 0);
            groupGrid.Controls.Add(MakeLabel("材料"), 2, 0);
            groupGrid.Controls.Add(materialBox, 3, 0);
            groupGrid.Controls.Add(MakeLabel("语言"), 4, 0);
            groupGrid.Controls.Add(languageBox, 5, 0);
            groupGrid.Controls.Add(MakeLabel("导出前缀"), 0, 1);
            groupGrid.Controls.Add(prefixBox, 1, 1);
            groupGrid.Controls.Add(MakeLabel("起始编号"), 2, 1);
            groupGrid.Controls.Add(startSequenceBox, 3, 1);
            groupGrid.Controls.Add(MakeLabel("倍速计算"), 4, 1);
            groupGrid.Controls.Add(new Label {
                Text = "自动：样品时长 ÷ 配音剩余时长",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(5, 150, 105)
            }, 5, 1);
            groupGrid.Controls.Add(MakeLabel("独立配音"), 0, 2);
            groupGrid.Controls.Add(sharedAudioBox, 1, 2);
            groupGrid.Controls.Add(audioButton, 2, 2);
            groupGrid.Controls.Add(MakeLabel("样品文件夹"), 3, 2);
            groupGrid.Controls.Add(sampleBox, 4, 2);
            groupGrid.Controls.Add(sampleButton, 5, 2);
            groupGrid.Controls.Add(MakeLabel("字幕样式"), 0, 3);
            groupGrid.Controls.Add(subtitleStyleBox, 1, 3);
            groupGrid.Controls.Add(styleCalibrationButton, 2, 3);
            var styleHint = new Label {
                Text = "公司标准：保留当前字体 · 字号 10 · 第一排从左至右第 11 个预设",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(5, 150, 105)
            };
            groupGrid.Controls.Add(styleHint, 3, 3);
            groupGrid.SetColumnSpan(styleHint, 3);
            groupSettings.Controls.Add(groupGrid);

            var dropPanel = new Panel {
                Dock = DockStyle.Top, Height = 70, Margin = new Padding(22), Padding = new Padding(22),
                BackColor = Color.FromArgb(239, 246, 255), AllowDrop = true
            };
            var dropLabel = new Label {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Text = "将一个或多个视频拖到这里", Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235), AllowDrop = true
            };
            dropPanel.Controls.Add(dropLabel);

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(22, 8, 22, 4) };
            addButton = MakeButton("添加视频", 96, 32); addButton.Location = new Point(22, 8);
            categoryButton = MakeButton("扫描类目", 96, 32); categoryButton.Location = new Point(126, 8);
            removeButton = MakeButton("移除选中", 96, 32); removeButton.Location = new Point(230, 8);
            retryButton = MakeButton("重试失败", 96, 32); retryButton.Location = new Point(334, 8);
            rerunCompletedButton = MakeButton("重跑选中完成", 118, 32);
            rerunCompletedButton.Location = new Point(438, 8);
            // This is deliberately an isolated-lab-only escape hatch.  Formal
            // production tasks must be resumed from their workbench manifest,
            // never reset from a local client button.
            rerunCompletedButton.Visible = RuntimeModes.IsolatedLabRuntime;
            recycleManualSourceBox = new CheckBox {
                Text = "成功后回收源视频（仅手工任务）", AutoSize = true,
                Location = new Point(RuntimeModes.IsolatedLabRuntime ? 566 : 444, 15), Checked = false
            };
            summaryLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Width = 450, ForeColor = Color.FromArgb(71, 85, 105) };
            toolbar.Controls.Add(addButton); toolbar.Controls.Add(categoryButton); toolbar.Controls.Add(removeButton); toolbar.Controls.Add(retryButton); toolbar.Controls.Add(rerunCompletedButton);
            toolbar.Controls.Add(recycleManualSourceBox); toolbar.Controls.Add(summaryLabel);

            grid = new DataGridView {
                Dock = DockStyle.Top, Height = 270, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false, AutoGenerateColumns = false
            };
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
            grid.ColumnHeadersHeight = 34;
            grid.RowTemplate.Height = 32;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "状态", Width = 105 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Group", HeaderText = "项目 / 语言", Width = 190 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "视频数量", Width = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Progress", HeaderText = "完成进度", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Audio", HeaderText = "共用配音", Width = 210 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Output", HeaderText = "导出编号", Width = 210 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Error", HeaderText = "错误", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            var controls = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(22, 10, 22, 8), BackColor = Color.White };
            startButton = MakeButton("开始处理", 112, 38); startButton.Location = new Point(22, 10); startButton.BackColor = Color.FromArgb(37, 99, 235); startButton.ForeColor = Color.White; startButton.FlatStyle = FlatStyle.Flat; startButton.FlatAppearance.BorderSize = 0;
            pauseButton = MakeButton("暂停", 92, 38); pauseButton.Location = new Point(144, 10);
            stopButton = MakeButton("停止", 92, 38); stopButton.Location = new Point(246, 10);
            controls.Controls.Add(startButton); controls.Controls.Add(pauseButton); controls.Controls.Add(stopButton);

            var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 10, 22, 18) };
            var logTitle = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 25, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold) };
            logBox = new TextBox {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(15, 23, 42), ForeColor = Color.FromArgb(226, 232, 240),
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F)
            };
            logPanel.Controls.Add(logBox); logPanel.Controls.Add(logTitle);

            Controls.Add(logPanel); Controls.Add(controls); Controls.Add(grid); Controls.Add(toolbar); Controls.Add(dropPanel); Controls.Add(groupSettings); Controls.Add(settings); Controls.Add(header);

            browseOutput.Click += delegate { BrowseOutput(); };
            browseExe.Click += delegate { BrowseExecutable(); };
            addButton.Click += delegate { BrowseVideos(); };
            categoryButton.Click += delegate { ScanCategory(); };
            audioButton.Click += delegate { BrowseAudio(); };
            sampleButton.Click += delegate { BrowseSample(); };
            removeButton.Click += delegate { RemoveSelected(); };
            retryButton.Click += delegate { RetryFailed(); };
            rerunCompletedButton.Click += delegate { RerunSelectedCompletedLabTasks(); };
            startButton.Click += delegate { StartRun(); };
            pauseButton.Click += delegate { TogglePause(); };
            stopButton.Click += delegate { if (runner != null) runner.Stop(); };
            calibrateButton.Click += delegate { using (var form = new CalibrationForm(config, AppPaths.ConfigFile)) form.ShowDialog(this); };
            firstCaptionCalibrationButton.Click += delegate { using (var form = new CalibrationForm(config, AppPaths.ConfigFile, false, true)) form.ShowDialog(this); };
            styleCalibrationButton.Click += delegate { using (var form = new CalibrationForm(config, AppPaths.ConfigFile, true)) form.ShowDialog(this); };
            configButton.Click += delegate { OpenConfig(); };
            DragEnter += OnDragEnter; DragDrop += OnDragDrop;
            dropPanel.DragEnter += OnDragEnter; dropPanel.DragDrop += OnDragDrop;
            dropLabel.DragEnter += OnDragEnter; dropLabel.DragDrop += OnDragDrop;
            FormClosing += HandleFormClosing;
            logger.LineWritten += AppendLog;

            elapsedTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            elapsedTimer.Tick += delegate { RefreshGrid(); };
            elapsedTimer.Start();
            RefreshGrid();
            UpdateControls(false, false);
            if (handoffResult != null) {
                HandoffImportResult result = handoffResult;
                Shown += delegate {
                    logger.Info(String.Format("工作台交接清单已导入：新增 {0}，跳过 {1}，异常 {2}",
                        result.Added, result.Skipped, result.Issues.Count));
                    if (result.Issues.Count > 0) {
                        MessageBox.Show(this, String.Join(Environment.NewLine, result.Issues.Take(12).ToArray()),
                            "交接清单存在异常", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
            }
            if (retryFailedOnStart) {
                // This is the exact same guarded path as the visible "Retry failed"
                // button.  It is intentionally opt-in so normal auto-start never
                // requeues a failed formal task by surprise.
                Shown += delegate { BeginInvoke((Action)RetryFailed); };
            } else if (autoStart) {
                Shown += delegate { BeginInvoke((Action)StartRun); };
            }
        }

        private void RestoreInterruptedTasks()
        {
            bool changed = false;
            foreach (VideoTask task in tasks) {
                TaskState state = task.GetState();
                if (state != TaskState.Waiting && state != TaskState.Completed && state != TaskState.Failed) {
                    task.SetState(TaskState.Waiting);
                    task.Error = "上次运行中断，已恢复为等待状态";
                    changed = true;
                }
            }
            foreach (IGrouping<string, VideoTask> group in tasks
                .Where(task => !String.IsNullOrWhiteSpace(task.GroupId))
                .GroupBy(task => task.GetGroupKey(), StringComparer.OrdinalIgnoreCase)) {
                VideoTask completed = group
                    .Where(task => task.GetState() == TaskState.Completed && !String.IsNullOrWhiteSpace(task.OutputPath))
                    .OrderBy(task => task.Sequence)
                    .FirstOrDefault();
                if (completed == null) continue;
                string groupOutputDirectory = Path.GetDirectoryName(completed.OutputPath);
                if (String.IsNullOrWhiteSpace(groupOutputDirectory)) continue;
                foreach (VideoTask task in group.Where(task => task.GetState() != TaskState.Completed)) {
                    string extension = Path.GetExtension(task.OutputPath);
                    if (String.IsNullOrWhiteSpace(extension)) extension = config.ExportExtension;
                    // Workbench handoff tasks carry the full file stem as the
                    // prefix (..._001_04); appending the sequence again gave
                    // ..._001_044 on 10-16.
                    if (String.Equals(Path.GetFileNameWithoutExtension(task.OutputPath ?? ""),
                        (task.OutputPrefix ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    string expected = OutputNaming.ProjectBatchPath(
                        task.OutputPrefix, task.Sequence, groupOutputDirectory, extension);
                    if (String.Equals(task.OutputPath, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    task.OutputPath = expected;
                    changed = true;
                    logger.Warn("已按同一项目语言组的完成记录恢复输出路径: " + expected);
                }
            }
            if (changed) {
                store.Save(tasks);
                // The receiver/dashboard reads the handoff status file rather than
                // this local queue.  Without this write an interrupted export can
                // remain displayed as "running" forever after the next launch.
                HandoffStatusWriter.WriteAll(tasks, false, false);
            }
        }

        private void BrowseVideos()
        {
            using (var dialog = new OpenFileDialog {
                Multiselect = true, Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v;*.flv;*.webm;*.ts;*.mts|所有文件|*.*",
                Title = "选择要识别字幕的视频"
            }) {
                if (dialog.ShowDialog(this) == DialogResult.OK) AddVideos(dialog.FileNames);
            }
        }

        private void AddVideos(IEnumerable<string> paths)
        {
            SaveSettings();
            Directory.CreateDirectory(config.OutputDirectory);
            MediaMetadata audioMetadata;
            List<MediaMetadata> verticalSamples = null;
            try {
                audioMetadata = WindowsMediaMetadataReader.Read(config.SharedAudioPath);
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "无法建立任务", MessageBoxButtons.OK, MessageBoxIcon.Error);
                logger.Error("读取配音失败: " + ex.Message);
                return;
            }

            var existingSources = new HashSet<string>(tasks.Select(t => Path.GetFullPath(t.VideoPath)), StringComparer.OrdinalIgnoreCase);
            string groupId = Guid.NewGuid().ToString("N");
            int sequence = config.StartSequence;
            int added = 0;
            var errors = new List<string>();
            foreach (string path in paths) {
                if (!File.Exists(path) || !VideoExtensions.Contains(Path.GetExtension(path))) continue;
                string full = Path.GetFullPath(path);
                if (existingSources.Contains(full)) continue;
                SamplePlan plan;
                try {
                    MediaMetadata videoMetadata = WindowsMediaMetadataReader.Read(full);
                    if (audioMetadata.DurationSeconds - videoMetadata.DurationSeconds > 0.10 && verticalSamples == null) {
                        verticalSamples = SamplePlanner.LoadVerticalSamples(config.SampleDirectory);
                        logger.Info(String.Format("样品文件夹检测完成：{0} 条真实竖屏素材", verticalSamples.Count));
                    }
                    string selectionKey = (config.DefaultProjectName ?? "") + "|" +
                        (config.DefaultMaterialName ?? "") + "|" +
                        CapCutTaskIdentity.NormalizePath(full);
                    plan = SamplePlanner.CreateDeterministic(videoMetadata, audioMetadata,
                        verticalSamples, selectionKey);
                    if (plan.RequiresSample) {
                        logger.Info(String.Format(
                            "随机样品与倍速：{0} -> {1}，视频 {2:0.000}s，配音 {3:0.000}s，剩余 {4:0.000}s，样品 {5:0.000}s，速度 {6:0.00}x",
                            Path.GetFileName(full), Path.GetFileName(plan.SamplePath),
                            videoMetadata.DurationSeconds, audioMetadata.DurationSeconds,
                            plan.AvailableSeconds, plan.SampleDurationSeconds, plan.Speed));
                    } else {
                        logger.Info(String.Format(
                            "无需结尾样品：{0}，视频 {1:0.000}s，配音 {2:0.000}s",
                            Path.GetFileName(full), videoMetadata.DurationSeconds, audioMetadata.DurationSeconds));
                    }
                } catch (Exception ex) {
                    errors.Add(Path.GetFileName(full) + ": " + ex.Message);
                    logger.Error("无法为视频计算随机样品: " + Path.GetFileName(full) + " / " + ex.Message);
                    continue;
                }
                string output = OutputNaming.ProjectBatchPath(config.DefaultOutputPrefix, sequence, config.OutputDirectory, config.ExportExtension);
                var task = new VideoTask {
                    VideoPath = full,
                    AudioPath = config.SharedAudioPath,
                    SamplePath = plan.SamplePath,
                    OutputPath = output,
                    GroupId = groupId,
                    ProjectName = config.DefaultProjectName,
                    MaterialName = config.DefaultMaterialName,
                    LanguageName = config.DefaultLanguageName,
                    OutputPrefix = config.DefaultOutputPrefix,
                    ExportResolution = config.ExportResolution,
                    SubtitleStylePreset = config.DefaultSubtitleStylePreset,
                    SampleSpeed = plan.Speed,
                    Sequence = sequence,
                    TaskOrigin = TaskOrigins.Manual,
                    SourceRootPath = recycleManualSourceBox.Checked ? Path.GetDirectoryName(full) : "",
                    DeleteSourceAfterSuccess = SourceDeletionPolicy.ShouldRecycle(TaskOrigins.Manual, recycleManualSourceBox.Checked),
                    PreserveDraftOnFailure = diagnosticFirstCaptionQNoOp
                };
                task.SampleAssignment = SamplePlanner.BuildAssignment(plan, task);
                tasks.Add(task);
                existingSources.Add(full);
                sequence++;
                added++;
            }
            store.Save(tasks);
            logger.Info("已添加项目语言批次: " + config.DefaultProjectName + " / " + config.DefaultLanguageName + " / " + added + " 条");
            RefreshGrid();
            if (errors.Count > 0) {
                MessageBox.Show(this, String.Join(Environment.NewLine, errors.ToArray()),
                    "部分视频未加入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ScanCategory()
        {
            if (runner != null && runner.IsRunning) return;
            SaveSettings();
            using (var dialog = new CategoryBatchForm(config)) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                List<VideoTask> pending = tasks
                    .Where(task => task.GetState() == TaskState.Waiting)
                    .ToList();
                string importedCategory = dialog.ImportedTasks
                    .Select(task => task.ProjectName)
                    .FirstOrDefault();
                if (pending.Any(task =>
                    !task.DeleteSourceAfterSuccess ||
                    !String.Equals(task.ProjectName, importedCategory, StringComparison.OrdinalIgnoreCase))) {
                    MessageBox.Show(this,
                        "队列中已有其他类目或手动任务。请先完成或移除等待任务；类目批处理一次只运行一个类目。",
                        "不能混合类目", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var existingSources = new HashSet<string>(
                    tasks.Where(task => !String.IsNullOrWhiteSpace(task.VideoPath))
                        .Select(task => Path.GetFullPath(task.VideoPath)),
                    StringComparer.OrdinalIgnoreCase);
                List<VideoTask> imported = dialog.ImportedTasks
                    .Where(task => existingSources.Add(Path.GetFullPath(task.VideoPath)))
                    .ToList();
                tasks.AddRange(imported);
                store.Save(tasks);
                logger.Info(String.Format(
                    "类目扫描已加入队列：{0}，配音任务 {1} 组，视频 {2} 条。",
                    config.LastCategoryName,
                    imported.Select(task => task.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    imported.Count));
                RefreshGrid();
            }
        }

        private void BrowseAudio()
        {
            using (var dialog = new OpenFileDialog {
                Multiselect = false, Filter = "音频文件|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.wma|所有文件|*.*",
                Title = "选择本项目语言共用的独立配音"
            }) {
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    sharedAudioBox.Text = dialog.FileName;
                    SaveSettings();
                }
            }
        }

        private void BrowseSample()
        {
            using (var dialog = new FolderBrowserDialog {
                Description = "选择竖屏结尾样品文件夹；客户端会自动过滤横屏素材",
                SelectedPath = sampleBox.Text
            }) {
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    sampleBox.Text = dialog.SelectedPath;
                    SaveSettings();
                }
            }
        }

        private void RemoveSelected()
        {
            if (runner != null && runner.IsRunning) return;
            var groupKeys = new HashSet<string>(
                grid.SelectedRows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Tag)),
                StringComparer.OrdinalIgnoreCase);
            tasks.RemoveAll(task => groupKeys.Contains(task.GetGroupKey()));
            store.Save(tasks);
            RefreshGrid();
        }

        private void RetryFailed()
        {
            if (runner != null && runner.IsRunning) return;
            SaveSettings();
            int count = 0;
            foreach (VideoTask task in tasks.Where(t => t.GetState() == TaskState.Failed)) {
                task.SetState(TaskState.Waiting);
                task.Error = "";
                count++;
            }
            store.Save(tasks);
            // A Dashboard safety-continue command reads the handoff status file,
            // not this in-memory grid.  Persist the re-queued state immediately
            // so a clicked retry cannot remain visible remotely as a stale
            // failure.  Then use the same guarded start path as the main button:
            // it will process all incomplete tasks but never re-run a verified
            // completed sibling.
            HandoffStatusWriter.WriteAll(tasks, false, false);
            logger.Info("已将失败任务放回队列: " + count);
            RefreshGrid();
            if (count > 0) BeginInvoke((Action)StartRun);
        }

        private void RerunSelectedCompletedLabTasks()
        {
            if (!RuntimeModes.IsolatedLabRuntime || (runner != null && runner.IsRunning)) return;

            var selectedGroupKeys = new HashSet<string>(
                grid.SelectedRows.Cast<DataGridViewRow>()
                    .Select(row => Convert.ToString(row.Tag))
                    .Where(key => !String.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            if (selectedGroupKeys.Count == 0) {
                MessageBox.Show(this, "请先在列表中选中需要重跑的内测完成任务。", "隔离内测重跑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<VideoTask> targets = tasks.Where(task =>
                task.GetState() == TaskState.Completed &&
                selectedGroupKeys.Contains(task.GetGroupKey())).ToList();
            if (targets.Count == 0) {
                MessageBox.Show(this, "所选条目没有可重跑的已完成内测任务。", "隔离内测重跑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string attemptStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (VideoTask task in targets) {
                string previousOutput = task.OutputPath;
                task.OutputPath = OutputNaming.LabRerunPath(previousOutput, attemptStamp);
                task.TemporaryOutputPath = "";
                task.PostProcessWorkingPath = "";
                task.DirectDraftCheckpoint = null;
                task.CaptionDraftCheckpoint = null;
                task.TimelineAssemblyCheckpoint = null;
                task.Error = "";
                task.StartedAt = "";
                task.FinishedAt = "";
                task.Attempts = 0;
                task.SetState(TaskState.Waiting);
            }
            store.Save(tasks);
            HandoffStatusWriter.WriteAll(tasks, false, false);
            logger.Info("隔离内测已将选中的完成任务重置为等待；旧成片保留，新输出将带 lab-rerun 时间戳: " + targets.Count);
            RefreshGrid();
        }

        private void StartRun()
        {
            // A retry callback may already be queued when the preceding run
            // reaches a terminal state.  Never let that callback start a
            // second worker during the 900 ms handoff-exit grace period.
            if (autoExitScheduled || IsDisposed || Disposing ||
                (runner != null && runner.IsRunning)) return;
            try {
                SaveSettings();
                List<VideoTask> waiting = tasks.Where(task =>
                    task.GetState() == TaskState.Waiting ||
                    BatchRunner.IsDraftReleaseOnlyTask(task)).ToList();
                if (RuntimeModes.IsolatedLabRuntime) {
                    foreach (VideoTask task in waiting) {
                        RuntimeModes.ValidateIsolatedLabTaskAndDependencies(task, config);
                    }
                    logger.Info("隔离内测运行前校验通过：运行身份=" +
                        RuntimeModes.IsolatedLabProfileSource + "，字幕样式=LabZYModern，固定草稿引擎可用。");
                }
                if (!RuntimeModes.IsolatedLabRuntime &&
                    waiting.Any(task => RuntimeModes.ShouldUseDirectDraftSubtitleStyle(
                        String.IsNullOrWhiteSpace(task.SubtitleStylePreset)
                            ? config.DefaultSubtitleStylePreset
                            : task.SubtitleStylePreset))) {
                    RuntimeModes.ValidateDirectDraftSubtitleStyleDependencies(config);
                    logger.Info(
                        "正式公司字幕草稿直改预检通过：ZY Modern / 10 / 0725公司预设，固定草稿引擎可用。");
                }

                // The isolated laboratory can be closed while CapCut is in its
                // launch phase.  A stale in-memory/JSON "Launching" value must
                // never make the visible queue say "processing" while Start
                // reports zero work.  Recover only the lab's non-terminal
                // states here; formal recovery keeps its existing explicit
                // Dashboard/receiver contract.
                if (waiting.Count == 0 && RuntimeModes.IsolatedLabRuntime) {
                    int recovered = 0;
                    foreach (VideoTask task in tasks.Where(task =>
                        task.GetState() != TaskState.Waiting &&
                        task.GetState() != TaskState.Completed &&
                        task.GetState() != TaskState.Failed)) {
                        task.SetState(TaskState.Waiting);
                        task.Error = "上一次内测客户端未完成启动；已恢复为等待，可重新开始。";
                        task.FinishedAt = "";
                        recovered++;
                    }
                    if (recovered > 0) {
                        store.Save(tasks);
                        HandoffStatusWriter.WriteAll(tasks, false, false);
                        logger.Warn("已恢复 " + recovered + " 条残留‘正在处理’的内测任务为等待状态。");
                        RefreshGrid();
                        waiting = tasks.Where(task =>
                            task.GetState() == TaskState.Waiting ||
                            BatchRunner.IsDraftReleaseOnlyTask(task)).ToList();
                    }
                }
                if (waiting.Count == 0) {
                    MessageBox.Show(this, "队列中没有等待处理的任务。", "自动字幕", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (diagnosticFirstCaptionQNoOp && waiting.Count != 1) {
                    MessageBox.Show(this, "Q 失效诊断模式一次只允许运行一条专用测试任务。", "诊断模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                List<string> categoryNames = waiting
                    .Where(task => task.DeleteSourceAfterSuccess)
                    .Select(task => task.ProjectName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool allFromWorkbench = waiting.All(task => task.IsHandoffTask);
                if (!allFromWorkbench && (categoryNames.Count > 1 ||
                    (categoryNames.Count == 1 && waiting.Any(task => !task.DeleteSourceAfterSuccess)))) {
                    MessageBox.Show(this,
                        "类目批处理不能与其他类目或手动任务混在同一次运行中。请先移除其他等待任务。",
                        "队列需要整理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Directory.CreateDirectory(config.OutputDirectory);
                // The workbench handoff UI is only a status surface.  Keeping it
                // visible while CapCut is being driven lets Windows promote this
                // form after CapCut is closed/reopened, which steals the focus
                // required by the precise draft-reopen step.  Hide only the
                // automatically launched formal handoff window; manually opened
                // clients and the isolated lab remain visible and unchanged.
                HideHandoffWindowForAutomation();

                runner = new BatchRunner(config, new CapCutAutomation(config, logger, diagnosticFirstCaptionQNoOp), store, logger);
                runner.TaskChanged += delegate { BeginInvoke((Action)RefreshGrid); };
                runner.RunStateChanged += delegate(bool running, bool paused) { BeginInvoke((Action)(delegate { UpdateControls(running, paused); RefreshGrid(); })); };
                runner.BatchFinished += delegate {
                    try {
                        BeginInvoke((Action)(delegate {
                            if (IsDisposed) return;
                            if (closePending) {
                                closePending = false;
                                SaveSettings();
                                Close();
                            } else if (autoExitAfterTerminalHandoff) {
                                ScheduleTerminalHandoffExit();
                            }
                        }));
                    } catch { }
                };
                runner.Start(tasks);
                UpdateControls(true, false);
            } catch (Exception ex) {
                RestoreHandoffWindowAfterStartFailure();
                MessageBox.Show(this, ex.Message, "无法开始", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (runner != null && runner.IsRunning) {
                e.Cancel = true;
                if (!closePending) {
                    closePending = true;
                    logger.Info("收到关闭请求：正在安全停止批处理、释放草稿租约并恢复用户目录；完成后自动关闭窗口。");
                    runner.Stop();
                }
                return;
            }
            SaveSettings();
        }

        private void HideHandoffWindowForAutomation()
        {
            if (!autoExitAfterTerminalHandoff || handoffWindowHiddenForAutomation || IsDisposed) return;
            handoffWindowHiddenForAutomation = true;
            logger.Info("工作台交接模式：批处理窗口已最小化并保留在任务栏，CapCut 自动化期间不会抢占前台焦点。");
            ShowInTaskbar = true;
            WindowState = FormWindowState.Minimized;
        }

        private void RestoreHandoffWindowAfterStartFailure()
        {
            if (!handoffWindowHiddenForAutomation || IsDisposed) return;
            handoffWindowHiddenForAutomation = false;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ScheduleTerminalHandoffExit()
        {
            if (autoExitScheduled || IsDisposed) return;
            autoExitScheduled = true;
            logger.Info("工作台交接批处理已进入终态；客户端将在状态落盘后自动退出，下一项目可安全启动新实例。");
            var timer = new System.Windows.Forms.Timer { Interval = 900 };
            timer.Tick += delegate {
                timer.Stop();
                timer.Dispose();
                if (!IsDisposed) Close();
            };
            timer.Start();
        }

        private void TogglePause()
        {
            if (runner == null || !runner.IsRunning) return;
            if (runner.IsPaused) runner.Resume(); else runner.Pause();
        }

        private void BrowseOutput()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择字幕视频输出目录", SelectedPath = outputBox.Text }) {
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    outputBox.Text = dialog.SelectedPath;
                    SaveSettings();
                    ReassignWaitingOutputs();
                }
            }
        }

        private void BrowseExecutable()
        {
            using (var dialog = new OpenFileDialog { Filter = "程序|*.exe", Title = "选择剪映专业版或 CapCut 程序" }) {
                if (dialog.ShowDialog(this) == DialogResult.OK) { executableBox.Text = dialog.FileName; SaveSettings(); }
            }
        }

        private void ReassignWaitingOutputs()
        {
            foreach (VideoTask task in tasks.Where(t => t.GetState() == TaskState.Waiting)) {
                task.OutputPath = String.IsNullOrWhiteSpace(task.OutputPrefix)
                    ? OutputNaming.UniqueCaptionPath(task.VideoPath, config.OutputDirectory, config.ExportExtension, tasks.Select(t => t.OutputPath))
                    : OutputNaming.ProjectBatchPath(task.OutputPrefix, task.Sequence, config.OutputDirectory, config.ExportExtension);
            }
            store.Save(tasks);
            RefreshGrid();
        }

        private void SaveSettings()
        {
            string output = (outputBox == null ? config.OutputDirectory : outputBox.Text).Trim();
            if (!String.IsNullOrWhiteSpace(output)) config.OutputDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(output));
            config.CapCutPath = (executableBox == null ? config.CapCutPath : executableBox.Text).Trim();
            config.DefaultProjectName = (projectBox == null ? config.DefaultProjectName : projectBox.Text).Trim();
            config.DefaultMaterialName = (materialBox == null ? config.DefaultMaterialName : materialBox.Text).Trim();
            config.DefaultLanguageName = (languageBox == null ? config.DefaultLanguageName : languageBox.Text).Trim();
            config.DefaultOutputPrefix = (prefixBox == null ? config.DefaultOutputPrefix : prefixBox.Text).Trim();
            config.SharedAudioPath = (sharedAudioBox == null ? config.SharedAudioPath : sharedAudioBox.Text).Trim();
            config.SampleDirectory = (sampleBox == null ? config.SampleDirectory : sampleBox.Text).Trim();
            config.StartSequence = startSequenceBox == null ? config.StartSequence : Convert.ToInt32(startSequenceBox.Value);
            config.ExportResolution = "2K";
            config.DefaultSubtitleStylePreset = RuntimeModes.SubtitleStyleLab
                ? "LabZYModern"
                : (subtitleStyleBox == null || subtitleStyleBox.SelectedIndex <= 0 ? "Default" : "CompanyStandard");
            config.Save(AppPaths.ConfigFile);
        }

        private void OpenConfig()
        {
            SaveSettings();
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppPaths.ConfigFile + "\"") { UseShellExecute = true });
        }

        private void RefreshGrid()
        {
            if (IsDisposed) return;
            string selectedGroup = grid.CurrentRow == null ? "" : Convert.ToString(grid.CurrentRow.Tag);
            grid.Rows.Clear();
            var groups = tasks
                .GroupBy(task => task.GetGroupKey(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Min(task => task.CreatedAt))
                .ToList();
            foreach (var taskGroup in groups) {
                List<VideoTask> groupTasks = taskGroup.OrderBy(task => task.Sequence).ToList();
                VideoTask first = groupTasks.First();
                int completed = groupTasks.Count(task => task.GetState() == TaskState.Completed);
                int failed = groupTasks.Count(task => task.GetState() == TaskState.Failed);
                VideoTask active = groupTasks.FirstOrDefault(task =>
                    task.GetState() != TaskState.Waiting &&
                    task.GetState() != TaskState.Completed &&
                    task.GetState() != TaskState.Failed);
                string stateText;
                TaskState colorState;
                if (active != null) {
                    colorState = active.GetState();
                    stateText = TaskStateText.ToDisplay(colorState);
                } else if (completed == groupTasks.Count) {
                    colorState = TaskState.Completed;
                    stateText = TaskStateText.ToDisplay(colorState);
                } else if (failed > 0) {
                    colorState = TaskState.Failed;
                    stateText = failed == groupTasks.Count ? "失败" : "部分失败";
                } else {
                    colorState = TaskState.Waiting;
                    stateText = TaskStateText.ToDisplay(colorState);
                }

                string groupName = String.IsNullOrWhiteSpace(first.GroupId)
                    ? "旧任务"
                    : first.ProjectName + " / " + first.MaterialName + " / " + first.LanguageName +
                      (String.IsNullOrWhiteSpace(first.VoiceTaskNumber) ? "" : " / #" + first.VoiceTaskNumber);
                int firstSequence = groupTasks.Min(task => task.Sequence);
                int lastSequence = groupTasks.Max(task => task.Sequence);
                string outputRange;
                if (first.IsHandoffTask) {
                    outputRange = Path.GetFileName(groupTasks.First().OutputPath);
                    if (groupTasks.Count > 1)
                        outputRange += " – " + Path.GetFileName(groupTasks.Last().OutputPath);
                } else {
                    outputRange = first.OutputPrefix + firstSequence;
                    if (lastSequence != firstSequence) outputRange += " – " + first.OutputPrefix + lastSequence;
                    outputRange += config.ExportExtension;
                }
                string error = String.Join("；", groupTasks
                    .Where(task => !String.IsNullOrWhiteSpace(task.Error))
                    .Select(task => task.Error)
                    .Distinct()
                    .Take(2)
                    .ToArray());
                int rowIndex = grid.Rows.Add(
                    stateText,
                    groupName,
                    groupTasks.Count,
                    completed + " / " + groupTasks.Count,
                    Path.GetFileName(first.AudioPath),
                    outputRange,
                    error);
                DataGridViewRow row = grid.Rows[rowIndex];
                row.Tag = taskGroup.Key;
                if (colorState == TaskState.Completed) row.Cells[0].Style.ForeColor = Color.FromArgb(5, 150, 105);
                else if (colorState == TaskState.Failed) row.Cells[0].Style.ForeColor = Color.FromArgb(220, 38, 38);
                else if (colorState != TaskState.Waiting) row.Cells[0].Style.ForeColor = Color.FromArgb(37, 99, 235);
                if (String.Equals(taskGroup.Key, selectedGroup, StringComparison.OrdinalIgnoreCase)) row.Selected = true;
            }
            int totalCompleted = tasks.Count(t => t.GetState() == TaskState.Completed);
            int totalFailed = tasks.Count(t => t.GetState() == TaskState.Failed);
            int waiting = tasks.Count - totalCompleted - totalFailed;
            summaryLabel.Text = String.Format("项目语言组 {0}    视频 {1}    等待/处理中 {2}    完成 {3}    失败 {4}",
                groups.Count, tasks.Count, waiting, totalCompleted, totalFailed);
        }

        private static string FormatElapsed(VideoTask task)
        {
            DateTime start;
            if (!DateTime.TryParse(task.StartedAt, out start)) return "--";
            DateTime end;
            if (!DateTime.TryParse(task.FinishedAt, out end)) end = DateTime.Now;
            TimeSpan elapsed = end - start;
            return elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        }

        private void UpdateControls(bool running, bool paused)
        {
            startButton.Enabled = !running;
            pauseButton.Enabled = running;
            pauseButton.Text = paused ? "继续" : "暂停";
            stopButton.Enabled = running;
            retryButton.Enabled = !running;
            addButton.Enabled = !running;
            categoryButton.Enabled = !running;
            removeButton.Enabled = !running;
            outputBox.Enabled = !running;
            executableBox.Enabled = !running;
            projectBox.Enabled = !running;
            materialBox.Enabled = !running;
            languageBox.Enabled = !running;
            prefixBox.Enabled = !running;
            sharedAudioBox.Enabled = !running;
            sampleBox.Enabled = !running;
            startSequenceBox.Enabled = !running;
            subtitleStyleBox.Enabled = !running;
            styleCalibrationButton.Enabled = !running;
            audioButton.Enabled = !running;
            sampleButton.Enabled = !running;
        }

        private void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action<string>)AppendLog, line); return; }
            logBox.AppendText(line + Environment.NewLine);
            if (logBox.TextLength > 150000) logBox.Text = logBox.Text.Substring(logBox.TextLength - 100000);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (runner != null && runner.IsRunning) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            string sampleDirectory = files.FirstOrDefault(path => Directory.Exists(path));
            if (sampleDirectory != null) sampleBox.Text = sampleDirectory;
            string[] expanded = files.Where(path => !Directory.Exists(path)).ToArray();
            string audio = expanded.FirstOrDefault(path => AudioExtensions.Contains(Path.GetExtension(path)));
            if (audio != null) sharedAudioBox.Text = audio;
            AddVideos(expanded.Where(path => VideoExtensions.Contains(Path.GetExtension(path))));
        }

        private static Button MakeButton(string text, int width, int height)
        {
            return new Button { Text = text, Width = width, Height = height, FlatStyle = FlatStyle.System, Margin = new Padding(4) };
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(71, 85, 105) };
        }

        private static TextBox MakeTextBox(string text)
        {
            return new TextBox { Text = text ?? "", Dock = DockStyle.Fill, Margin = new Padding(3, 5, 8, 4) };
        }
    }

    internal static class TextBoxCompatibility
    {
        public static void PlaceholderTextCompat(this TextBox box, string placeholder)
        {
            box.Tag = placeholder;
            if (String.IsNullOrEmpty(box.Text)) box.Text = "";
        }
    }
}
