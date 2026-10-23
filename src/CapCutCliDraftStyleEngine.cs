using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CapCutBatchClient
{
    internal sealed class CapCutCliDraftStyleEngine
    {
        private static object[] SupportedProfiles()
        {
            return new object[] {
                new Dictionary<string, object> {
                    { "appVersion", "9.1.0" },
                    { "schema", 360000 },
                    { "newVersion", "" },
                    { "requireExactNewVersion", false }
                },
                new Dictionary<string, object> {
                    { "appVersion", "9.2.0" },
                    { "schema", 360000 },
                    { "newVersion", "181.0.0" },
                    { "requireExactNewVersion", true }
                }
            };
        }

        public static void ValidateRuntime()
        {
            string tools = Path.Combine(AppPaths.Root, "tools");
            string engine = Path.Combine(tools, "draft-style-engine.mjs");
            string preset = Path.Combine(tools, "company-subtitle-0725.preset.json");
            string upstreamPreset = Path.Combine(tools, "capcut-cli-v0.15.0", "dist", "preset.js");
            string upstreamDraft = Path.Combine(tools, "capcut-cli-v0.15.0", "dist", "draft.js");
            if (!File.Exists(engine))
                throw new FileNotFoundException("固定草稿引擎不存在，已停止隔离内测。", engine);
            if (!File.Exists(preset))
                throw new FileNotFoundException("0725 公司字幕预设不存在，已停止隔离内测。", preset);
            if (!File.Exists(upstreamPreset) || !File.Exists(upstreamDraft))
                throw new FileNotFoundException("固定 capcut-cli 运行文件不完整，已停止隔离内测。", tools);
            ResolveNode(tools);
        }

        public CapCutCliDraftStyleEngineResult Apply(
            string stagedDraftDirectory,
            string fontPath,
            AppLogger logger,
            double? captionTargetY = null)
        {
            return Execute("apply", stagedDraftDirectory, fontPath,
                logger, captionTargetY);
        }

        public CapCutCliDraftStyleEngineResult Inspect(
            string draftDirectory,
            AppLogger logger)
        {
            return Execute("inspect", draftDirectory, null, logger, null);
        }

        public void ReconcileSavedCaptionMirrors(string draftDirectory, AppLogger logger)
        {
            Execute("reconcile", draftDirectory, null, logger, null);
        }

        private CapCutCliDraftStyleEngineResult Execute(
            string operation,
            string stagedDraftDirectory,
            string fontPath,
            AppLogger logger,
            double? captionTargetY)
        {
            string tools = Path.Combine(AppPaths.Root, "tools");
            string engine = Path.Combine(tools, "draft-style-engine.mjs");
            string preset = Path.Combine(tools, "company-subtitle-0725.preset.json");
            string node = ResolveNode(tools);
            ValidateRuntime();

            string requestPath = Path.Combine(
                stagedDraftDirectory,
                ".draft-style-request-" + Guid.NewGuid().ToString("N") + ".json");
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var request = new Dictionary<string, object> {
                { "operation", operation },
                { "draftDirectory", Path.GetFullPath(stagedDraftDirectory) },
                { "supportedProfiles", SupportedProfiles() }
            };
            if (String.Equals(operation, "apply", StringComparison.OrdinalIgnoreCase)) {
                request["fontPath"] = Path.GetFullPath(fontPath);
                request["presetPath"] = preset;
            }
            if (String.Equals(operation, "apply", StringComparison.OrdinalIgnoreCase) &&
                captionTargetY.HasValue)
                request["captionTargetY"] = captionTargetY.Value;
            File.WriteAllText(
                requestPath,
                serializer.Serialize(request),
                new UTF8Encoding(false));
            try {
                var start = new ProcessStartInfo {
                    FileName = node,
                    Arguments = Quote(engine) + " " + Quote(requestPath),
                    WorkingDirectory = tools,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (Process process = Process.Start(start)) {
                    if (process == null)
                        throw new InvalidOperationException("无法启动固定草稿引擎。");

                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(120000)) {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException(
                            "固定草稿引擎运行超过 120 秒，已停止写入。");
                    }

                    CapCutCliDraftStyleEngineResult result = null;
                    if (!String.IsNullOrWhiteSpace(stdout)) {
                        try {
                            result = serializer.Deserialize<CapCutCliDraftStyleEngineResult>(stdout);
                        } catch (Exception ex) {
                            throw new InvalidDataException(
                                "固定草稿引擎返回了无法解析的结果。", ex);
                        }
                    }
                    if (result == null || !result.Ok || process.ExitCode != 0) {
                        string reason = result == null ? "" : result.Error;
                        if (String.IsNullOrWhiteSpace(reason)) reason = stderr.Trim();
                        if (String.IsNullOrWhiteSpace(reason)) reason = "未知草稿引擎错误";
                        string code = result == null ? "" : result.ErrorCode;
                        throw new InvalidOperationException(
                            "固定草稿引擎失败: " +
                            (String.IsNullOrWhiteSpace(code) ? "" : code + ": ") +
                            reason);
                    }
                    if (!String.IsNullOrWhiteSpace(stderr))
                        logger.Warn("固定草稿引擎诊断: " + stderr.Trim());
                    logger.Info(String.Format(
                        "固定草稿引擎{4}已完成: engine={0}, capcut-cli={1}, 字幕={2}, 草稿组={3}",
                        result.EngineVersion,
                        result.UpstreamVersion,
                        result.CaptionCount,
                        result.Groups == null ? 0 : result.Groups.Length,
                        operation));
                    return result;
                }
            } finally {
                try {
                    if (File.Exists(requestPath)) File.Delete(requestPath);
                } catch { }
            }
        }

        private static string ResolveNode(string toolsDirectory)
        {
            var candidates = new List<string> {
                Path.Combine(toolsDirectory, "node", "node.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "nodejs",
                    "node.exe")
            };
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(
                path.Split(Path.PathSeparator)
                    .Where(value => !String.IsNullOrWhiteSpace(value))
                    .Select(value => Path.Combine(value.Trim(), "node.exe")));
            string resolved = candidates.FirstOrDefault(File.Exists);
            if (String.IsNullOrWhiteSpace(resolved))
                throw new FileNotFoundException(
                    "固定草稿引擎缺少 Node 运行时。请使用包含 tools\\node\\node.exe 的内测包。");
            return Path.GetFullPath(resolved);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class CapCutCliDraftStyleEngineResult
    {
        public bool Ok { get; set; }
        public string EngineVersion { get; set; }
        public string UpstreamVersion { get; set; }
        public string Error { get; set; }
        public string ErrorCode { get; set; }
        public int CaptionCount { get; set; }
        public CapCutCliDraftStyleGroup[] Groups { get; set; }
    }

    internal sealed class CapCutCliDraftStyleGroup
    {
        public string Group { get; set; }
        public string Canonical { get; set; }
        public int CaptionCount { get; set; }
        public string[] Files { get; set; }
        public string CaptionIdentityHash { get; set; }
        public string TimingAndTrackHash { get; set; }
        public string ProtectedMediaHash { get; set; }
        public string AppVersion { get; set; }
        public int SchemaVersion { get; set; }
        public string NewVersion { get; set; }
    }
}
