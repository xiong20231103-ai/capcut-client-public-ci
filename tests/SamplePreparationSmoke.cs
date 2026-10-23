using System;
using System.IO;
using System.Threading;
using CapCutBatchClient;

// Isolated, command-line smoke test for the production sample preprocessor.
// It never opens CapCut or reads handoff / Dashboard state.
internal static class SamplePreparationSmoke
{
    public static int Main(string[] args)
    {
        if (args == null || args.Length != 3)
        {
            Console.Error.WriteLine("usage: SamplePreparationSmoke <video> <audio> <sample>");
            return 2;
        }

        var task = new VideoTask
        {
            Id = "sample-preparation-smoke",
            VideoPath = args[0],
            AudioPath = args[1],
            SamplePath = args[2]
        };
        new FfmpegSamplePreprocessor(new AppLogger()).Prepare(task, 120, CancellationToken.None);
        Console.WriteLine("prepared=" + task.PreparedSamplePath);
        Console.WriteLine("targetSeconds=" + task.PreparedSampleTargetSeconds.ToString("0.000"));
        Console.WriteLine("sampleSpeed=" + task.SampleSpeed.ToString("0.000"));
        return File.Exists(task.PreparedSamplePath) && Math.Abs(task.SampleSpeed - 1.0) < 0.001 ? 0 : 1;
    }
}
