using System;

namespace CapCutBatchClient
{
    internal static class DraftStyleIntegrationSmoke
    {
        private static int Main(string[] args)
        {
            if (args == null || args.Length != 1) {
                Console.Error.WriteLine("Usage: DraftStyleIntegrationSmoke.exe <copied-draft-directory>");
                return 2;
            }
            try {
                var logger = new AppLogger();
                var patcher = new DirectDraftSubtitleStylePatcher();
                DirectDraftStyleResult result = patcher.Apply(args[0], logger);
                patcher.VerifyPersisted(args[0], result.PatchedFiles);
                Console.WriteLine(
                    "PASS captions={0} files={1} backup={2}",
                    result.CaptionCount,
                    result.PatchedFiles == null ? 0 : result.PatchedFiles.Length,
                    result.BackupDirectory);
                return 0;
            } catch (Exception ex) {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
