using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CapCutBatchClient
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try {
                TraceStartup("程序入口");
                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
                    TraceStartup("界面线程异常: " + e.Exception);
                    MessageBox.Show("程序发生错误：" + e.Exception.Message, "CapCut 自动字幕", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                    Exception ex = e.ExceptionObject as Exception;
                    TraceStartup("未处理异常: " + (ex == null ? "未知错误" : ex.ToString()));
                };
                bool autoStart = false;
                bool retryFailed = false;
                string handoffPath = "";
                bool diagnosticFirstCaptionQNoOp = false;
                string localFirstCaptionTestCase = "";
                string localFirstCaptionTestPack = "";
                string localSampleTestTaskFile = "";
                string localSampleOverridePath = "";
                bool localSampleWithoutSample = false;
                bool subtitleStyleLab = false;
                bool isolatedLabHandoff = false;
                string[] arguments = Environment.GetCommandLineArgs();
                for (int index = 0; index < arguments.Length; index++) {
                    string argument = arguments[index];
                    if (String.Equals(argument, "--auto-start", StringComparison.OrdinalIgnoreCase)) autoStart = true;
                    if (String.Equals(argument, "--retry-failed", StringComparison.OrdinalIgnoreCase)) retryFailed = true;
                    if (String.Equals(argument, "--diagnostic-first-caption-q-noop", StringComparison.OrdinalIgnoreCase))
                        diagnosticFirstCaptionQNoOp = true;
                    if (String.Equals(argument, "--local-first-caption-test", StringComparison.OrdinalIgnoreCase) &&
                        index + 1 < arguments.Length) {
                        localFirstCaptionTestCase = arguments[++index];
                    }
                    if (String.Equals(argument, "--test-pack", StringComparison.OrdinalIgnoreCase) &&
                        index + 1 < arguments.Length) {
                        localFirstCaptionTestPack = ReadPossiblyUnquotedPath(arguments, ref index);
                    }
                    if (String.Equals(argument, "--local-sample-test", StringComparison.OrdinalIgnoreCase) &&
                        index + 1 < arguments.Length) {
                        localSampleTestTaskFile = ReadPossiblyUnquotedPath(arguments, ref index);
                    }
                    if (String.Equals(argument, "--sample-override", StringComparison.OrdinalIgnoreCase) &&
                        index + 1 < arguments.Length) {
                        localSampleOverridePath = ReadPossiblyUnquotedPath(arguments, ref index);
                    }
                    if (String.Equals(argument, "--without-sample", StringComparison.OrdinalIgnoreCase)) {
                        localSampleWithoutSample = true;
                    }
                    if (String.Equals(argument, "--subtitle-style-lab", StringComparison.OrdinalIgnoreCase)) {
                        subtitleStyleLab = true;
                    }
                    if (String.Equals(argument, "--isolated-lab-handoff", StringComparison.OrdinalIgnoreCase)) {
                        isolatedLabHandoff = true;
                    }
                    if (String.Equals(argument, "--handoff", StringComparison.OrdinalIgnoreCase) &&
                        index + 1 < arguments.Length) {
                        handoffPath = ReadPossiblyUnquotedPath(arguments, ref index);
                    }
                }
                // A deployed isolated client is also opened directly by the
                // user.  It must not depend on a shortcut preserving a command
                // line flag: the marker next to that executable is the source
                // of truth for its safe runtime profile.
                bool installedIsolatedManualLab = RuntimeModes.TryEnableInstalledIsolatedLabProfile();
                if (isolatedLabHandoff) {
                    RuntimeModes.EnableIsolatedLabHandoff();
                } else if (!installedIsolatedManualLab && subtitleStyleLab) {
                    RuntimeModes.EnableSubtitleStyleLab();
                }
                if (RuntimeModes.IsolatedLabRuntime) {
                    TraceStartup("隔离字幕样式运行身份已启用: " + RuntimeModes.IsolatedLabProfileSource);
                }
                RuntimeModes.ValidateLaunch(autoStart, handoffPath);
                DiagnosticLaunchPolicy.ValidateFirstCaptionQNoOp(diagnosticFirstCaptionQNoOp, autoStart, handoffPath);
                LocalFirstCaptionTestFactory.ValidateLaunch(
                    localFirstCaptionTestCase, localFirstCaptionTestPack,
                    diagnosticFirstCaptionQNoOp, handoffPath);
                LocalSampleAppendTestFactory.ValidateLaunch(localSampleTestTaskFile, handoffPath,
                    localSampleOverridePath, localSampleWithoutSample);
                string autoStartFlag = Path.Combine(AppPaths.DataDirectory, "capcut-autostart.flag");
                if (File.Exists(autoStartFlag)) {
                    autoStart = true;
                    try { File.Delete(autoStartFlag); } catch { }
                }
                using (ClientInstanceGate gate = ClientInstanceGate.TryAcquire()) {
                    if (gate == null) {
                        ClientInstanceGate.ActivateExistingWindow();
                        TraceStartup("检测到同一安装目录已有 CapCut 批处理客户端；本次启动未读取任务表并已退出。");
                        return;
                    }
                    var form = new MainForm(autoStart, retryFailed, handoffPath, diagnosticFirstCaptionQNoOp,
                        localFirstCaptionTestCase, localFirstCaptionTestPack, localSampleTestTaskFile,
                        localSampleOverridePath, localSampleWithoutSample);
                    TraceStartup("主窗口构造完成");
                    Application.Run(form);
                    TraceStartup("程序正常退出");
                }
            } catch (Exception ex) {
                TraceStartup("启动失败: " + ex);
                MessageBox.Show("程序启动失败：" + ex.Message + "\r\n\r\n详细信息已写入 logs 目录。", "CapCut 自动字幕", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string ReadPossiblyUnquotedPath(string[] arguments, ref int optionIndex)
        {
            // PowerShell Start-Process can split an ArgumentList path containing
            // spaces into several argv entries.  Reassemble path-valued options
            // until the next named option so local test packs and handoffs work
            // whether their caller quoted the value or not.
            var parts = new System.Collections.Generic.List<string>();
            int index = optionIndex + 1;
            while (index < arguments.Length &&
                   !arguments[index].StartsWith("--", StringComparison.Ordinal)) {
                parts.Add(arguments[index]);
                index++;
            }
            if (parts.Count == 0) return "";
            optionIndex = index - 1;
            return String.Join(" ", parts.ToArray()).Trim().Trim('"');
        }

        private static void TraceStartup(string message)
        {
            try {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                string path = Path.Combine(AppPaths.LogDirectory, "capcut-startup-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = String.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1}{2}", DateTime.Now, message, Environment.NewLine);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            } catch { }
        }

        private sealed class ClientInstanceGate : IDisposable
        {
            private Mutex mutex;

            private ClientInstanceGate(Mutex mutex) { this.mutex = mutex; }

            public static ClientInstanceGate TryAcquire()
            {
                var candidate = new Mutex(false,
                    InterprocessMutexNames.ForPath("client-instance", AppPaths.Root));
                try {
                    if (!candidate.WaitOne(0, false)) {
                        candidate.Dispose();
                        return null;
                    }
                    return new ClientInstanceGate(candidate);
                } catch (AbandonedMutexException) {
                    // A crashed client cannot keep this installation locked.
                    return new ClientInstanceGate(candidate);
                }
            }

            public static void ActivateExistingWindow()
            {
                string processName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
                int current = Process.GetCurrentProcess().Id;
                foreach (Process candidate in Process.GetProcessesByName(processName)) {
                    try {
                        if (candidate.Id == current || candidate.MainWindowHandle == IntPtr.Zero) continue;
                        NativeMethods.ActivateWindow(candidate.MainWindowHandle);
                        return;
                    } catch { }
                    finally { try { candidate.Dispose(); } catch { } }
                }
            }

            public void Dispose()
            {
                Mutex current = Interlocked.Exchange(ref mutex, null);
                if (current == null) return;
                try { current.ReleaseMutex(); } catch { }
                current.Dispose();
            }
        }
    }
}
