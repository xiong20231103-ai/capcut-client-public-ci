using System;
using System.Threading;
using System.Windows.Forms;
using CapCutBatchClient;

internal static class CapCutUiSmokeTests
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        int expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
        Console.WriteLine("NativeInputSize=" + NativeMethods.KeyboardInputStructureSize);
        if (NativeMethods.KeyboardInputStructureSize != expectedInputSize)
            throw new InvalidOperationException("Win32 INPUT ABI mismatch; SendInput would reject keyboard input");
        using (var form = new MainForm()) {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            Thread.Sleep(250);
            Application.DoEvents();
            Console.WriteLine("HandleCreated=" + form.IsHandleCreated);
            Console.WriteLine("Visible=" + form.Visible);
            Console.WriteLine("TitleLength=" + form.Text.Length);
            if (!form.IsHandleCreated || !form.Visible || !form.Text.StartsWith("CapCut / ")) Environment.ExitCode = 1;
            form.Close();
            Application.DoEvents();
        }
        using (var categoryForm = new CategoryBatchForm(AppConfig.CreateDefault())) {
            categoryForm.ShowInTaskbar = false;
            categoryForm.Opacity = 0;
            categoryForm.Show();
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();
            Console.WriteLine("CategoryHandleCreated=" + categoryForm.IsHandleCreated);
            Console.WriteLine("CategoryTitleLength=" + categoryForm.Text.Length);
            if (!categoryForm.IsHandleCreated || !categoryForm.Visible) Environment.ExitCode = 1;
            categoryForm.Close();
            Application.DoEvents();
        }
    }
}
