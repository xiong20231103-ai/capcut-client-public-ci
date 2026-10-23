using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CapCutBatchClient
{
    public sealed class CalibrationForm : Form
    {
        private const int HotKeyId = 0xCA91;
        private readonly AppConfig config;
        private readonly string configPath;
        private readonly List<CalibrationStep> steps;
        private readonly Label progressLabel;
        private readonly Label actionLabel;
        private readonly Label coordinateLabel;
        private readonly ListBox resultList;
        private readonly bool firstCaptionOnly;
        private readonly Dictionary<string, PointF> pendingCoordinates = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase);
        private int index;

        public CalibrationForm(AppConfig config, string configPath)
            : this(config, configPath, false)
        {
        }

        public CalibrationForm(AppConfig config, string configPath, bool subtitleStyleOnly)
            : this(config, configPath, subtitleStyleOnly, false)
        {
        }

        public CalibrationForm(AppConfig config, string configPath, bool subtitleStyleOnly, bool firstCaptionOnly)
        {
            this.config = config;
            this.configPath = configPath;
            this.firstCaptionOnly = firstCaptionOnly;
            var fullSteps = new List<CalibrationStep> {
                new CalibrationStep("CreateProject", "新建项目 / 开始创作"),
                new CalibrationStep("Import", "导入按钮"),
                new CalibrationStep("MediaItem", "导入后的第一个媒体缩略图"),
                new CalibrationStep("MediaSearchField", "媒体区的“搜索素材名称”输入框"),
                new CalibrationStep("TimelineDrop", "视频轨道时间线落点"),
                new CalibrationStep("MainVideoClip", "时间线上的主视频片段（用于后续替换）"),
                new CalibrationStep("TimelineZeroDrop", "主视频轨道零点落点（用于后续替换）"),
                new CalibrationStep("VideoTrackMute", "原视频轨道静音按钮"),
                new CalibrationStep("AudioItem", "导入后的音频缩略图"),
                new CalibrationStep("AudioTimelineDrop", "音频轨道零点落点"),
                new CalibrationStep("SampleItem", "媒体区中的结尾样品缩略图"),
                new CalibrationStep("SampleTimelineEndDrop", "主视频末尾的样品落点"),
                new CalibrationStep("SampleClip", "时间线上的结尾样品片段"),
                new CalibrationStep("SpeedPanel", "样品片段的变速面板"),
                new CalibrationStep("NormalSpeed", "正常变速选项"),
                new CalibrationStep("SpeedValue", "正常变速倍速输入框"),
                new CalibrationStep("TextPanel", "文本或字幕面板"),
                new CalibrationStep("AutoCaption", "智能字幕 / 识别字幕"),
                new CalibrationStep("StartRecognition", "开始识别按钮"),
                new CalibrationStep("FirstCaptionAtZeroProbe", "首帧字幕检测点（首条字幕左端内部）"),
                new CalibrationStep("FirstCaptionLeftEdge", "首条字幕片段左边缘"),
                new CalibrationStep("FirstCaptionOneFrameRight", "首条字幕左边缘向右一帧的位置"),
                new CalibrationStep("Export", "编辑器右上角导出按钮"),
                new CalibrationStep("ExportPathField", "导出窗口的保存位置输入框"),
                new CalibrationStep("ExportNameField", "导出窗口的文件名输入框"),
                new CalibrationStep("ExportResolution", "导出窗口的分辨率下拉框"),
                new CalibrationStep("ExportResolution2K", "分辨率列表中的 2K 选项"),
                new CalibrationStep("ExportConfirm", "导出窗口的确认导出按钮"),
                new CalibrationStep("ExportDone", "导出完成窗口的完成按钮"),
                new CalibrationStep("BackToHome", "返回首页按钮")
            };
            var subtitleSteps = new List<CalibrationStep> {
                new CalibrationStep("CaptionApplyAll", "字幕样式的“对全部主字幕生效”复选框"),
                new CalibrationStep("CaptionFontField", "字幕字体输入框"),
                new CalibrationStep("CaptionFontSizeField", "字幕字号输入框"),
                new CalibrationStep("CaptionCompanyPreset", "公司标准字幕预设（第一排最右侧）")
            };
            if (firstCaptionOnly) {
                steps = new List<CalibrationStep> {
                    new CalibrationStep("FirstCaptionAtZeroProbe", "首帧字幕检测点（首条字幕左端内部）"),
                    new CalibrationStep("FirstCaptionLeftEdge", "首条字幕片段左边缘（时间线第0帧）"),
                    new CalibrationStep("FirstCaptionOneFrameRight", "首条字幕左边缘向右一帧的位置")
                };
            } else if (subtitleStyleOnly) steps = subtitleSteps;
            else {
                fullSteps.InsertRange(20, subtitleSteps);
                steps = fullSteps;
            }

            Text = "剪映按钮校准";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(680, 520);
            MinimumSize = new Size(620, 480);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 248, 251);

            var title = new Label { Text = "按钮位置校准", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
            var help = new Label {
                Text = "切换到剪映，把鼠标停在目标控件中央，然后按 F8。可以跳过系统已经能识别的控件。",
                ForeColor = Color.FromArgb(71, 85, 105), AutoSize = false, Location = new Point(26, 58), Size = new Size(610, 42)
            };
            progressLabel = new Label { Location = new Point(26, 112), Size = new Size(180, 24), ForeColor = Color.FromArgb(37, 99, 235) };
            actionLabel = new Label { Location = new Point(26, 140), Size = new Size(610, 46), Font = new Font(Font.FontFamily, 14F, FontStyle.Bold) };
            coordinateLabel = new Label { Location = new Point(26, 188), Size = new Size(610, 28), ForeColor = Color.FromArgb(100, 116, 139) };
            resultList = new ListBox { Location = new Point(26, 226), Size = new Size(610, 180), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };

            var skip = MakeButton("跳过此项", 26);
            var finish = MakeButton("保存并结束", 146);
            if (firstCaptionOnly) skip.Enabled = false;
            skip.Click += delegate { AddSkipped(); MoveNext(); };
            finish.Click += delegate { SaveAndClose(); };

            Controls.Add(title); Controls.Add(help); Controls.Add(progressLabel); Controls.Add(actionLabel);
            Controls.Add(coordinateLabel); Controls.Add(resultList); Controls.Add(skip); Controls.Add(finish);
            Shown += delegate { ShowCurrent(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!NativeMethods.RegisterHotKey(Handle, HotKeyId, 0, (uint)Keys.F8)) {
                MessageBox.Show(this, "无法注册 F8 热键，请关闭占用 F8 的软件后重试。", "校准", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            NativeMethods.UnregisterHotKey(Handle, HotKeyId);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_HOTKEY = 0x0312;
            if (message.Msg == WM_HOTKEY && message.WParam.ToInt32() == HotKeyId) {
                CaptureCurrent();
                return;
            }
            base.WndProc(ref message);
        }

        private void CaptureCurrent()
        {
            if (index >= steps.Count) return;
            IntPtr window = NativeMethods.GetForegroundWindow();
            IntPtr rootOwner = NativeMethods.GetAncestor(window, 3);
            if (rootOwner != IntPtr.Zero) window = rootOwner;
            if (!IsCapCutWindow(window)) {
                coordinateLabel.Text = "记录失败：当前前台窗口不是 CapCut。请切换到 CapCut 后重试。";
                return;
            }
            System.Windows.Rect bounds;
            if (window == IntPtr.Zero || !TryGetCapCutBounds(window, out bounds)) {
                coordinateLabel.Text = "没有取得有效窗口，请切换到剪映后重试。";
                return;
            }

            Point cursor = Cursor.Position;
            double x = (cursor.X - bounds.Left) / bounds.Width;
            double y = (cursor.Y - bounds.Top) / bounds.Height;
            if (x < 0 || x > 1 || y < 0 || y > 1) {
                coordinateLabel.Text = "鼠标不在当前前台窗口内，请重新定位。";
                return;
            }

            CalibrationStep step = steps[index];
            pendingCoordinates[step.Key] = new PointF((float)Math.Round(x, 6), (float)Math.Round(y, 6));
            resultList.Items.Add(String.Format("已记录  {0}  X={1:0.0000}, Y={2:0.0000}", step.Label, x, y));
            coordinateLabel.Text = String.Format("已记录 {0}: X={1:0.000000}, Y={2:0.000000}", step.Key, x, y);
            MoveNext();
        }

        private bool IsCapCutWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            try {
                uint processId;
                NativeMethods.GetWindowThreadProcessId(window, out processId);
                if (processId == 0) return false;
                string actual = Process.GetProcessById((int)processId).ProcessName;
                foreach (string configured in config.ProcessNames ?? new string[0]) {
                    if (String.Equals(Path.GetFileNameWithoutExtension(configured), actual, StringComparison.OrdinalIgnoreCase)) return true;
                }
            } catch { }
            return false;
        }

        private static bool TryGetCapCutBounds(IntPtr window, out System.Windows.Rect bounds)
        {
            bounds = System.Windows.Rect.Empty;
            try {
                System.Windows.Automation.AutomationElement element =
                    System.Windows.Automation.AutomationElement.FromHandle(window);
                if (element != null) {
                    System.Windows.Rect automationBounds = element.Current.BoundingRectangle;
                    if (!automationBounds.IsEmpty && automationBounds.Width > 0 && automationBounds.Height > 0) {
                        bounds = automationBounds;
                        return true;
                    }
                }
            } catch { }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(window, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) {
                return false;
            }
            bounds = new System.Windows.Rect(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top);
            return true;
        }

        private void AddSkipped()
        {
            if (index < steps.Count) resultList.Items.Add("已跳过  " + steps[index].Label);
        }

        private void MoveNext()
        {
            index++;
            if (index >= steps.Count) {
                progressLabel.Text = "校准完成";
                actionLabel.Text = "点击“保存并结束”写入配置";
                coordinateLabel.Text = "以后可以随时重新校准单个位置。";
                return;
            }
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            if (index >= steps.Count) return;
            progressLabel.Text = String.Format("第 {0} / {1} 项", index + 1, steps.Count);
            actionLabel.Text = steps[index].Label;
            coordinateLabel.Text = "切换到剪映，鼠标悬停在此控件中央并按 F8";
        }

        private void SaveAndClose()
        {
            try {
                if (firstCaptionOnly) {
                    PointF probe;
                    PointF left;
                    PointF oneFrame;
                    if (!pendingCoordinates.TryGetValue("FirstCaptionAtZeroProbe", out probe) ||
                        !pendingCoordinates.TryGetValue("FirstCaptionLeftEdge", out left) ||
                        !pendingCoordinates.TryGetValue("FirstCaptionOneFrameRight", out oneFrame)) {
                        MessageBox.Show(this, "三项首帧定位必须全部重新记录，当前不会保存。", "首帧校准", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!(probe.X > left.X)) {
                        MessageBox.Show(this, String.Format("探针 X={0:0.000000} 必须大于左边缘 X={1:0.000000}，当前不会保存。", probe.X, left.X), "首帧校准", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!(oneFrame.X > left.X)) {
                        MessageBox.Show(this, String.Format("右一帧 X={0:0.000000} 必须大于左边缘 X={1:0.000000}，当前不会保存。", oneFrame.X, left.X), "首帧校准", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                foreach (KeyValuePair<string, PointF> pair in pendingCoordinates) {
                    LocatorRule rule = config.GetRule(pair.Key);
                    rule.RelativeX = pair.Value.X;
                    rule.RelativeY = pair.Value.Y;
                    config.Locators[pair.Key] = rule;
                }
                config.Save(configPath);
                DialogResult = DialogResult.OK;
                Close();
            } catch (Exception ex) {
                MessageBox.Show(this, "保存配置失败：" + ex.Message, "校准", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private Button MakeButton(string text, int x)
        {
            return new Button {
                Text = text, Location = new Point(x, 420), Size = new Size(108, 36),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, FlatStyle = FlatStyle.System
            };
        }

        private sealed class CalibrationStep
        {
            public readonly string Key;
            public readonly string Label;
            public CalibrationStep(string key, string label) { Key = key; Label = label; }
        }
    }
}
