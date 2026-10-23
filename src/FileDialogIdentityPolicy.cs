using System;
using System.Collections.Generic;
using System.Linq;

namespace CapCutBatchClient
{
    internal static class FileDialogIdentityPolicy
    {
        // Refocusing is allowed only for the same already-accepted native picker.
        // This never permits typing into an unrelated or replaced window.
        public static bool CanRefocusPicker(bool sameProcessInstance, bool visible,
            bool enabled, string title, string className, bool fieldVerified, bool openVerified)
        {
            return sameProcessInstance && visible && enabled && fieldVerified && openVerified &&
                !new[] { "错误", "error", "重命名", "rename", "另存为", "save as", "导出", "export" }.Any(
                    blocked => (title ?? "").IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0) &&
                String.Equals(className, "#32770", StringComparison.Ordinal) &&
                IsVerifiedMediaDialog(title, className, fieldVerified,
                    new[] { "请选择媒体资源", "选择媒体", "打开", "Open", "Select media" });
        }

        public static bool IsSafeFileNameControl(string automationId, string name)
        {
            string id = automationId ?? "";
            string label = name ?? "";
            return String.Equals(id, "1148", StringComparison.OrdinalIgnoreCase) ||
                id.IndexOf("FileNameControlHost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("File name", StringComparison.OrdinalIgnoreCase) >= 0 ||
                label.IndexOf("文件名", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsVerifiedMediaDialog(
            string title,
            string className,
            bool hasSafeFileNameControl,
            IEnumerable<string> expectedTitles)
        {
            if (!hasSafeFileNameControl) return false;
            string caption = title ?? "";
            bool titleMatches = (expectedTitles ?? Enumerable.Empty<string>()).Any(name =>
                !String.IsNullOrWhiteSpace(name) &&
                caption.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            // #32770 is shared by Open, Rename and error dialogs.  The class is
            // useful diagnostic evidence, never sufficient identity by itself.
            return titleMatches;
        }
    }
}
