using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CapCutBatchClient
{
    // Runs outside the production worker so a wedged shell provider/UIA tree
    // can be terminated without taking down the transaction or draft lease.
    internal static class CapCutFileDialogProbe
    {
        private sealed class ProbeResult
        {
            public bool success { get; set; }
            public bool fieldVerified { get; set; }
            public bool pathSubmitted { get; set; }
            public string errorCode { get; set; }
            public string diagnostic { get; set; }
        }

        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
        private const uint KeyUp = 0x0002;
        private const byte Ctrl = 0x11;
        private const byte A = 0x41;
        private const byte V = 0x56;
        private const byte Enter = 0x0D;

        [STAThread]
        private static int Main(string[] args)
        {
            var serializer = new JavaScriptSerializer();
            try {
                bool inspect = args != null && args.Length == 3 &&
                    String.Equals(args[0], "--inspect", StringComparison.OrdinalIgnoreCase);
                bool import = args != null && args.Length >= 4 &&
                    String.Equals(args[0], "--import", StringComparison.OrdinalIgnoreCase);
                bool returnEditor = args != null && args.Length == 4 && args[0] == "--return-editor";
                if (!inspect && !import && !returnEditor) {
                    Write(serializer, false, false, false, "BAD_ARGS", "expected --inspect HWND PID or --import HWND PID PATH");
                    return 2;
                }
                long raw;
                if (!Int64.TryParse(args[1], out raw)) {
                    Write(serializer, false, false, false, "BAD_HWND", "invalid native window handle");
                    return 2;
                }
                uint expectedPid, actualPid;
                IntPtr hwnd = new IntPtr(raw);
                GetWindowThreadProcessId(hwnd, out actualPid);
                if (!UInt32.TryParse(args[2], out expectedPid) || expectedPid != actualPid || !IsWindow(hwnd))
                    throw new InvalidOperationException("dialog process identity changed");
                if (returnEditor) {
                    long ticks;
                    if (!Int64.TryParse(args[3], out ticks) ||
                        Process.GetProcessById((int)actualPid).StartTime.ToUniversalTime().Ticks != ticks ||
                        GetForegroundWindow() != hwnd)
                        throw new InvalidOperationException("return-editor foreground/process identity mismatch");
                    AutomationElement root = AutomationElement.FromHandle(hwnd);
                    var queue = new Queue<KeyValuePair<AutomationElement,int>>();
                    queue.Enqueue(new KeyValuePair<AutomationElement,int>(root,0));
                    AutomationElement target = null;
                    int inspected = 0;
                    bool truncated = false;
                    while (queue.Count > 0 && inspected++ < 256) {
                        var item = queue.Dequeue();
                        var element = item.Key;
                        string name = (element.Current.Name ?? "").Trim();
                        if (element.Current.ControlType == ControlType.Button &&
                            (name == "返回编辑" || name == "Back to editing" || name == "Return to editor")) {
                            if (target != null) throw new InvalidOperationException("multiple return-editor buttons");
                            target = element;
                        }
                        if (item.Value >= 6) { if (TreeWalker.ControlViewWalker.GetFirstChild(element) != null) truncated = true; continue; }
                        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                        while (child != null && queue.Count + inspected < 256) {
                            queue.Enqueue(new KeyValuePair<AutomationElement,int>(child,item.Value+1));
                            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                        }
                        if (child != null) truncated = true;
                    }
                    object pattern;
                    if (truncated || queue.Count != 0 || target == null || !target.Current.IsEnabled || target.Current.IsOffscreen ||
                        !target.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        throw new InvalidOperationException("unique enabled return-editor action not verified");
                    if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("foreground changed before return-editor");
                    ((InvokePattern)pattern).Invoke();
                    Write(serializer,true,true,true,"","exact return-editor action invoked once");
                    return 0;
                }
                long processStarted = Process.GetProcessById((int)actualPid).StartTime.ToUniversalTime().Ticks;
                // Resolve the dedicated native filename control before touching
                // UIA. Walking the shell navigation pane can stall on providers.
                IntPtr nativeField = FindNativeFilenameField(hwnd);
                AutomationElement dialog = null;
                AutomationElement field;
                if (nativeField != IntPtr.Zero) {
                    field = AutomationElement.FromHandle(nativeField);
                    if (field.Current.ControlType != ControlType.Edit) field = null;
                } else {
                    dialog = AutomationElement.FromHandle(hwnd);
                    field = FindField(dialog);
                }
                if (field == null) {
                    Write(serializer, false, false, false, "FIELD_MISSING", "dedicated filename field was not found");
                    return 3;
                }
                if (inspect) {
                    if (!HasOpenAction(hwnd)) {
                        Write(serializer, false, true, false, "ACTION_MISSING", "verified Open action was not found");
                        return 3;
                    }
                    Write(serializer, true, true, false, "", "filename field verified");
                    return 0;
                }
                string path = String.Join(" ", Slice(args, 3));
                if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) {
                    Write(serializer, false, true, false, "BAD_PATH", "media path is not absolute");
                    return 2;
                }
                EnsurePickerForeground(hwnd, actualPid, processStarted, field != null);
                if (!HasOpenAction(hwnd))
                    throw new InvalidOperationException("Open action changed before path entry");
                field.SetFocus();
                object value;
                if (field.TryGetCurrentPattern(ValuePattern.Pattern, out value)) {
                    ((ValuePattern)value).SetValue(path);
                } else {
                    SetClipboard(path);
                    Chord(Ctrl, A); Thread.Sleep(40); Chord(Ctrl, V);
                }
                Thread.Sleep(60);
                // After path entry, never refocus/retry: its outcome may be unknown.
                if (GetForegroundWindow() != hwnd || !SameProcessInstance(hwnd, actualPid, processStarted) ||
                    !HasOpenAction(hwnd))
                    throw new InvalidOperationException("foreground or identity changed before path confirmation");
                keybd_event(Enter, 0, 0, UIntPtr.Zero);
                keybd_event(Enter, 0, KeyUp, UIntPtr.Zero);
                Write(serializer, true, true, true, "", "path submitted");
                return 0;
            } catch (Exception ex) {
                Write(serializer, false, false, false, "PROBE_FAILED",
                    ex.GetType().Name + ":" + ex.Message);
                return 4;
            }
        }

        private static bool SameProcessInstance(IntPtr hwnd, uint pid, long started)
        {
            uint current;
            GetWindowThreadProcessId(hwnd, out current);
            return IsWindow(hwnd) && current == pid &&
                Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks == started;
        }

        private static void EnsurePickerForeground(IntPtr hwnd, uint pid, long started, bool fieldVerified)
        {
            // Bounded activation only, before any clipboard/path/keyboard operation.
            for (int attempt = 0; attempt < 3; attempt++) {
                var title = new StringBuilder(256);
                var kind = new StringBuilder(128);
                GetWindowText(hwnd, title, title.Capacity);
                GetClassName(hwnd, kind, kind.Capacity);
                if (!FileDialogIdentityPolicy.CanRefocusPicker(
                    SameProcessInstance(hwnd, pid, started), IsWindowVisible(hwnd),
                    IsWindowEnabled(hwnd), title.ToString(), kind.ToString(), fieldVerified, HasOpenAction(hwnd)))
                    throw new InvalidOperationException("picker identity changed before foreground acquisition");
                if (GetForegroundWindow() == hwnd) return;
                SetForegroundWindow(hwnd);
                Thread.Sleep(100);
            }
            if (GetForegroundWindow() != hwnd || !SameProcessInstance(hwnd, pid, started))
                throw new InvalidOperationException("verified picker foreground acquisition failed");
        }

        private static IntPtr FindNativeFilenameField(IntPtr dialog)
        {
            var matches = new List<IntPtr>();
            EnumChildWindows(dialog, delegate(IntPtr child, IntPtr unused) {
                var kind = new StringBuilder(128);
                GetClassName(child, kind, kind.Capacity);
                if (!String.Equals(kind.ToString(), "Edit", StringComparison.OrdinalIgnoreCase) ||
                    !IsWindowVisible(child) || !IsWindowEnabled(child)) return true;
                IntPtr ancestor = child;
                for (int depth=0; depth<4 && ancestor!=IntPtr.Zero && ancestor!=dialog; depth++) {
                    if (GetDlgCtrlID(ancestor)==1148) { matches.Add(child); break; }
                    ancestor=GetParent(ancestor);
                }
                return true;
            }, IntPtr.Zero);
            return matches.Count==1 ? matches[0] : IntPtr.Zero;
        }

        private static bool HasOpenAction(IntPtr dialog)
        {
            IntPtr button = GetDlgItem(dialog, 1);
            if (button == IntPtr.Zero || !IsWindowVisible(button) || !IsWindowEnabled(button)) return false;
            var text = new StringBuilder(128);
            GetWindowText(button, text, text.Capacity);
            string name = text.ToString().Replace("&", "");
            return name.IndexOf("打开", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Write(JavaScriptSerializer serializer, bool success,
            bool fieldVerified, bool pathSubmitted, string errorCode,
            string diagnostic)
        {
            Console.WriteLine(serializer.Serialize(new ProbeResult {
                success = success,
                fieldVerified = fieldVerified,
                pathSubmitted = pathSubmitted,
                errorCode = errorCode ?? "",
                diagnostic = diagnostic ?? ""
            }));
        }

        private static string[] Slice(string[] args, int start)
        {
            var list = new List<string>();
            for (int i = start; i < args.Length; i++) list.Add(args[i]);
            return list.ToArray();
        }

        private static AutomationElement FindField(AutomationElement dialog)
        {
            if (dialog == null) return null;
            var pending = new Stack<Tuple<AutomationElement, int>>();
            pending.Push(Tuple.Create(dialog, 0));
            int inspected = 0;
            while (pending.Count > 0 && inspected++ < 256) {
                var item = pending.Pop();
                AutomationElement e = item.Item1;
                string id = "", name = "";
                ControlType type = null;
                try { id = e.Current.AutomationId ?? ""; name = e.Current.Name ?? ""; type = e.Current.ControlType; }
                catch { continue; }
                if (FileDialogIdentityPolicy.IsSafeFileNameControl(id, name)) {
                    if (type == ControlType.Edit) return e;
                    try {
                        AutomationElement child = e.FindFirst(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                        if (child != null) return child;
                    } catch { }
                }
                if (item.Item2 >= 6) continue;
                try {
                    AutomationElement child = TreeWalker.ControlViewWalker.GetFirstChild(e);
                    var siblings = new List<AutomationElement>();
                    while (child != null && siblings.Count < 64) {
                        siblings.Add(child); child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                    for (int i = siblings.Count - 1; i >= 0; i--)
                        pending.Push(Tuple.Create(siblings[i], item.Item2 + 1));
                } catch { }
            }
            return null;
        }

        private static void Chord(byte modifier, byte key)
        {
            keybd_event(modifier, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyUp, UIntPtr.Zero); keybd_event(modifier, 0, KeyUp, UIntPtr.Zero);
        }

        private static void SetClipboard(string text)
        {
            Exception error = null;
            var thread = new Thread(new ThreadStart(delegate { try { Clipboard.SetText(text ?? ""); } catch (Exception ex) { error = ex; } }));
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(1000);
            if (error != null) throw error;
        }
    }
}
