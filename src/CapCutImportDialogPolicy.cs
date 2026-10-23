using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    public static class MediaImportFacts
    {
        public static string CaptionSignature(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }
                .DeserializeObject(json) as IDictionary<string, object>;
            object tracks;
            if (root == null || !root.TryGetValue("tracks", out tracks)) return "";
            var ids = new List<string>();
            foreach (object raw in tracks as IEnumerable ?? new object[0]) {
                var track = raw as IDictionary<string, object>;
                object type, segments;
                if (track == null || !track.TryGetValue("type", out type) ||
                    !String.Equals(Convert.ToString(type), "text", StringComparison.OrdinalIgnoreCase) ||
                    !track.TryGetValue("segments", out segments)) continue;
                foreach (object segmentRaw in segments as IEnumerable ?? new object[0]) {
                    var segment = segmentRaw as IDictionary<string, object>;
                    object id, rangeRaw, material;
                    if (segment == null || !segment.TryGetValue("id", out id) ||
                        !segment.TryGetValue("material_id", out material) ||
                        !segment.TryGetValue("target_timerange", out rangeRaw)) continue;
                    var range = rangeRaw as IDictionary<string, object>;
                    object duration, start;
                    if (range == null || !range.TryGetValue("duration", out duration) ||
                        !range.TryGetValue("start", out start) || Convert.ToInt64(duration) <= 0) continue;
                    ids.Add(Convert.ToString(id) + ":" + Convert.ToString(material) + ":" + start + ":" + duration);
                }
            }
            ids.Sort(StringComparer.Ordinal);
            return String.Join("|", ids.ToArray());
        }

        // CapCut writes the media bin separately from timeline segments. Only
        // this exact material record proves import; a painted thumbnail does not.
        public static string FindMaterialId(string metadataJson, string expectedPath)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }
                .DeserializeObject(metadataJson) as IDictionary<string, object>;
            object groups;
            if (root == null || !root.TryGetValue("draft_materials", out groups)) return "";
            foreach (object rawGroup in groups as IEnumerable ?? new object[0]) {
                var group = rawGroup as IDictionary<string, object>;
                object values;
                if (group == null || !group.TryGetValue("value", out values)) continue;
                foreach (object raw in values as IEnumerable ?? new object[0]) {
                    var material = raw as IDictionary<string, object>;
                    object path, id;
                    if (material == null || !material.TryGetValue("file_Path", out path) ||
                        !material.TryGetValue("id", out id)) continue;
                    if (SamePath(Convert.ToString(path), expectedPath))
                        return Convert.ToString(id) ?? "";
                }
            }
            return "";
        }

        private static bool SamePath(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right)) return false;
            return String.Equals(Path.GetFullPath(left.Replace('/', '\\')),
                Path.GetFullPath(right.Replace('/', '\\')), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUnresolved(string phase)
        {
            return !String.IsNullOrWhiteSpace(phase) &&
                !String.Equals(phase, "import_waiting", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class MediaDialogObservation
    {
        public long Handle { get; set; }
        public int ProcessId { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        public long OwnerHandle { get; set; }
        public long RootOwnerHandle { get; set; }
        public bool Visible { get; set; }
        public bool Responsive { get; set; }
        public bool Foreground { get; set; }
        public bool NewSinceCommand { get; set; }
        public bool DirectProcessMatch { get; set; }
        public bool OwnedSessionProcess { get; set; }
        public bool OwnerChainMatchesEditor { get; set; }
        public bool NativeDialogClass { get; set; }
        public bool BlockedTitle { get; set; }
        public bool Accepted { get; set; }
        public string RejectionReason { get; set; }
        public long ObservedAfterMilliseconds { get; set; }

        public MediaDialogObservation()
        {
            Title = "";
            ClassName = "";
            RejectionReason = "";
        }
    }

    // Pure, side-effect-free policy used by native dialog discovery and tests.
    // No UI Automation calls belong in this classifier.
    public static class CapCutImportDialogPolicy
    {
        // A Qt onboarding tooltip is not the editor and must never receive
        // Ctrl+I. Dismiss only this positively identified informational hint.
        public static bool CanProbeEditorHint(long editor, long foreground,
            bool owned, bool responsive, string title, string className,
            double popupArea, double editorArea, bool insideEditor)
        {
            return editor != 0 && foreground != 0 && editor != foreground &&
                owned && responsive && insideEditor && popupArea >= 5000 &&
                editorArea > 0 && popupArea < editorArea * 0.25 &&
                String.Equals(title, "CapCut", StringComparison.OrdinalIgnoreCase) &&
                (className ?? "").StartsWith("Qt", StringComparison.Ordinal) &&
                (className ?? "").IndexOf("QWindow", StringComparison.Ordinal) >= 0;
        }

        public static bool CanDismissEditorHint(long editor, long foreground,
            bool owned, bool responsive, string title, string className,
            double popupArea, double editorArea, bool insideEditor, string text)
        {
            if (!CanProbeEditorHint(editor, foreground, owned, responsive,
                    title, className, popupArea, editorArea, insideEditor)) return false;
            string value = String.Join("", (text ?? "").Split(
                (char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            // Windows OCR consistently confuses the small EditPilot logo's
            // lowercase l with uppercase I. The localized body must contain
            // both exact onboarding phrases when the logo is unreadable.
            bool localizedHint = value.Contains("开启") && value.Contains("发起对话");
            bool product = value.Contains("editpilot") || value.Contains("editpiiot") ||
                value.Contains("快捷编辑模式") || localizedHint;
            bool dismiss = value.Contains("关闭") || value.Contains("close");
            bool tutorial = value.Contains("下一步") || value.Contains("next") ||
                localizedHint;
            return product && dismiss && tutorial && !IsBlockedTitle(value);
        }

        public static bool CanDismissHomePromotion(bool owned, bool responsive,
            string title, string className, bool insideHome, double popupArea,
            double homeArea, string text)
        {
            string value = String.Join("", (text ?? "").Split((char[])null,
                StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            return owned && responsive && insideHome && popupArea >= 20000 &&
                homeArea > 0 && popupArea < homeArea * 0.3 &&
                String.Equals(title, "CapCut", StringComparison.OrdinalIgnoreCase) &&
                (className ?? "").StartsWith("SplashDialogV2_QMLTYPE_", StringComparison.Ordinal) &&
                value.Contains("开通会员畅享") && value.Contains("高级功能") &&
                value.Contains("免费试用") && value.Contains("专属素材") &&
                !IsBlockedTitle(value);
        }

        public static bool IsCandidate(string title, string className,
            bool visible, bool responsive, bool ownedByCapCut)
        {
            return IsCandidate(title, className, visible, responsive,
                ownedByCapCut, false, false, false);
        }

        public static bool IsCandidate(string title, string className,
            bool visible, bool responsive, bool ownedSessionProcess,
            bool directProcessMatch, bool ownerChainMatchesEditor,
            bool newForegroundWindow)
        {
            if (!visible || !responsive) return false;
            string t = title ?? "";
            string c = className ?? "";
            if (IsBlockedTitle(t))
                return false;
            bool nativeClass = IsNativeDialogClass(c);
            if (!nativeClass) return false;

            // A native picker may be hosted by a CapCut child/helper process,
            // so direct PID ownership is not the only valid relationship.  An
            // owner/root-owner chain back to the bound editor is stronger than
            // process-name guessing and survives shell helper changes.
            bool related = ownedSessionProcess || directProcessMatch ||
                ownerChainMatchesEditor;
            if (!related) return false;

            bool titleMatches = IsMediaPickerTitle(t);
            // Empty/localized titles are accepted only for the newly-created
            // foreground window with a verified editor ownership relationship.
            // The killable UI worker must still prove the filename control
            // before any path is written.
            return titleMatches || ownerChainMatchesEditor;
        }

        public static bool IsBlockedTitle(string title)
        {
            string t = title ?? "";
            return t.IndexOf("重命名", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("rename", StringComparison.OrdinalIgnoreCase) >= 0 ||
				t.IndexOf("另存为", StringComparison.OrdinalIgnoreCase) >= 0 ||
				t.IndexOf("save as", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("导出", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("错误", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("草稿", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("draft", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsNativeDialogClass(string className)
        {
            string c = className ?? "";
            return String.Equals(c, "#32770", StringComparison.OrdinalIgnoreCase) ||
                c.IndexOf("filedialog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("file picker", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsMediaPickerTitle(string title)
        {
            string t = title ?? "";
            return t.IndexOf("打开", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("选择媒体", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("select media", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("media", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
