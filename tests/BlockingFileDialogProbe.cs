using System;
using System.Threading;
internal static class BlockingFileDialogProbe
{
    private static void Main(string[] args)
    {
        if (args.Length > 1 && args[1] == "1") { Console.WriteLine("OK"); return; }
        if (args.Length > 1 && args[1] == "2") {
            Console.WriteLine("{\"success\":true,\"fieldVerified\":true,\"pathSubmitted\":false,\"diagnostic\":\"inspection only\"}");
            return;
        }
        Thread.Sleep(60000);
    }
}
