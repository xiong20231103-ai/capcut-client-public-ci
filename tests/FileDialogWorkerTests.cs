using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.IO;
using System.Web.Script.Serialization;
using System.Linq;
using CapCutBatchClient;
internal static class FileDialogWorkerTests
{
    private static int Main()
    {
        try {
            var automation = (CapCutAutomation)FormatterServices.GetUninitializedObject(typeof(CapCutAutomation));
            var call = typeof(CapCutAutomation).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == "RunBoundedFileDialogProbe" && m.GetParameters().Length == 4);
            var watch = Stopwatch.StartNew();
            object stalled = call.Invoke(automation, new object[] { IntPtr.Zero, "unused", CancellationToken.None, true });
            string code = (string)stalled.GetType().GetField("ErrorCode").GetValue(stalled);
            if (code != "CAPCUT_FILE_DIALOG_PROBE_TIMEOUT" || watch.Elapsed.TotalSeconds > 3.5)
                throw new Exception("stalled worker exceeded hard timeout: " + code + " / " + watch.Elapsed);
            Thread.Sleep(150);
            foreach (var process in Process.GetProcessesByName("CapCutFileDialogProbe")) {
                using (process) {
                    if (process.MainModule.FileName.StartsWith(AppPaths.Root, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("timed-out probe was not reclaimed");
                }
            }
            object legacy = call.Invoke(automation, new object[] { new IntPtr(1), "unused", CancellationToken.None, true });
            if ((bool)legacy.GetType().GetField("Success").GetValue(legacy))
                throw new Exception("legacy OK text incorrectly accepted");
            object inspect = call.Invoke(automation, new object[] { new IntPtr(2), "unused", CancellationToken.None, false });
            if ((bool)inspect.GetType().GetField("Success").GetValue(inspect))
                throw new Exception("inspection incorrectly accepted as submission");
            Console.WriteLine("PASS worker hard timeout and process reclamation");
            Console.WriteLine("PASS structured output requires actual submission");
            string root = Path.Combine(AppPaths.Root, "empty-draft-fixture");
            string draft = Path.Combine(root, "arbitrary-draft");
            Directory.CreateDirectory(draft);
            var serializer = new JavaScriptSerializer();
            File.WriteAllText(Path.Combine(root, "root_meta_info.json"), serializer.Serialize(new {
                all_draft_store = new[] { new { draft_id="fixture-id", draft_fold_path=draft } }
            }));
            File.WriteAllText(Path.Combine(draft, "draft_meta_info.json"),
                "{\"draft_id\":\"fixture-id\",\"draft_name\":\"arbitrary-draft\"}");
            typeof(CapCutAutomation).GetField("draftRoot", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(automation, root);
            typeof(CapCutAutomation).GetField("draftCreationCheckpoint", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(automation,
                new DraftCreationCheckpoint { DiscoveredDraftId="fixture-id", DiscoveredDraftPath=draft });
            var resolve = typeof(CapCutAutomation).GetMethod("ResolveImportDraftDirectory", BindingFlags.Instance|BindingFlags.NonPublic);
            if (!String.Equals((string)resolve.Invoke(automation, null),Path.GetFullPath(draft),StringComparison.OrdinalIgnoreCase)) throw new Exception("empty draft incorrectly requires source media");
            var identityType=typeof(CapCutAutomation).GetNestedType("DirectDraftIdentity", BindingFlags.NonPublic);
            var identity=Activator.CreateInstance(identityType, true);
            identityType.GetField("DraftId").SetValue(identity,"fixture-id");
            typeof(CapCutAutomation).GetField("directDraftDirectory", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(automation,draft);
            typeof(CapCutAutomation).GetField("directDraftIdentity", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(automation,identity);
            typeof(CapCutAutomation).GetField("draftCreationCheckpoint", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(automation,null);
            if (!String.Equals(Path.GetFullPath((string)resolve.Invoke(automation,null)),Path.GetFullPath(draft),StringComparison.OrdinalIgnoreCase)) throw new Exception("restored media checkpoint incorrectly requires creation baseline");
            Console.WriteLine("PASS empty creation and restored checkpoint resolve independently of media placement");
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            typeof(CapCutAutomation).GetField("recoveringCreatedDraft",flags).SetValue(automation,true);
            typeof(CapCutAutomation).GetField("draftCreationCheckpoint",flags).SetValue(automation,
                new DraftCreationCheckpoint {DiscoveredDraftId="fixture-id",DiscoveredDraftPath=draft});
            string source=Path.Combine(root,"source.mp4");
            typeof(CapCutAutomation).GetField("draftExpectedVideoPath",flags).SetValue(automation,source);
            File.WriteAllText(Path.Combine(draft,"draft_meta_info.json"),serializer.Serialize(new {
                draft_id="fixture-id",draft_name="arbitrary-draft",draft_materials=new[]{new{value=new[]{new{id="media",file_Path=source}}}}
            }));
            File.WriteAllText(Path.Combine(draft,"draft_content.json"),"{\"tracks\":[],\"materials\":{}}");
            var evidence=typeof(CapCutAutomation).GetMethod("HasLockedReopenSourceEvidence",flags);
            if(!(bool)evidence.Invoke(automation,new object[]{draft}))throw new Exception("empty timeline with exact bin media must reopen");
            File.WriteAllText(Path.Combine(draft,"draft_content.json"),"{\"tracks\":[{\"segments\":[{\"id\":\"unknown\"}]}]}");
            if((bool)evidence.Invoke(automation,new object[]{draft}))throw new Exception("creation recovery must not bypass committed segment verification");
            Console.WriteLine("PASS creation recovery requires exact media-bin identity and rejects unverified segments");
            TestResumedLockedDraftCleanup();
            TestEnvironmentCheckTitle();
            TestReadIndexWhileCapCutWrites();
            TestDraftFilesQuiet();
            TestCenterCropForOcr();
            return 0;
        } catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
    }

    // 2026-10-09..11: a task resumed from a checkpoint has no creation
    // baseline; cleanup returned silently and the draft directory survived
    // ("直接清理后草稿目录仍存在") six times.
    private static void TestResumedLockedDraftCleanup()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var serializer = new JavaScriptSerializer();
        string root = Path.Combine(AppPaths.Root, "resumed-cleanup-fixture");
        string draft = Path.Combine(root, "1011");
        string other = Path.Combine(root, "keep");
        Directory.CreateDirectory(draft);
        Directory.CreateDirectory(other);
        string index = Path.Combine(root, "root_meta_info.json");
        File.WriteAllText(index, serializer.Serialize(new {
            all_draft_store = new object[] {
                new { draft_id = "locked-id", draft_fold_path = draft },
                new { draft_id = "other-id", draft_fold_path = other }
            }
        }));
        var automation = (CapCutAutomation)FormatterServices.GetUninitializedObject(typeof(CapCutAutomation));
        typeof(CapCutAutomation).GetField("logger", flags).SetValue(automation, new AppLogger());
        typeof(CapCutAutomation).GetField("draftRoot", flags).SetValue(automation, root);
        typeof(CapCutAutomation).GetField("draftIndexPath", flags).SetValue(automation, index);
        typeof(CapCutAutomation).GetField("directDraftDirectory", flags).SetValue(automation, draft);
        typeof(CapCutAutomation).GetField("draftBaseline", flags).SetValue(automation, null);
        var cleanup = typeof(CapCutAutomation).GetMethod("CleanupCreatedDrafts", flags);

        // Without identity lock the old guarded behaviour is unchanged.
        cleanup.Invoke(automation, new object[] { true, draft, false });
        if (!Directory.Exists(draft)) throw new Exception("unlocked cleanup without baseline must not delete");

        // A locked cleanup must refuse a directory other than the locked draft.
        try {
            cleanup.Invoke(automation, new object[] { true, other, true });
            throw new Exception("locked cleanup accepted a different directory");
        } catch (TargetInvocationException ex) {
            if (!(ex.InnerException is InvalidOperationException)) throw;
        }
        if (!Directory.Exists(other)) throw new Exception("unrelated draft was deleted");

        cleanup.Invoke(automation, new object[] { true, draft, true });
        if (Directory.Exists(draft)) throw new Exception("resumed locked draft was not deleted");
        if (!Directory.Exists(other)) throw new Exception("unrelated draft was deleted");
        string remaining = File.ReadAllText(index);
        if (remaining.Contains("locked-id") || !remaining.Contains("other-id"))
            throw new Exception("root index was not updated exactly: " + remaining);
        Console.WriteLine("PASS resumed locked draft is deleted without a creation baseline");
    }

    private static void TestEnvironmentCheckTitle()
    {
        if (!CapCutAutomation.IsEnvironmentCheckTitle("环境检测")) throw new Exception("环境检测 not recognised");
        if (!CapCutAutomation.IsEnvironmentCheckTitle(" Environment check ")) throw new Exception("English title not recognised");
        foreach (string other in new[] { "CapCut", "导出-1011", "请选择媒体资源", "环境检测失败", "", null })
            if (CapCutAutomation.IsEnvironmentCheckTitle(other)) throw new Exception("unrelated window accepted: " + other);
        Console.WriteLine("PASS only the CapCut environment-check window title is accepted");
    }

    private static void TestReadIndexWhileCapCutWrites()
    {
        var read = typeof(CapCutAutomation).GetMethod("ReadTextAllowingWriters",
            BindingFlags.Static | BindingFlags.NonPublic);
        string folder = Path.Combine(AppPaths.Root, "index-share-fixture");
        Directory.CreateDirectory(folder);
        string index = Path.Combine(folder, "root_meta_info.json");
        File.WriteAllText(index, "{\"all_draft_store\":[]}");
        using (new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {
            if ((string)read.Invoke(null, new object[] { index }) != "{\"all_draft_store\":[]}")
                throw new Exception("an index held open for writing must still be readable");
        }
        var exclusive = new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = new Thread(delegate() { Thread.Sleep(400); exclusive.Dispose(); });
        release.Start();
        if ((string)read.Invoke(null, new object[] { index }) != "{\"all_draft_store\":[]}")
            throw new Exception("a briefly locked index must be read after the lock is released");
        release.Join();
        try {
            read.Invoke(null, new object[] { Path.Combine(folder, "missing.json") });
            throw new Exception("a missing index must not be retried into success");
        } catch (TargetInvocationException ex) {
            if (!(ex.InnerException is FileNotFoundException)) throw;
        }
        Console.WriteLine("PASS root index is read while CapCut briefly holds it");
    }

    private static void TestDraftFilesQuiet()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string root = Path.Combine(AppPaths.Root, "quiet-draft-fixture");
        string draft = Path.Combine(root, "1011");
        Directory.CreateDirectory(draft);
        string index = Path.Combine(root, "root_meta_info.json");
        string content = Path.Combine(draft, "draft_content.json");
        File.WriteAllText(index, "{}");
        File.WriteAllText(content, "{}");
        DateTime old = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(index, old);
        File.SetLastWriteTimeUtc(content, old);
        var automation = (CapCutAutomation)FormatterServices.GetUninitializedObject(typeof(CapCutAutomation));
        typeof(CapCutAutomation).GetField("draftRoot", flags).SetValue(automation, root);
        var quiet = typeof(CapCutAutomation).GetMethod("DraftFilesQuietFor", flags);
        if (!(bool)quiet.Invoke(automation, new object[] { TimeSpan.FromMilliseconds(1500) }))
            throw new Exception("old draft files must count as quiet");
        File.SetLastWriteTimeUtc(content, DateTime.UtcNow);
        if ((bool)quiet.Invoke(automation, new object[] { TimeSpan.FromMilliseconds(1500) }))
            throw new Exception("a draft file written just now must not count as quiet");
        File.SetLastWriteTimeUtc(content, old);
        File.SetLastWriteTimeUtc(index, DateTime.UtcNow);
        if ((bool)quiet.Invoke(automation, new object[] { TimeSpan.FromMilliseconds(1500) }))
            throw new Exception("a root index written just now must not count as quiet");
        Console.WriteLine("PASS close waits while root index or draft files are still being written");
    }

    private static void TestCenterCropForOcr()
    {
        string folder = Path.Combine(AppPaths.Root, "ocr-crop-fixture");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "editor.png");
        using (var bitmap = new System.Drawing.Bitmap(1000, 800)) {
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                graphics.Clear(System.Drawing.Color.Black);
                // Marker pixel inside the centre crop (x 30-70 %, y 30-75 %).
                graphics.FillRectangle(System.Drawing.Brushes.White, 500, 400, 4, 4);
            }
            bitmap.Save(source, System.Drawing.Imaging.ImageFormat.Png);
        }
        string crop = CapCutAutomation.CropCenterForOcr(source, "centre.png");
        if (String.IsNullOrWhiteSpace(crop) || !File.Exists(crop)) throw new Exception("centre crop was not written");
        using (var result = new System.Drawing.Bitmap(crop)) {
            if (result.Width != 800 || result.Height != 720)
                throw new Exception("centre crop must be the 40 % x 45 % centre scaled 2x: " + result.Size);
            // Source (500,400) -> crop (200,160) -> scaled (400,320).
            if (result.GetPixel(402, 322).GetBrightness() < 0.5f)
                throw new Exception("centre crop does not contain the editor centre");
        }
        if (CapCutAutomation.CropCenterForOcr(Path.Combine(folder, "missing.png"), "x.png") != "")
            throw new Exception("missing screenshot must return an empty path");
        Console.WriteLine("PASS caption progress OCR retries on an enlarged editor-centre crop");
    }
}
