using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    // Applies the visual profile captured from the verified ZY Modern / size 10
    // / first-row preset-11 laboratory draft.  It intentionally touches only
    // subtitle visual fields and never caption identity, text, timing, tracks,
    // media paths, or draft metadata.
    public sealed class DirectDraftSubtitleStylePatcher
    {
        private const string FontTitle = "ZY Modern";
        private const double FontSize = 10.0;
        private const string FontFileName = "ZY-Modern-2.ttf";
        private readonly string fontPath;
        private readonly double? captionTargetY;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer {
            MaxJsonLength = Int32.MaxValue,
            RecursionLimit = 256
        };

        public DirectDraftSubtitleStylePatcher(
            string configuredFontPath = null,
            double? captionTargetY = null)
        {
            fontPath = ResolveFontPath(configuredFontPath);
            this.captionTargetY = captionTargetY;
        }

        public static string ResolveFontPath(string configuredFontPath)
        {
            var candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(configuredFontPath)) {
                string explicitPath;
                try {
                    explicitPath = Path.GetFullPath(
                        Environment.ExpandEnvironmentVariables(configuredFontPath.Trim()));
                } catch (Exception ex) {
                    throw new FileNotFoundException("CompanySubtitleFontPath 不是有效路径: " + configuredFontPath, ex);
                }
                if (!File.Exists(explicitPath))
                    throw new FileNotFoundException("CompanySubtitleFontPath 指向的字体文件不存在: " + explicitPath);
                return explicitPath.Replace('\\', '/');
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrWhiteSpace(localAppData))
                candidates.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Fonts", FontFileName));

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!String.IsNullOrWhiteSpace(windows))
                candidates.Add(Path.Combine(windows, "Fonts", FontFileName));

            string resolved = candidates
                .Select(value => {
                    try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)); }
                    catch { return null; }
                })
                .FirstOrDefault(value => !String.IsNullOrWhiteSpace(value) && File.Exists(value));
            if (String.IsNullOrWhiteSpace(resolved))
                throw new FileNotFoundException(
                    "未找到 ZY Modern 字体文件。请在本机私有配置中设置 CompanySubtitleFontPath，或安装 " +
                    FontFileName + " 到当前用户/Windows字体目录。");
            return resolved.Replace('\\', '/');
        }

        public DirectDraftStyleResult Apply(string draftDirectory, AppLogger logger)
        {
            return ApplyWithFixedEngine(draftDirectory, logger);
        }

        // Kept temporarily for rollback diagnostics. Runtime calls the fixed engine
        // through ApplyWithFixedEngine and never falls back to this implementation.
        private DirectDraftStyleResult ApplyLegacy(string draftDirectory, AppLogger logger)
        {
            if (String.IsNullOrWhiteSpace(draftDirectory) || !Directory.Exists(draftDirectory))
                throw new DirectoryNotFoundException("唯一草稿目录不存在，已停止字幕样式写入: " + draftDirectory);

            string[] targets = ResolveTargets(draftDirectory);
            if (targets.Length == 0)
                throw new InvalidDataException("唯一草稿中没有可写入的 draft_content/template-2 文件，已停止字幕样式写入");

            var before = ReadAndFingerprint(draftDirectory, targets);
            if (before.Primary.CaptionCount <= 0)
                throw new InvalidDataException("唯一草稿中没有真实字幕，已停止字幕样式写入");

            string backupRoot = CreateBackup(draftDirectory, targets);
            try {
                foreach (string target in targets) {
                    object document = Deserialize(File.ReadAllText(target, Encoding.UTF8));
                    int changed = ApplyToDocument(document);
                    if (changed != before.ByPath[target].CaptionCount)
                        throw new InvalidDataException("字幕样式写入数量异常: " + Path.GetFileName(target));
                    AtomicWrite(target, serializer.Serialize(document));
                }

                var after = ReadAndFingerprint(draftDirectory, targets);
                VerifyIdentityAndStyle(before, after);
                logger.Info(String.Format(CultureInfo.InvariantCulture,
                    "草稿直改字幕样式已验证: 草稿={0}, 字幕={1}, 字体={2}, 字号={3}, 文件={4}",
                    Path.GetFileName(draftDirectory), after.Primary.CaptionCount, FontTitle, FontSize, targets.Length));
                return new DirectDraftStyleResult {
                    DraftDirectory = draftDirectory,
                    BackupDirectory = backupRoot,
                    CaptionCount = after.Primary.CaptionCount,
                    PatchedFiles = targets
                };
            } catch {
                RestoreBackup(backupRoot, draftDirectory, targets);
                throw;
            }
        }

        private DirectDraftStyleResult ApplyWithFixedEngine(
            string draftDirectory,
            AppLogger logger)
        {
            if (String.IsNullOrWhiteSpace(draftDirectory) ||
                !Directory.Exists(draftDirectory))
                throw new DirectoryNotFoundException(
                    "唯一草稿目录不存在，已停止字幕样式写入: " + draftDirectory);

            string[] targets = ResolveTargets(draftDirectory);
            if (targets.Length == 0)
                throw new InvalidDataException(
                    "唯一草稿中没有可写入的 draft_content/template-2 文件。");

            DraftCollectionFingerprint before =
                ReadAndFingerprint(draftDirectory, targets);
            if (before.Primary.CaptionCount <= 0)
                throw new InvalidDataException(
                    "唯一草稿中没有真实主字幕，已停止样式写入。");

            string backupRoot = CreateBackup(draftDirectory, targets);
            string stageRoot = CreateEngineStage(draftDirectory, targets);
            try {
                var engine = new CapCutCliDraftStyleEngine();
                CapCutCliDraftStyleEngineResult engineResult =
                    engine.Apply(stageRoot, fontPath, logger, captionTargetY);
                if (engineResult.CaptionCount != before.Primary.CaptionCount)
                    throw new InvalidDataException(
                        "固定草稿引擎处理的字幕数量与锁定草稿不一致，已停止写回。");

                DraftCollectionFingerprint staged =
                    ReadStagedAndFingerprint(
                        draftDirectory,
                        stageRoot,
                        targets);
                try {
                    VerifyIdentityAndStyle(before, staged);
                } catch (Exception ex) {
                    throw new InvalidDataException(
                        "暂存草稿未通过受保护数据校验。", ex);
                }
                EnsureLiveFilesUnchanged(before);
                CommitStagedFiles(draftDirectory, stageRoot, targets);

                DraftCollectionFingerprint after =
                    ReadAndFingerprint(draftDirectory, targets);
                try {
                    VerifyIdentityAndStyle(before, after);
                } catch (Exception ex) {
                    throw new InvalidDataException(
                        "原子写回后的草稿未通过受保护数据校验。", ex);
                }
                logger.Info(String.Format(
                    CultureInfo.InvariantCulture,
                    "固定引擎字幕样式已验证: 草稿={0}, 字幕={1}, 字体={2}, 字号={3}, 文件={4}",
                    Path.GetFileName(draftDirectory),
                    after.Primary.CaptionCount,
                    FontTitle,
                    FontSize,
                    targets.Length));
                return new DirectDraftStyleResult {
                    DraftDirectory = draftDirectory,
                    BackupDirectory = backupRoot,
                    CaptionCount = after.Primary.CaptionCount,
                    PatchedFiles = targets
                };
            } catch {
                RestoreBackup(backupRoot, draftDirectory, targets);
                throw;
            } finally {
                DeleteEngineStage(stageRoot);
            }
        }

        public void VerifyPersisted(string draftDirectory, string[] expectedFiles)
        {
            string[] targets = ResolveTargets(draftDirectory);
            if (expectedFiles == null || expectedFiles.Length == 0 ||
                !SetEquals(targets, expectedFiles))
                throw new InvalidDataException("草稿样式验证时文件集合发生变化，已停止导出");
            var current = ReadAndFingerprint(draftDirectory, targets);
            foreach (DraftDocumentFingerprint item in current.ByPath.Values) VerifyStyle(item);
        }

        public void Restore(DirectDraftStyleResult result)
        {
            if (result == null || String.IsNullOrWhiteSpace(result.BackupDirectory) || result.PatchedFiles == null) return;
            RestoreBackup(result.BackupDirectory, result.DraftDirectory, result.PatchedFiles);
        }

        private string[] ResolveTargets(string draftDirectory)
        {
            var paths = new List<string>();
            string root = Path.Combine(draftDirectory, "draft_content.json");
            string rootTemplate = Path.Combine(draftDirectory, "template-2.tmp");
            if (File.Exists(root)) paths.Add(root);
            if (File.Exists(rootTemplate)) paths.Add(rootTemplate);
            string timelines = Path.Combine(draftDirectory, "Timelines");
            if (Directory.Exists(timelines)) {
                paths.AddRange(Directory.GetFiles(timelines, "draft_content.json", SearchOption.AllDirectories));
                paths.AddRange(Directory.GetFiles(timelines, "template-2.tmp", SearchOption.AllDirectories));
            }
            // CapCut 9.2 can leave a template-2.tmp beside the active timeline
            // that is a cache/cover mirror rather than a subtitle-bearing draft
            // document.  It is not safe to pass that file to the style engine:
            // it has no text track and would make a valid caption draft look
            // like it has no main subtitles.  Keep only files that actually
            // reference at least one subtitle material from a text track.  The
            // file remains in the draft untouched and is therefore still
            // preserved for CapCut's own bookkeeping.
            return paths.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(IsSubtitleStyleTarget)
                .OrderBy(value => value)
                .ToArray();
        }

        private bool IsSubtitleStyleTarget(string path)
        {
            try {
                object document = Deserialize(File.ReadAllText(path, Encoding.UTF8));
                return GetMainSubtitleTexts(document).Any();
            } catch {
                // A non-caption cache mirror, malformed temporary file, or an
                // unsupported sidecar is not a writable style target.  Do not
                // let it mask the valid root/active-timeline documents.
                return false;
            }
        }

        private DraftCollectionFingerprint ReadAndFingerprint(string draftDirectory, IEnumerable<string> paths)
        {
            var result = new DraftCollectionFingerprint();
            foreach (string path in paths) {
                string json = File.ReadAllText(path, Encoding.UTF8);
                DraftDocumentFingerprint fingerprint =
                    Fingerprint(path, Deserialize(json));
                fingerprint.FullFileHash = Hash(json);
                result.ByPath[path] = fingerprint;
            }
            string root = Path.Combine(Path.GetFullPath(draftDirectory), "draft_content.json");
            if (!result.ByPath.ContainsKey(root)) root = null;
            result.Primary = !String.IsNullOrWhiteSpace(root) ? result.ByPath[root] : result.ByPath.Values.First();
            return result;
        }

        private DraftCollectionFingerprint ReadStagedAndFingerprint(
            string draftDirectory,
            string stageRoot,
            IEnumerable<string> livePaths)
        {
            var result = new DraftCollectionFingerprint();
            foreach (string livePath in livePaths) {
                string stagedPath = Path.Combine(
                    stageRoot,
                    RelativeTo(draftDirectory, livePath));
                if (!File.Exists(stagedPath))
                    throw new FileNotFoundException(
                        "固定草稿引擎没有返回完整的草稿副本。",
                        stagedPath);
                string json = File.ReadAllText(stagedPath, Encoding.UTF8);
                DraftDocumentFingerprint fingerprint =
                    Fingerprint(livePath, Deserialize(json));
                fingerprint.FullFileHash = Hash(json);
                result.ByPath[livePath] = fingerprint;
            }
            string root = Path.Combine(
                Path.GetFullPath(draftDirectory),
                "draft_content.json");
            result.Primary = result.ByPath.ContainsKey(root)
                ? result.ByPath[root]
                : result.ByPath.Values.First();
            return result;
        }

        private DraftDocumentFingerprint Fingerprint(string path, object document)
        {
            object[] texts = GetMainSubtitleTexts(document).ToArray();
            var captionIds = new HashSet<string>(texts
                .Select(raw => Convert.ToString(Get(raw as Dictionary<string, object>, "id")))
                .Where(value => !String.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            var identities = new List<string>();
            foreach (object raw in texts) {
                var text = raw as Dictionary<string, object>;
                identities.Add(Convert.ToString(Get(text, "id")) + "|" +
                    Convert.ToString(Get(text, "recognize_text")) + "|" +
                    EmbeddedText(Get(text, "content")) + "|" +
                    Canonical(Get(text, "words")) + "|" + Canonical(Get(text, "current_words")));
            }
            return new DraftDocumentFingerprint {
                Path = path,
                CaptionCount = texts.Length,
                CaptionIdentityHash = Hash(String.Join("\n", identities)),
                TimingAndTrackHash = Hash(Canonical(ProtectedTracksForHash(document, captionIds)) + "\n" +
                    Canonical(Get(document as Dictionary<string, object>, "segments"))),
                ProtectedMediaHash = ProtectedMediaHash(document),
                Texts = texts,
                CaptionPositionYs = CaptionPositionYs(document, captionIds)
            };
        }

        private object ProtectedTracksForHash(object document, HashSet<string> captionIds)
        {
            var root = document as Dictionary<string, object>;
            object tracks = Get(root, "tracks");
            object clone = Deserialize(serializer.Serialize(tracks));
            IEnumerable rawTracks = clone as IEnumerable;
            if (rawTracks == null || clone is string) return clone;
            foreach (object rawTrack in rawTracks) {
                var track = rawTrack as Dictionary<string, object>;
                IEnumerable segments = Get(track, "segments") as IEnumerable;
                if (segments == null) continue;
                foreach (object rawSegment in segments) {
                    var segment = rawSegment as Dictionary<string, object>;
                    if (segment == null || !captionIds.Contains(
                        Convert.ToString(Get(segment, "material_id")))) continue;
                    var clip = Get(segment, "clip") as Dictionary<string, object>;
                    var transform = Get(clip, "transform") as Dictionary<string, object>;
                    if (transform != null) {
                        transform.Remove("y");
                        if (transform.Count == 0 && clip != null) clip.Remove("transform");
                    }
                    if (clip != null && clip.Count == 0) segment.Remove("clip");
                }
            }
            return clone;
        }

        private double[] CaptionPositionYs(object document, HashSet<string> captionIds)
        {
            var values = new List<double>();
            var root = document as Dictionary<string, object>;
            IEnumerable tracks = Get(root, "tracks") as IEnumerable;
            if (tracks == null) return values.ToArray();
            foreach (object rawTrack in tracks) {
                var track = rawTrack as Dictionary<string, object>;
                IEnumerable segments = Get(track, "segments") as IEnumerable;
                if (segments == null) continue;
                foreach (object rawSegment in segments) {
                    var segment = rawSegment as Dictionary<string, object>;
                    if (segment == null || !captionIds.Contains(
                        Convert.ToString(Get(segment, "material_id")))) continue;
                    var clip = Get(segment, "clip") as Dictionary<string, object>;
                    var transform = Get(clip, "transform") as Dictionary<string, object>;
                    values.Add(ToDouble(Get(transform, "y")));
                }
            }
            return values.ToArray();
        }

        private int ApplyToDocument(object document)
        {
            int changed = 0;
            foreach (object raw in GetMainSubtitleTexts(document)) {
                var text = raw as Dictionary<string, object>;
                if (text == null) continue;
                ApplyVisualFields(text);
                PatchEmbeddedContent(text, "content");
                changed++;
            }
            return changed;
        }

        private void ApplyVisualFields(Dictionary<string, object> text)
        {
            text["font_title"] = FontTitle;
            text["font_size"] = FontSize;
            text["font_path"] = fontPath;
            text["text_color"] = "#000000";
            text["check_flag"] = 23;
            text["background_color"] = "#ffffff";
            text["background_alpha"] = 0.5;
            text["background_style"] = 1;
            text["background_round_radius"] = 0.4000000059604645;
            text["background_width"] = 0.2800000011920929;
            text["background_height"] = 0.2800000011920929;
        }

        private void PatchEmbeddedContent(Dictionary<string, object> text, string key)
        {
            string raw = Convert.ToString(Get(text, key));
            if (String.IsNullOrWhiteSpace(raw))
                throw new InvalidDataException("字幕缺少 " + key + "，已停止草稿样式写入");
            var embedded = Deserialize(raw) as Dictionary<string, object>;
            if (embedded == null) throw new InvalidDataException("字幕 " + key + " 不是有效JSON");
            object[] styles = Get(embedded, "styles") as object[];
            if (styles == null || styles.Length == 0) throw new InvalidDataException("字幕 " + key + " 缺少styles");
            foreach (object rawStyle in styles) {
                var style = rawStyle as Dictionary<string, object>;
                if (style == null) throw new InvalidDataException("字幕样式对象无效");
                style["size"] = FontSize;
                style["useLetterColor"] = true;
                var font = Get(style, "font") as Dictionary<string, object> ?? new Dictionary<string, object>();
                font["path"] = fontPath;
                style["font"] = font;
                var fill = new Dictionary<string, object> {
                    { "alpha", 1.0 },
                    { "content", new Dictionary<string, object> {
                        { "render_type", "solid" },
                        { "solid", new Dictionary<string, object> { { "alpha", 1.0 }, { "color", new object[] { 0.0, 0.0, 0.0 } } } }
                    } }
                };
                style["fill"] = fill;
            }
            text[key] = serializer.Serialize(embedded);
        }

        private void VerifyIdentityAndStyle(DraftCollectionFingerprint before, DraftCollectionFingerprint after)
        {
            if (!SetEquals(before.ByPath.Keys, after.ByPath.Keys))
                throw new InvalidDataException("草稿样式写入后文件集合发生变化");
            foreach (string path in before.ByPath.Keys) {
                DraftDocumentFingerprint oldValue = before.ByPath[path];
                DraftDocumentFingerprint newValue = after.ByPath[path];
                bool captionCountChanged =
                    oldValue.CaptionCount != newValue.CaptionCount;
                bool captionIdentityChanged = !String.Equals(
                    oldValue.CaptionIdentityHash,
                    newValue.CaptionIdentityHash,
                    StringComparison.Ordinal);
                bool timingOrTrackChanged = !String.Equals(
                    oldValue.TimingAndTrackHash,
                    newValue.TimingAndTrackHash,
                    StringComparison.Ordinal);
                bool mediaChanged = !String.Equals(
                    oldValue.ProtectedMediaHash,
                    newValue.ProtectedMediaHash,
                    StringComparison.Ordinal);
                if (captionCountChanged || captionIdentityChanged ||
                    timingOrTrackChanged || mediaChanged)
                    throw new InvalidDataException(String.Format(
                        CultureInfo.InvariantCulture,
                        "草稿受保护数据发生变化，已恢复备份: {0}; count={1}, identity={2}, timingOrTrack={3}, media={4}",
                        Path.GetFileName(path),
                        captionCountChanged,
                        captionIdentityChanged,
                        timingOrTrackChanged,
                        mediaChanged));
                if (oldValue.CaptionCount != newValue.CaptionCount ||
                    !String.Equals(oldValue.CaptionIdentityHash, newValue.CaptionIdentityHash, StringComparison.Ordinal) ||
                    !String.Equals(oldValue.TimingAndTrackHash, newValue.TimingAndTrackHash, StringComparison.Ordinal) ||
                    !String.Equals(oldValue.ProtectedMediaHash, newValue.ProtectedMediaHash, StringComparison.Ordinal))
                    throw new InvalidDataException("草稿直改试图改变字幕文本、数量、时间、轨道、媒体或静音状态，已恢复备份: " + Path.GetFileName(path));
                VerifyStyle(newValue);
            }
        }

        private void VerifyStyle(DraftDocumentFingerprint document)
        {
            if (document.CaptionCount <= 0) throw new InvalidDataException("样式验证时没有字幕");
            foreach (object raw in document.Texts) {
                var text = raw as Dictionary<string, object>;
                if (Math.Abs(ToDouble(Get(text, "font_size")) - FontSize) > 0.001 ||
                    !DraftPathEquals(Convert.ToString(Get(text, "font_path")), fontPath) ||
                    !String.Equals(Convert.ToString(Get(text, "text_color")), "#000000", StringComparison.OrdinalIgnoreCase) ||
                    Convert.ToInt32(Get(text, "check_flag"), CultureInfo.InvariantCulture) != 23 ||
                    !String.Equals(Convert.ToString(Get(text, "background_color")), "#ffffff", StringComparison.OrdinalIgnoreCase) ||
                    Math.Abs(ToDouble(Get(text, "background_alpha")) - 0.5) > 0.000001 ||
                    Convert.ToInt32(Get(text, "background_style"), CultureInfo.InvariantCulture) != 1 ||
                    Math.Abs(ToDouble(Get(text, "background_round_radius")) - 0.4000000059604645) > 0.000001 ||
                    Math.Abs(ToDouble(Get(text, "background_width")) - 0.2800000011920929) > 0.000001 ||
                    Math.Abs(ToDouble(Get(text, "background_height")) - 0.2800000011920929) > 0.000001)
                    throw new InvalidDataException("字幕样式验证失败: " + Path.GetFileName(document.Path));
                var embedded = Deserialize(Convert.ToString(Get(text, "content"))) as Dictionary<string, object>;
                object[] styles = embedded == null ? null : Get(embedded, "styles") as object[];
                if (styles == null || styles.Length == 0) throw new InvalidDataException("字幕样式验证缺少内容样式");
                foreach (object rawStyle in styles) {
                    var style = rawStyle as Dictionary<string, object>;
                    var font = style == null ? null : Get(style, "font") as Dictionary<string, object>;
                    if (style == null || Math.Abs(ToDouble(Get(style, "size")) - FontSize) > 0.001 ||
                        !ToBoolean(Get(style, "useLetterColor")) ||
                        font == null || !DraftPathEquals(Convert.ToString(Get(font, "path")), fontPath) ||
                        !HasVerifiedBlackFill(style))
                        throw new InvalidDataException("字幕内嵌样式验证失败");
                }
            }
            if (captionTargetY.HasValue) {
                if (document.CaptionPositionYs == null ||
                    document.CaptionPositionYs.Length != document.CaptionCount)
                    throw new InvalidDataException("字幕位置验证缺少字幕轨道变换数据");
                foreach (double actualY in document.CaptionPositionYs) {
                    if (Math.Abs(actualY - captionTargetY.Value) > 0.001)
                        throw new InvalidDataException("字幕未稳定写入原字幕带位置: " +
                            Path.GetFileName(document.Path));
                }
            }
        }

        private string CreateBackup(string draftDirectory, IEnumerable<string> targets)
        {
            string key = Path.GetFileName(draftDirectory) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            string root = Path.Combine(AppPaths.DataDirectory, "draft-style-backups", key);
            foreach (string source in targets) {
                string relative = RelativeTo(draftDirectory, source);
                string destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, false);
            }
            return root;
        }

        private static string CreateEngineStage(
            string draftDirectory,
            IEnumerable<string> targets)
        {
            string root = Path.Combine(
                AppPaths.DataDirectory,
                "draft-style-engine-staging",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (string source in targets) {
                string destination = Path.Combine(
                    root,
                    RelativeTo(draftDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, false);
            }
            return root;
        }

        private static void EnsureLiveFilesUnchanged(
            DraftCollectionFingerprint before)
        {
            foreach (DraftDocumentFingerprint original in before.ByPath.Values) {
                string current = File.ReadAllText(
                    original.Path,
                    Encoding.UTF8);
                if (!String.Equals(
                    original.FullFileHash,
                    Hash(current),
                    StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "草稿在样式处理期间被其他程序修改，已停止写回: " +
                        Path.GetFileName(original.Path));
            }
        }

        private static void CommitStagedFiles(
            string draftDirectory,
            string stageRoot,
            IEnumerable<string> targets)
        {
            foreach (string target in targets) {
                string staged = Path.Combine(
                    stageRoot,
                    RelativeTo(draftDirectory, target));
                AtomicWrite(
                    target,
                    File.ReadAllText(staged, Encoding.UTF8));
            }
        }

        private static void DeleteEngineStage(string stageRoot)
        {
            if (String.IsNullOrWhiteSpace(stageRoot)) return;
            try {
                string full = Path.GetFullPath(stageRoot);
                string allowed = Path.GetFullPath(Path.Combine(
                    AppPaths.DataDirectory,
                    "draft-style-engine-staging"));
                if (full.StartsWith(
                    AppendSlash(allowed),
                    StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(full))
                    Directory.Delete(full, true);
            } catch { }
        }

        private static void RestoreBackup(string backupRoot, string draftDirectory, IEnumerable<string> targets)
        {
            foreach (string target in targets) {
                string relative = RelativeTo(draftDirectory, target);
                string backup = Path.Combine(backupRoot, relative);
                if (!File.Exists(backup)) continue;
                string temp = target + ".restore-" + Guid.NewGuid().ToString("N");
                File.Copy(backup, temp, true);
                ReplaceAtomically(temp, target);
            }
        }

        private static string RelativeTo(string draftDirectory, string source)
        {
            Uri baseUri = new Uri(AppendSlash(Path.GetFullPath(draftDirectory)));
            Uri fileUri = new Uri(Path.GetFullPath(source));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendSlash(string path) { return path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\"; }

        private static void AtomicWrite(string path, string content)
        {
            string temp = path + ".codex-new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            ReplaceAtomically(temp, path);
        }

        private static void ReplaceAtomically(string temp, string path)
        {
            try { File.Replace(temp, path, null); }
            catch (PlatformNotSupportedException) { File.Move(temp, path); }
            catch (FileNotFoundException) { File.Move(temp, path); }
        }

        private object Deserialize(string json)
        {
            try { return serializer.DeserializeObject(json); }
            catch (Exception ex) { throw new InvalidDataException("草稿JSON无法解析", ex); }
        }

        private static IEnumerable<object> GetMainSubtitleTexts(object document)
        {
            var root = document as Dictionary<string, object>;
            var materials = root == null ? null : Get(root, "materials") as Dictionary<string, object>;
            object[] texts = materials == null ? null : Get(materials, "texts") as object[];
            var byId = (texts ?? new object[0])
                .Select(raw => raw as Dictionary<string, object>)
                .Where(text => text != null)
                .ToDictionary(text => Convert.ToString(Get(text, "id")), text => (object)text,
                    StringComparer.OrdinalIgnoreCase);

            object[] tracks = Get(root, "tracks") as object[];
            var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object rawTrack in tracks ?? new object[0]) {
                var track = rawTrack as Dictionary<string, object>;
                if (track == null || !String.Equals(Convert.ToString(Get(track, "type")), "text",
                    StringComparison.OrdinalIgnoreCase)) continue;
                object[] segments = Get(track, "segments") as object[];
                foreach (object rawSegment in segments ?? new object[0]) {
                    var segment = rawSegment as Dictionary<string, object>;
                    string materialId = Convert.ToString(Get(segment, "material_id"));
                    object material;
                    if (String.IsNullOrWhiteSpace(materialId) || !byId.TryGetValue(materialId, out material)) continue;
                    var text = material as Dictionary<string, object>;
                    if (text != null && String.Equals(Convert.ToString(Get(text, "type")), "subtitle",
                        StringComparison.OrdinalIgnoreCase))
                        referencedIds.Add(materialId);
                }
            }
            if (referencedIds.Count == 0)
                throw new InvalidDataException("草稿没有字幕轨实际引用的主字幕，已停止样式写入");
            return (texts ?? new object[0]).Where(raw => {
                var text = raw as Dictionary<string, object>;
                return text != null && referencedIds.Contains(Convert.ToString(Get(text, "id")));
            });
        }

        private static string ProtectedMediaHash(object document)
        {
            var root = document as Dictionary<string, object>;
            var materials = root == null ? null : Get(root, "materials") as Dictionary<string, object>;
            var config = root == null ? null : Get(root, "config") as Dictionary<string, object>;
            return Hash(
                Canonical(materials == null ? null : Get(materials, "videos")) + "\n" +
                Canonical(materials == null ? null : Get(materials, "audios")) + "\n" +
                Canonical(materials == null ? null : Get(materials, "sound_channel_mappings")) + "\n" +
                Canonical(config == null ? null : Get(config, "video_mute")));
        }

        private static bool HasVerifiedBlackFill(Dictionary<string, object> style)
        {
            var fill = Get(style, "fill") as Dictionary<string, object>;
            var content = fill == null ? null : Get(fill, "content") as Dictionary<string, object>;
            var solid = content == null ? null : Get(content, "solid") as Dictionary<string, object>;
            object[] color = solid == null ? null : Get(solid, "color") as object[];
            return fill != null && Math.Abs(ToDouble(Get(fill, "alpha")) - 1.0) <= 0.000001 &&
                String.Equals(Convert.ToString(Get(content, "render_type")), "solid", StringComparison.OrdinalIgnoreCase) &&
                solid != null && Math.Abs(ToDouble(Get(solid, "alpha")) - 1.0) <= 0.000001 &&
                color != null && color.Length == 3 && color.All(value => Math.Abs(ToDouble(value)) <= 0.000001);
        }

        private static bool DraftPathEquals(string left, string right)
        {
            return String.Equals((left ?? "").Replace('\\', '/'), (right ?? "").Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static object Get(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value : null;
        }

        private string EmbeddedText(object raw)
        {
            var document = Deserialize(Convert.ToString(raw)) as Dictionary<string, object>;
            return document == null ? "" : Convert.ToString(Get(document, "text"));
        }

        private static string Canonical(object value)
        {
            if (value == null) return "null";
            var dict = value as Dictionary<string, object>;
            if (dict != null) return "{" + String.Join(",", dict.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => Escape(item.Key) + ":" + Canonical(item.Value))) + "}";
            var array = value as object[];
            if (array != null) return "[" + String.Join(",", array.Select(Canonical)) + "]";
            var enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
                return "[" + String.Join(",", enumerable.Cast<object>().Select(Canonical)) + "]";
            if (IsNumeric(value)) {
                decimal normalized;
                if (Decimal.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out normalized))
                    return normalized.ToString("G29", CultureInfo.InvariantCulture);
            }
            return Escape(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static bool IsNumeric(object value)
        {
            if (value == null) return false;
            switch (Type.GetTypeCode(value.GetType())) {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    return true;
                default:
                    return false;
            }
        }

        private static string Escape(string value) { return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", ""); }
        private static double ToDouble(object value) { double parsed; return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0; }
        private static bool ToBoolean(object value) { bool parsed; return Boolean.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) && parsed; }
        private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right) { return new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right); }

        private sealed class DraftCollectionFingerprint
        {
            public readonly Dictionary<string, DraftDocumentFingerprint> ByPath = new Dictionary<string, DraftDocumentFingerprint>(StringComparer.OrdinalIgnoreCase);
            public DraftDocumentFingerprint Primary;
        }

        private sealed class DraftDocumentFingerprint
        {
            public string Path;
            public string FullFileHash;
            public int CaptionCount;
            public string CaptionIdentityHash;
            public string TimingAndTrackHash;
            public string ProtectedMediaHash;
            public object[] Texts;
            public double[] CaptionPositionYs;
        }
    }

    public sealed class DirectDraftStyleResult
    {
        public string DraftDirectory { get; set; }
        public string BackupDirectory { get; set; }
        public int CaptionCount { get; set; }
        public string[] PatchedFiles { get; set; }
    }
}
