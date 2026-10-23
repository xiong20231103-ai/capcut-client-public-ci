using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CapCutBatchClient
{
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT keyboard;
            // INPUT's native union is sized by MOUSEINPUT on x64.  Omitting
            // this member makes Marshal.SizeOf(INPUT) 32 instead of the
            // required 40 bytes and SendInput returns ERROR_INVALID_PARAMETER.
            [FieldOffset(0)] public MOUSEINPUT mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort virtualKey;
            public ushort scanCode;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint command);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int SW_RESTORE = 9;
        public const int SW_MAXIMIZE = 3;
        public const int SW_SHOW = 5;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const byte VK_MENU = 0x12;
        public const byte VK_CONTROL = 0x11;
        public const byte VK_SHIFT = 0x10;
        public const byte VK_E = 0x45;
        public const byte VK_I = 0x49;
        public const byte VK_L = 0x4C;
        public const byte VK_A = 0x41;
        public const byte VK_C = 0x43;
        public const byte VK_V = 0x56;
        public const byte VK_N = 0x4E;
        public const byte VK_RETURN = 0x0D;
        public const byte VK_ESCAPE = 0x1B;
        public const byte VK_DELETE = 0x2E;
        public const byte VK_HOME = 0x24;
        public const byte VK_RIGHT = 0x27;
        public const byte VK_Q = 0x51;
        public const uint WM_CLOSE = 0x0010;
        public const uint GW_OWNER = 4;
        public const uint GA_ROOTOWNER = 3;
        public static int LastSendInputError { get; private set; }
        public static int KeyboardInputStructureSize { get { return Marshal.SizeOf(typeof(INPUT)); } }

        // UI Automation's RootElement.FindAll can block for a very long time
        // when a Chromium/QML CapCut surface is busy.  Top-level window
        // discovery is a Win32 operation and must remain bounded so a media
        // import cannot appear frozen and so Stop() can reach the lease
        // release path.  UIA is still used after a handle has been selected.
        public static IList<IntPtr> GetTopLevelWindowHandles()
        {
            var handles = new List<IntPtr>();
            try {
                EnumWindows(delegate(IntPtr handle, IntPtr ignored) {
                    if (handle != IntPtr.Zero && IsWindowVisible(handle))
                        handles.Add(handle);
                    return true;
                }, IntPtr.Zero);
            } catch { }
            return handles;
        }

        public static string GetWindowTitle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return "";
            try {
                var buffer = new StringBuilder(512);
                int length = GetWindowText(handle, buffer, buffer.Capacity);
                return length <= 0 ? "" : buffer.ToString();
            } catch { return ""; }
        }

        public static string GetWindowClassName(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return "";
            try {
                var buffer = new StringBuilder(256);
                int length = GetClassName(handle, buffer, buffer.Capacity);
                return length <= 0 ? "" : buffer.ToString();
            } catch { return ""; }
        }

        public static bool ActivateWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            ShowWindow(handle, SW_MAXIMIZE);
            return FocusWindow(handle);
        }

        public static bool EnsureMaximized(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !IsWindowVisible(handle) ||
                IsHungAppWindow(handle)) return false;
            for (int attempt = 0; attempt < 3; attempt++) {
                ShowWindow(handle, SW_MAXIMIZE);
                FocusWindow(handle);
                if (IsZoomed(handle)) return true;
                RECT rect;
                if (GetWindowRect(handle, out rect)) {
                    try {
                        Rectangle work = Screen.FromHandle(handle).WorkingArea;
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        if (width >= (int)(work.Width * 0.95) &&
                            height >= (int)(work.Height * 0.95)) return true;
                    } catch { }
                }
                Thread.Sleep(150);
            }
            return false;
        }

        public static bool FocusWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            IntPtr previous = GetForegroundWindow();
            uint processId;
            uint targetThread = GetWindowThreadProcessId(handle, out processId);
            uint foregroundThread = previous == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(previous, out processId);
            uint currentThread = GetCurrentThreadId();
            bool attachedForeground = false;
            bool attachedTarget = false;
            try {
                if (foregroundThread != 0 && foregroundThread != currentThread) {
                    attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
                }
                if (targetThread != 0 && targetThread != currentThread) {
                    attachedTarget = AttachThreadInput(currentThread, targetThread, true);
                }
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                ShowWindow(handle, SW_SHOW);
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
                SetFocus(handle);
                SwitchToThisWindow(handle, true);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            } finally {
                if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
            }
            IntPtr active = GetForegroundWindow();
            if (active != handle && !BelongsToSameProcess(active, handle)) {
                SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                RECT rect;
                if (GetWindowRect(handle, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) {
                    Click(rect.Left + (rect.Right - rect.Left) / 2, rect.Top + 12, 1);
                }
                SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                active = GetForegroundWindow();
            }
            return active == handle || BelongsToSameProcess(active, handle);
        }

        public static void CenterWindowOnCurrentScreen(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            RECT rect;
            if (!GetWindowRect(handle, out rect)) return;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;
            System.Drawing.Rectangle workArea = Screen.FromHandle(handle).WorkingArea;
            int x = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
            int y = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
        }

        public static bool BelongsToSameProcess(IntPtr first, IntPtr second)
        {
            if (first == IntPtr.Zero || second == IntPtr.Zero) return false;
            uint firstProcessId;
            uint secondProcessId;
            GetWindowThreadProcessId(first, out firstProcessId);
            GetWindowThreadProcessId(second, out secondProcessId);
            return firstProcessId != 0 && firstProcessId == secondProcessId;
        }

        public static bool SendImportShortcut()
        {
            INPUT[] inputs = {
                KeyboardInput(VK_CONTROL, 0),
                KeyboardInput(VK_I, 0),
                KeyboardInput(VK_I, KEYEVENTF_KEYUP),
                KeyboardInput(VK_CONTROL, KEYEVENTF_KEYUP)
            };
            uint sent = SendInput((uint)inputs.Length, inputs,
                KeyboardInputStructureSize);
            LastSendInputError = sent == (uint)inputs.Length
                ? 0 : Marshal.GetLastWin32Error();
            return sent == (uint)inputs.Length;
        }

        private static INPUT KeyboardInput(byte key, uint flags)
        {
            return new INPUT {
                type = 1,
                union = new INPUTUNION {
                    keyboard = new KEYBDINPUT {
                        virtualKey = key,
                        scanCode = 0,
                        flags = flags,
                        time = 0,
                        extraInfo = IntPtr.Zero
                    }
                }
            };
        }

        public static void SendControlChord(byte key)
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void SendControlAltChord(byte key)
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void ReleaseModifierKeys()
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressEnter()
        {
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressEscape()
        {
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressDelete()
        {
            keybd_event(VK_DELETE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_DELETE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressHome()
        {
            keybd_event(VK_HOME, 0, 0, UIntPtr.Zero);
            keybd_event(VK_HOME, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressRight()
        {
            keybd_event(VK_RIGHT, 0, 0, UIntPtr.Zero);
            keybd_event(VK_RIGHT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void PressQ()
        {
            keybd_event(VK_Q, 0, 0, UIntPtr.Zero);
            keybd_event(VK_Q, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void Click(int x, int y, int count)
        {
            SetCursorPos(x, y);
            for (int i = 0; i < count; i++) {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                if (count > 1) Thread.Sleep(90);
            }
        }

        public static void SendUnicodeText(string value)
        {
            if (String.IsNullOrEmpty(value)) return;
            var inputs = new List<INPUT>();
            foreach (char character in value) {
                inputs.Add(new INPUT {
                    type = 1,
                    union = new INPUTUNION {
                        keyboard = new KEYBDINPUT {
                            virtualKey = 0,
                            scanCode = character,
                            flags = KEYEVENTF_UNICODE,
                            time = 0,
                            extraInfo = IntPtr.Zero
                        }
                    }
                });
                inputs.Add(new INPUT {
                    type = 1,
                    union = new INPUTUNION {
                        keyboard = new KEYBDINPUT {
                            virtualKey = 0,
                            scanCode = character,
                            flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                            time = 0,
                            extraInfo = IntPtr.Zero
                        }
                    }
                });
            }
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Count) {
                throw new InvalidOperationException("Windows 原生文字输入未完整发送，错误码=" +
                    Marshal.GetLastWin32Error());
            }
        }

        public static void RightClick(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        public static void ScrollVertical(int x, int y, int wheelDelta)
        {
            SetCursorPos(x, y);
            Thread.Sleep(100);
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)wheelDelta), UIntPtr.Zero);
        }

        public static void Drag(int fromX, int fromY, int toX, int toY)
        {
            SetCursorPos(fromX, fromY);
            Thread.Sleep(120);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            const int steps = 24;
            for (int i = 1; i <= steps; i++) {
                int x = fromX + (toX - fromX) * i / steps;
                int y = fromY + (toY - fromY) * i / steps;
                SetCursorPos(x, y);
                Thread.Sleep(20);
            }
            Thread.Sleep(180);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }

    public sealed class ControlLocator
    {
        private readonly AppConfig config;
        private readonly AppLogger logger;

        public ControlLocator(AppConfig config, AppLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public AutomationElement ElementFromWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            try { return AutomationElement.FromHandle(handle); }
            catch { return null; }
        }

        public AutomationElement FindTopLevelWindow(IEnumerable<string> names)
        {
            return FindTopLevelWindow(names, -1);
        }

        public AutomationElement FindTopLevelWindow(IEnumerable<string> names, int excludedProcessId)
        {
            return FindTopLevelWindow(names, excludedProcessId, null);
        }

        public AutomationElement FindTopLevelWindow(IEnumerable<string> names, int excludedProcessId, IEnumerable<string> allowedProcessNames)
        {
            try {
                var allowed = new HashSet<string>((allowedProcessNames ?? new string[0])
                    .Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
                AutomationElement best = null;
                double bestArea = 0;
                foreach (IntPtr handle in NativeMethods.GetTopLevelWindowHandles()) {
                    int processId;
                    try {
                        uint nativeProcessId;
                        NativeMethods.GetWindowThreadProcessId(handle, out nativeProcessId);
                        processId = (int)nativeProcessId;
                    }
                    catch { continue; }
                    if (excludedProcessId > 0 && processId == excludedProcessId) continue;
                    if (allowed.Count > 0) {
                        string processName;
                        try { processName = Process.GetProcessById(processId).ProcessName; }
                        catch { continue; }
                        if (!allowed.Contains(processName)) continue;
                    }
                    string title = NativeMethods.GetWindowTitle(handle);
                    bool titleMatch = names.Any(name => !String.IsNullOrWhiteSpace(name) &&
                        title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                    // Some Chromium child shells expose an empty Win32 title but
                    // a useful UIA name.  Only pay the UIA conversion cost after
                    // the native process filter; never ask UIA for every desktop
                    // window while a media operation is in flight.
                    AutomationElement window = null;
                    if (!titleMatch) {
                        window = ElementFromWindow(handle);
                        if (window == null) continue;
                        string semanticTitle = SafeName(window);
                        titleMatch = names.Any(name => !String.IsNullOrWhiteSpace(name) &&
                            semanticTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    if (!titleMatch) continue;
                    if (window == null) window = ElementFromWindow(handle);
                    if (window == null) continue;
                    Rect bounds = window.Current.BoundingRectangle;
                    double area = bounds.Width * bounds.Height;
                    if (area > bestArea) {
                        best = window;
                        bestArea = area;
                    }
                }
                return best;
            } catch { }
            return null;
        }

        public AutomationElement Find(string action, AutomationElement root)
        {
            if (root == null) return null;
            LocatorRule rule = config.GetRule(action);
            return FindByRule(rule, root);
        }

        public AutomationElement FindByRule(LocatorRule rule, AutomationElement root)
        {
            if (root == null || rule == null || !rule.HasSemanticSelector) return null;
            try {
                foreach (string id in rule.AutomationIds.Where(value => !String.IsNullOrWhiteSpace(value))) {
                    AutomationElement match = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (match != null) return match;
                }
                foreach (string name in rule.Names.Where(value => !String.IsNullOrWhiteSpace(value))) {
                    AutomationElement match = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
                    if (match != null) return match;
                }

                // Never fall back to an unbounded descendant walk for a
                // calibrated action.  CapCut's QML/Chromium editor can expose
                // tens of thousands of UIA nodes and a single FindAll call may
                // block the batch thread for a minute.  The caller already has
                // a coordinate fallback after a short bounded wait; using that
                // fallback keeps the target deterministic and allows Stop() to
                // reach the lease-release transaction.  Uncalibrated actions
                // retain the exact id/name queries above but do not guess by a
                // substring scan.
                if (rule.HasCoordinate) return null;
            } catch { }
            return null;
        }

        public bool Exists(string action, AutomationElement root)
        {
            return Find(action, root) != null;
        }

        public bool WaitUntilExists(string action, AutomationElement root, int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                if (Exists(action, root)) return true;
                Thread.Sleep(300);
            }
            return false;
        }

        public bool WaitForVisualContent(string action, AutomationElement root, int timeoutSeconds, CancellationToken token)
        {
            LocatorRule rule = config.GetRule(action);
            if (root == null || !rule.HasCoordinate) return false;
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                EnsureRootIsForeground(root);
                Rect rect = root.Current.BoundingRectangle;
                int centerX = (int)(rect.Left + rect.Width * rule.RelativeX);
                int centerY = (int)(rect.Top + rect.Height * rule.RelativeY);
                using (var bitmap = new System.Drawing.Bitmap(80, 64))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen(centerX - 40, centerY - 32, 0, 0, bitmap.Size);
                    int min = 255;
                    int max = 0;
                    for (int y = 0; y < bitmap.Height; y += 4) {
                        for (int x = 0; x < bitmap.Width; x += 4) {
                            System.Drawing.Color color = bitmap.GetPixel(x, y);
                            int value = (color.R + color.G + color.B) / 3;
                            min = Math.Min(min, value);
                            max = Math.Max(max, value);
                        }
                    }
                    if (max - min >= 35) {
                        logger.Info("检测到素材缩略图: " + action);
                        return true;
                    }
                }
                Thread.Sleep(500);
            }
            return false;
        }

        public bool WaitForCaptionClip(string action, AutomationElement root, int timeoutSeconds, CancellationToken token)
        {
            if (root == null) return false;
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                EnsureRootIsForeground(root);
                System.Drawing.Rectangle firstClip;
                if (TryFindCaptionTrack(root, out firstClip)) {
                    logger.Info("Detected a continuous caption track clip: " + action);
                    return true;
                }
                Thread.Sleep(500);
            }
            return false;
        }

        public bool HasCaptionClipAtPoint(string action, AutomationElement root)
        {
            LocatorRule rule = config.GetRule(action);
            if (root == null || !rule.HasCoordinate) return false;
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            int targetX = (int)(rect.Left + rect.Width * rule.RelativeX);
            System.Drawing.Rectangle firstClip;
            if (!TryFindCaptionTrack(root, out firstClip)) return false;
            return targetX >= firstClip.Left && targetX < firstClip.Right;
        }

        public bool TryGetFirstCaptionLeftEdge(AutomationElement root, out int leftEdge)
        {
            leftEdge = 0;
            System.Drawing.Rectangle firstClip;
            if (!TryGetFirstCaptionBounds(root, out firstClip)) return false;
            leftEdge = firstClip.Left;
            return true;
        }

        public bool TryGetFirstCaptionBounds(AutomationElement root, out System.Drawing.Rectangle firstClip)
        {
            firstClip = System.Drawing.Rectangle.Empty;
            if (root == null) return false;
            EnsureRootIsForeground(root);
            return TryFindCaptionTrack(root, out firstClip);
        }

        public bool ClickFirstCaptionClip(string trackAction, string zeroAction, AutomationElement root)
        {
            if (root == null) return false;
            EnsureRootIsForeground(root);
            System.Drawing.Rectangle firstClip;
            if (!TryFindCaptionTrack(root, out firstClip)) return false;
            NativeMethods.Click(firstClip.Left + firstClip.Width / 2, firstClip.Top + firstClip.Height / 2, 1);
            logger.Info(String.Format("Selected the center of the first real caption clip at ({0}, {1})",
                firstClip.Left + firstClip.Width / 2, firstClip.Top + firstClip.Height / 2));
            return true;
        }

        public bool IsWideInputVisible(string action, AutomationElement root)
        {
            LocatorRule rule = config.GetRule(action);
            if (root == null || !rule.HasCoordinate) return false;
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            int centerX = (int)(rect.Left + rect.Width * rule.RelativeX);
            int centerY = (int)(rect.Top + rect.Height * rule.RelativeY);
            const int width = 280;
            const int height = 34;
            using (var bitmap = new System.Drawing.Bitmap(width, height))
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen(centerX - width / 2, centerY - height / 2, 0, 0, bitmap.Size);
                for (int y = 0; y < bitmap.Height; y++) {
                    int run = 0;
                    int maxRun = 0;
                    for (int x = 0; x < bitmap.Width; x++) {
                        System.Drawing.Color color = bitmap.GetPixel(x, y);
                        int maxChannel = Math.Max(color.R, Math.Max(color.G, color.B));
                        int minChannel = Math.Min(color.R, Math.Min(color.G, color.B));
                        int value = (color.R + color.G + color.B) / 3;
                        if (maxChannel - minChannel <= 14 && value >= 38 && value <= 105) {
                            run++;
                            maxRun = Math.Max(maxRun, run);
                        } else {
                            run = 0;
                        }
                    }
                    if (maxRun >= 150) return true;
                }
            }
            return false;
        }

        private bool TryFindCaptionTrack(AutomationElement root, out System.Drawing.Rectangle firstClip)
        {
            firstClip = System.Drawing.Rectangle.Empty;
            if (root == null) return false;
            Rect rect = root.Current.BoundingRectangle;
            LocatorRule calibratedZero = config.GetRule("FirstCaptionLeftEdge");
            double scanLeftRatio = FirstCaptionFrameValidation.CaptionScanLeftRatio(
                calibratedZero.RelativeX, calibratedZero.HasCoordinate);
            int left = (int)(rect.Left + rect.Width * scanLeftRatio);
            int right = (int)(rect.Left + rect.Width * 0.75);
            int top = (int)(rect.Top + rect.Height * 0.58);
            int bottom = (int)(rect.Top + rect.Height * 0.80);
            int width = Math.Max(1, right - left);
            int height = Math.Max(1, bottom - top);

            using (var bitmap = new System.Drawing.Bitmap(width, height))
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
                for (int y = 0; y < bitmap.Height; y += 2) {
                    int rowMatches = 0;
                    int run = 0;
                    int maxRun = 0;
                    int currentRunStart = -1;
                    int firstSolidStart = -1;
                    int firstSolidSamples = 0;
                    for (int x = 0; x < bitmap.Width; x += 2) {
                        if (IsCaptionRed(bitmap.GetPixel(x, y))) {
                            rowMatches++;
                            if (run == 0) currentRunStart = x;
                            run++;
                            maxRun = Math.Max(maxRun, run);
                        } else {
                            if (firstSolidStart < 0 && run >= 12) {
                                firstSolidStart = currentRunStart;
                                firstSolidSamples = run;
                            }
                            run = 0;
                            currentRunStart = -1;
                        }
                    }
                    if (firstSolidStart < 0 && run >= 12) {
                        firstSolidStart = currentRunStart;
                        firstSolidSamples = run;
                    }
                    // Real CapCut caption clips form broad, solid red/brown bars.  Short captions
                    // can be less than 160px wide at this timeline zoom, so use the run plus row
                    // density rather than requiring a long continuous transcript.
                    if (rowMatches >= 45 && maxRun >= 20 && firstSolidStart >= 0) {
                        firstClip = new System.Drawing.Rectangle(
                            left + firstSolidStart,
                            top + y,
                            Math.Max(24, firstSolidSamples * 2),
                            20);
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsCaptionRed(System.Drawing.Color color)
        {
            return color.R >= 100 && color.R >= color.G + 25 && color.R >= color.B + 20;
        }

        public bool HasAudioWaveform(AutomationElement root)
        {
            return InspectAudioTimeline(root).HasWaveform;
        }

        // A blue waveform only proves that the MP3 reached some audio lane.  It does
        // not prove that CapCut snapped it to the project zero point.  Keep this
        // separate from HasAudioWaveform so callers cannot accidentally treat an
        // offset narration as a successful handoff.
        public bool IsAudioWaveformAlignedAtTimelineStart(AutomationElement root, out int waveformStartX, out int timelineZeroX)
        {
            AudioTimelineEvidence evidence = InspectAudioTimeline(root);
            waveformStartX = evidence.FirstWaveformX;
            timelineZeroX = evidence.TimelineZeroX;
            return evidence.IsAligned;
        }

        internal AudioTimelineEvidence InspectAudioTimeline(AutomationElement root)
        {
            if (root == null) return new AudioTimelineEvidence { FirstWaveformX = -1, TimelineZeroX = -1 };
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            using (System.Drawing.Bitmap bitmap = CaptureRootBitmap(rect)) {
                int localZeroX = (int)Math.Round(bitmap.Width * 0.071);
                AudioTimelineEvidence local = AudioTimelineVision.Analyze(bitmap, localZeroX);
                local.FirstWaveformX = local.FirstWaveformX < 0
                    ? -1 : (int)rect.Left + local.FirstWaveformX;
                local.TimelineZeroX = (int)rect.Left + local.TimelineZeroX;
                local.ScanArea = new System.Drawing.Rectangle(
                    (int)rect.Left + local.ScanArea.X,
                    (int)rect.Top + local.ScanArea.Y,
                    local.ScanArea.Width,
                    local.ScanArea.Height);
                return local;
            }
        }

        public bool Click(string action, AutomationElement root, int timeoutSeconds, CancellationToken token, bool required)
        {
            LocatorRule rule = config.GetRule(action);
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                // A calibrated action already has a deterministic target.  Do
                // not start a UIA descendant query before using it: on a busy
                // Chromium/QML editor that query can block the worker thread,
                // preventing Stop() from reaching the lease-release path.  UIA
                // remains the primary route for semantic-only controls.
                AutomationElement element = rule.HasCoordinate ? null : FindByRule(rule, root);
                if (element != null) {
                    bool enabled = false;
                    try { enabled = element.Current.IsEnabled; } catch { }
                    if (enabled) {
                        logger.Info("定位控件成功: " + action + " / " + SafeName(element));
                        ClickElement(element, 1);
                        return true;
                    }
                    logger.Warn("跳过禁用控件: " + action + " / " + SafeName(element));
                }
                if (rule.HasCoordinate) {
                    ClickRelative(root, rule, 1);
                    logger.Info("使用校准坐标: " + action);
                    return true;
                }
                Thread.Sleep(250);
            }
            if (required) throw new InvalidOperationException("找不到控件: " + action + "。请运行校准或修改 capcut.config.json");
            return false;
        }

        public bool DoubleClick(string action, AutomationElement root, int timeoutSeconds, CancellationToken token, bool required)
        {
            LocatorRule rule = config.GetRule(action);
            AutomationElement element = rule.HasCoordinate ? null : FindByRule(rule, root);
            if (element != null) { ClickElement(element, 2); return true; }
            if (rule.HasCoordinate) { ClickRelative(root, rule, 2); return true; }
            if (required) throw new InvalidOperationException("找不到可双击的控件: " + action);
            return false;
        }

        public bool DragRelative(string sourceAction, string targetAction, AutomationElement root, bool required)
        {
            LocatorRule source = config.GetRule(sourceAction);
            LocatorRule target = config.GetRule(targetAction);
            if (root != null && source.HasCoordinate && target.HasCoordinate) {
                EnsureRootIsForeground(root);
                Rect rect = root.Current.BoundingRectangle;
                int fromX = (int)(rect.Left + rect.Width * source.RelativeX);
                int fromY = (int)(rect.Top + rect.Height * source.RelativeY);
                int toX = (int)(rect.Left + rect.Width * target.RelativeX);
                int toY = (int)(rect.Top + rect.Height * target.RelativeY);
                NativeMethods.Drag(fromX, fromY, toX, toY);
                logger.Info("使用校准坐标拖拽: " + sourceAction + " -> " + targetAction);
                return true;
            }
            if (required) throw new InvalidOperationException("缺少拖拽坐标: " + sourceAction + " 或 " + targetAction);
            return false;
        }

        public bool ClickRelativeIfCyan(string action, AutomationElement root)
        {
            LocatorRule rule = config.GetRule(action);
            if (root == null || !rule.HasCoordinate) return false;
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            int x = (int)(rect.Left + rect.Width * rule.RelativeX);
            int y = (int)(rect.Top + rect.Height * rule.RelativeY);
            System.Drawing.Point cyanCenter;
            if (!TryFindCyanCenter(x, y, out cyanCenter)) {
                logger.Info("未检测到青色按钮，跳过可选点击: " + action);
                return false;
            }
            // A calibration point can sit on the edge after a UI update.  The
            // screenshot probe already found the live cyan button, so click that
            // actual center rather than the stale configured point.
            NativeMethods.Click(cyanCenter.X, cyanCenter.Y, 1);
            logger.Info(String.Format("检测到青色按钮并点击实际中心: {0} / 校准=({1}, {2})，实际=({3}, {4})", action, x, y, cyanCenter.X, cyanCenter.Y));
            return true;
        }

        public bool ClickSemanticOnly(string action, AutomationElement root)
        {
            if (root == null) return false;
            LocatorRule rule = config.GetRule(action);
            if (rule == null || !rule.HasSemanticSelector) return false;
            AutomationElement element = null;
            try {
                // Caption recognition must never use the generic substring fallback:
                // names such as "Start" can also match unrelated controls (for example
                // the analysis panel).  UIA is accepted only when the automation id or
                // visible control name is an exact configured match.  If CapCut does not
                // expose that exact semantic control, the caller falls back to the
                // bounded left-panel visual detector instead.
                foreach (string id in rule.AutomationIds.Where(value => !String.IsNullOrWhiteSpace(value))) {
                    element = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (element != null) break;
                }
                if (element == null) {
                    foreach (string name in rule.Names.Where(value => !String.IsNullOrWhiteSpace(value))) {
                        element = root.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.NameProperty, name));
                        if (element != null) break;
                    }
                }
            } catch { element = null; }
            if (element == null) return false;
            bool enabled = false;
            try { enabled = element.Current.IsEnabled; } catch { }
            if (!enabled) return false;
            ClickElement(element, 1);
            logger.Info("已通过语义控件点击: " + action + " / " + SafeName(element));
            return true;
        }

        public bool HasExactSemanticControl(string action, AutomationElement root)
        {
            if (root == null) return false;
            LocatorRule rule = config.GetRule(action);
            if (rule == null || !rule.HasSemanticSelector) return false;
            try {
                foreach (string id in rule.AutomationIds.Where(value =>
                    !String.IsNullOrWhiteSpace(value))) {
                    AutomationElement element = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (IsVisibleElement(element)) return true;
                }
                foreach (string name in rule.Names.Where(value =>
                    !String.IsNullOrWhiteSpace(value))) {
                    AutomationElement element = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, name));
                    if (IsVisibleElement(element)) return true;
                }
            } catch { }
            return false;
        }

        private static bool IsVisibleElement(AutomationElement element)
        {
            if (element == null) return false;
            try {
                Rect bounds = element.Current.BoundingRectangle;
                return !element.Current.IsOffscreen && bounds.Width > 2 && bounds.Height > 2;
            } catch { return false; }
        }

        public bool ClickDynamicCaptionRecognitionButton(AutomationElement root,
            int timeoutSeconds, CancellationToken token)
        {
            if (root == null) return false;
            Stopwatch watch = Stopwatch.StartNew();
            System.Drawing.Rectangle previous = System.Drawing.Rectangle.Empty;
            int stableFrames = 0;
            string lastDiagnostic = "not scanned";
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                EnsureRootIsForeground(root);
                Rect rect = root.Current.BoundingRectangle;
                System.Drawing.Rectangle candidate;
                using (System.Drawing.Bitmap bitmap = CaptureRootBitmap(rect)) {
                    if (!CaptionRecognitionButtonVision.TryFind(bitmap, out candidate, out lastDiagnostic)) {
                        previous = System.Drawing.Rectangle.Empty;
                        stableFrames = 0;
                        Thread.Sleep(250);
                        continue;
                    }
                }
                if (CaptionRecognitionButtonVision.IsStable(previous, candidate, 8)) stableFrames++;
                else stableFrames = 1;
                previous = candidate;
                if (stableFrames >= 2) {
                    int clickX = (int)rect.Left + candidate.Left + candidate.Width / 2;
                    int clickY = (int)rect.Top + candidate.Top + candidate.Height / 2;
                    NativeMethods.Click(clickX, clickY, 1);
                    logger.Info(String.Format(
                        "已动态定位并点击智能字幕开始识别按钮一次: ({0},{1}); {2}",
                        clickX, clickY, lastDiagnostic));
                    return true;
                }
                Thread.Sleep(250);
            }
            logger.Warn("智能字幕面板内未找到唯一稳定的开始识别按钮: " + lastDiagnostic);
            return false;
        }

        public bool IsDynamicCaptionRecognitionButtonVisible(AutomationElement root)
        {
            if (root == null) return false;
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            System.Drawing.Rectangle ignored;
            string diagnostic;
            using (System.Drawing.Bitmap bitmap = CaptureRootBitmap(rect)) {
                return CaptionRecognitionButtonVision.TryFind(bitmap, out ignored, out diagnostic);
            }
        }

        private static System.Drawing.Bitmap CaptureRootBitmap(Rect rect)
        {
            int width = Math.Max(1, (int)Math.Round(rect.Width));
            int height = Math.Max(1, (int)Math.Round(rect.Height));
            var bitmap = new System.Drawing.Bitmap(width, height);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen((int)rect.Left, (int)rect.Top, 0, 0, bitmap.Size);
            }
            return bitmap;
        }

        public bool IsRelativeCyan(string action, AutomationElement root)
        {
            LocatorRule rule = config.GetRule(action);
            if (root == null || !rule.HasCoordinate) return false;
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            int x = (int)(rect.Left + rect.Width * rule.RelativeX);
            int y = (int)(rect.Top + rect.Height * rule.RelativeY);
            System.Drawing.Point ignored;
            return TryFindCyanCenter(x, y, out ignored);
        }

        private static bool TryFindCyanCenter(int x, int y, out System.Drawing.Point center)
        {
            const int halfWidth = 60;
            const int halfHeight = 32;
            int sumX = 0;
            int sumY = 0;
            int matches = 0;
            using (var bitmap = new System.Drawing.Bitmap(halfWidth * 2 + 1, halfHeight * 2 + 1))
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen(x - halfWidth, y - halfHeight, 0, 0, bitmap.Size);
                for (int scanY = 0; scanY < bitmap.Height; scanY += 2) {
                    for (int scanX = 0; scanX < bitmap.Width; scanX += 2) {
                        System.Drawing.Color color = bitmap.GetPixel(scanX, scanY);
                        if (color.G >= 125 && color.B >= 125 && color.R <= 100) {
                            sumX += scanX;
                            sumY += scanY;
                            matches++;
                        }
                    }
                }
            }
            if (matches < 10) {
                center = System.Drawing.Point.Empty;
                return false;
            }
            int clickX = x - halfWidth + sumX / matches;
            int clickY = y - halfHeight + sumY / matches;
            center = new System.Drawing.Point(clickX, clickY);
            return true;
        }

        public bool ClickCoordinateNow(string action, AutomationElement root)
        {
            LocatorRule rule = config.GetRule(action);
            if (root == null || !rule.HasCoordinate) return false;
            ClickRelative(root, rule, 1);
            logger.Info("使用校准坐标立即点击: " + action);
            return true;
        }

        public bool SetValue(string action, AutomationElement root, string value, int timeoutSeconds, CancellationToken token, bool required)
        {
            LocatorRule rule = config.GetRule(action);
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                AutomationElement element = FindByRule(rule, root);
                if (element != null) {
                    object pattern;
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) {
                        ((ValuePattern)pattern).SetValue(value);
                    } else {
                        ClickElement(element, 1);
                        PasteTextToForeground(value);
                    }
                    logger.Info("填写控件成功: " + action);
                    return true;
                }
                if (rule.HasCoordinate && watch.ElapsedMilliseconds >= 800) {
                    ClickRelative(root, rule, 1);
                    PasteTextToForeground(value);
                    logger.Info("使用校准坐标填写: " + action);
                    return true;
                }
                Thread.Sleep(250);
            }
            if (required) throw new InvalidOperationException("找不到输入框: " + action);
            return false;
        }

        public static void ClickElement(AutomationElement element, int count)
        {
            if (element == null) return;
            if (count == 1) {
                object invoke;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out invoke)) {
                    ((InvokePattern)invoke).Invoke();
                    return;
                }
                object selection;
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selection)) {
                    ((SelectionItemPattern)selection).Select();
                    return;
                }
            }
            Rect rect = element.Current.BoundingRectangle;
            NativeMethods.Click((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2), count);
        }

        public static string SafeName(AutomationElement element)
        {
            try { return element == null ? "" : (element.Current.Name ?? ""); }
            catch { return ""; }
        }

        private static void ClickRelative(AutomationElement root, LocatorRule rule, int count)
        {
            EnsureRootIsForeground(root);
            Rect rect = root.Current.BoundingRectangle;
            int x = (int)(rect.Left + rect.Width * rule.RelativeX);
            int y = (int)(rect.Top + rect.Height * rule.RelativeY);
            NativeMethods.Click(x, y, count);
        }

        private static void EnsureRootIsForeground(AutomationElement root)
        {
            if (root == null) throw new InvalidOperationException("坐标操作缺少目标窗口");
            int nativeHandle = root.Current.NativeWindowHandle;
            if (nativeHandle == 0) throw new InvalidOperationException("无法确认坐标操作的目标窗口，已阻止鼠标动作");
            IntPtr expected = new IntPtr(nativeHandle);
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != expected && !NativeMethods.BelongsToSameProcess(expected, foreground)) {
                throw new InvalidOperationException(String.Format("剪映不在前台，已阻止鼠标动作。目标={0}，前台={1}", expected, foreground));
            }
        }

        public static void PasteTextToForeground(string value)
        {
            Exception failure = null;
            for (int attempt = 0; attempt < 4; attempt++) {
                failure = null;
                var thread = new Thread(new ThreadStart(delegate {
                    try { Clipboard.SetText(value ?? ""); }
                    catch (Exception ex) { failure = ex; }
                }));
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                if (failure == null) {
                    NativeMethods.SendControlChord(NativeMethods.VK_A);
                    Thread.Sleep(80);
                    NativeMethods.SendControlChord(NativeMethods.VK_V);
                    return;
                }
                Thread.Sleep(120 * (attempt + 1));
            }

            // Clipboard ownership is frequently transient during remote-control
            // sessions.  Send UTF-16 key input directly as a safe final fallback
            // after selecting the already-focused field.
            NativeMethods.SendControlChord(NativeMethods.VK_A);
            Thread.Sleep(80);
            NativeMethods.SendUnicodeText(value ?? "");
        }
    }

public sealed class CapCutAutomation : ICapCutAutomation, IDraftTaskIdentity, ICaptionPlacementTask,
        IDirectDraftSubtitleStyling, ITaskScopedDraftRoot, ITimelineAssemblyCheckpointing,
        IDraftRootPreparation,
        IDraftCreationState, IDraftCreationPersistence, IResumableDraftCreation,
        IMediaImportPersistence, ICapCutSessionClosure
    {
        private readonly AppConfig config;
        private readonly AppLogger logger;
        private readonly ControlLocator locator;
        private Process process;
        // A production run must never close a user's already-visible editor.
        // Once the client starts its own fresh session, these IDs (plus
        // processes created during that session) are safe to close after export.
        private readonly HashSet<int> ownedSessionProcessIds = new HashSet<int>();
        private readonly HashSet<int> preLaunchSessionProcessIds = new HashSet<int>();
        private DateTime sessionStartedUtc = DateTime.MinValue;
        private bool sessionLaunchTimedOut;
        // True when this client had to start CapCut for the current session.
        // Cold starts need a longer editor transition budget than an already
        // warm CapCut process, but both budgets remain bounded.
        private bool sessionWasFreshlyLaunched;
        private AutomationElement mainWindow;
        private HashSet<string> draftBaseline;
        private string draftBaselineIndexHash = "";
        private string draftCreationPhase = "idle";
        private DraftCreationCheckpoint draftCreationCheckpoint;
        private Action<DraftCreationCheckpoint> draftCreationPersistence;
        private Action<string, string, string> mediaImportPersistence;
        private string activeImportCommandId = "";
        private string activeImportPath = "";
        private string activeImportPhase = "import_waiting";
        private string draftExpectedVideoPath;
        private double? directDraftCaptionTargetY;
        private string draftRoot;
        private string draftIndexPath;
        private double importedMainVideoSeconds;
        private double importedAudioSeconds;
        private double importedSampleSeconds;
        private string importedAudioPath;
        private string importedSamplePath;
        private readonly TimelineVision timelineVision;
        private readonly DraftTimelineAssemblyVerifier draftTimelineVerifier;
        private readonly DraftTimelineAssemblyRepairer draftTimelineRepairer;
        private DraftTimelineAssemblyResult timelineAssemblyResult;
        private string timelineAssemblyDirectory;
        private DirectDraftIdentity timelineAssemblyIdentity;
        private TimelineSnapshot sampleTimelineBefore;
        private int appendedSampleLeft;
        private int appendedSampleRight;
        // A visual confirmation failure must never make a step retry drag the
        // same narration into a second timeline lane.
        private bool audioPlacementAttempted;
        private readonly bool diagnosticFirstCaptionQNoOp;
        private string directDraftDirectory;
        private DirectDraftStyleResult directDraftStyleResult;
        private DirectDraftIdentity directDraftIdentity;
        private DateTime directDraftOpenAttemptUtc;
        private bool directDraftCardWasExplicitlyOpened;
        private bool lastCloseLeftOwnedProcesses;

        public bool LastCloseLeftOwnedProcesses { get { return lastCloseLeftOwnedProcesses; } }
        private Dictionary<string, DraftActivationStamp> directDraftActivationBaseline;
        private string directDraftActivatedDirectory;
        private bool directDraftStyleRequested;
        // A reopened draft can leave its homepage shell alive.  Keep the two
        // handles separate so later export operations never reactivate home.
        private IntPtr directDraftHomeHandle;
        private IntPtr directDraftEditorHandle;
        // Native editor binding is kept independently from the UIA root. CapCut
        // can expose a perfectly usable HWND while its UIA provider is still
        // loading (or temporarily wedged). The HWND/PID binding must survive
        // that interval so the draft transaction can be observed and resumed.
        private IntPtr boundEditorHandle;
        private int boundEditorProcessId;
        private string boundEditorClassName = "";
        private bool nativeEditorBound;
        private IntPtr activeExportHandle;
        private string lastDraftEditorProbeDiagnostic = "";
        private readonly TaskDraftRootLeaseManager draftRootLeaseManager;
        // UIA calls are only allowed for a candidate that has already passed
        // the cheap Win32/PID filter.  A busy CapCut QML surface can still
        // stall AutomationElement.FromHandle, so keep at most one bounded
        // probe per handle and never let it block the production thread.
        private readonly object editorProbeSync = new object();
        private readonly HashSet<IntPtr> editorProbeInFlight =
            new HashSet<IntPtr>();

        public CapCutAutomation(AppConfig config, AppLogger logger) : this(config, logger, false)
        {
        }

        public static string ReadClipboardText()
        {
            string value = "";
            var thread = new Thread(new ThreadStart(delegate {
                try { value = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch { value = ""; }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return value ?? "";
        }

        public CapCutAutomation(AppConfig config, AppLogger logger, bool diagnosticFirstCaptionQNoOp)
        {
            this.config = config;
            this.logger = logger;
            this.diagnosticFirstCaptionQNoOp = diagnosticFirstCaptionQNoOp;
            locator = new ControlLocator(config, logger);
            timelineVision = new TimelineVision(config, logger);
            draftTimelineVerifier = new DraftTimelineAssemblyVerifier();
            draftTimelineRepairer = new DraftTimelineAssemblyRepairer();
            draftRootLeaseManager = new TaskDraftRootLeaseManager(config, logger);
            draftRoot = ResolveDraftRoot();
        }

        public bool HasTaskDraftRootLease
        {
            get { return draftRootLeaseManager != null && draftRootLeaseManager.HasLease; }
        }

        public DraftRootLeaseRecord CurrentDraftRootLease
        {
            get { return draftRootLeaseManager == null ? null : draftRootLeaseManager.Current; }
        }

        public DraftRootPreparationResult CurrentDraftRootPreparation
        {
            get { return draftRootLeaseManager == null ? null :
                draftRootLeaseManager.CurrentPreparation; }
        }

        public string DraftCreationPhase
        {
            get { return draftCreationPhase ?? "idle"; }
        }

        public DraftCreationCheckpoint CaptureDraftCreationCheckpoint()
        {
            return draftCreationCheckpoint;
        }

        public void BindDraftCreationPersistence(Action<DraftCreationCheckpoint> callback)
        {
            draftCreationPersistence = callback;
            NotifyDraftCreationPersistence();
        }

        public void BindMediaImportPersistence(Action<string, string, string> callback,
            string restoredPhase, string restoredCommandId,
            string restoredImportPath)
        {
            mediaImportPersistence = callback;
            activeImportCommandId = restoredCommandId ?? "";
            activeImportPath = String.IsNullOrWhiteSpace(restoredImportPath)
                ? "" : Path.GetFullPath(restoredImportPath);
            activeImportPhase = String.IsNullOrWhiteSpace(restoredPhase)
                ? "import_waiting" : restoredPhase;
            if (!String.IsNullOrWhiteSpace(restoredPhase))
                logger.Info("已恢复媒体导入子事务: phase=" + restoredPhase +
                    "; command=" + (activeImportCommandId ?? "") +
                    "; media=" + Path.GetFileName(activeImportPath ?? ""));
        }

        private void NotifyMediaImportPersistence(string phase)
        {
            try {
                activeImportPhase = phase ?? "import_waiting";
                if (mediaImportPersistence != null)
                    mediaImportPersistence(activeImportPhase,
                        activeImportCommandId ?? "", activeImportPath ?? "");
            } catch (Exception ex) {
                logger.Warn("导入状态写入失败（不阻塞媒体操作）: " + ex.Message);
            }
        }

        public void RestoreDraftCreationCheckpoint(DraftCreationCheckpoint checkpoint)
        {
            if (checkpoint == null) return;
            draftCreationCheckpoint = checkpoint;
            draftCreationPhase = String.IsNullOrWhiteSpace(checkpoint.CreationPhase)
                ? "baseline_captured" : checkpoint.CreationPhase;
            draftBaseline = new HashSet<string>(
                (checkpoint.BaselineDraftIds ?? new string[0]).Select(path => {
                    try { return Path.GetFullPath(path); } catch { return path; }
                }), StringComparer.OrdinalIgnoreCase);
            draftBaselineIndexHash = checkpoint.BaselineIndexHash ?? "";
        }

        public void ResumeDraftCreation(int timeoutSeconds, CancellationToken token)
        {
            if (draftCreationCheckpoint == null)
                throw new CapCutOperationFailure("draft_creation",
                    "CAPCUT_DRAFT_CREATION_CHECKPOINT_MISSING",
                    CapCutFailureClassification.HardSafety,
                    "没有可恢复的创建检查点");
            if (!String.IsNullOrWhiteSpace(draftCreationCheckpoint.DiscoveredDraftPath)) {
                directDraftDirectory = ResolveImportDraftDirectory();
                directDraftIdentity = ReadDirectDraftIdentityWithRetry(directDraftDirectory, 10, token);
                if (directDraftIdentity == null || !String.Equals(directDraftIdentity.DraftId,
                    draftCreationCheckpoint.DiscoveredDraftId, StringComparison.OrdinalIgnoreCase))
                    throw new CapCutOperationFailure("draft_creation", "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH",
                        CapCutFailureClassification.HardSafety, "创建断点与实际草稿身份不一致");
                recoveringCreatedDraft = true;
                try {
                    if (!HasLockedReopenSourceEvidence(directDraftDirectory))
                        throw new CapCutOperationFailure("draft_creation", "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH",
                            CapCutFailureClassification.HardSafety, "恢复草稿未通过空时间线及素材身份校验");
                    CaptureDirectDraftActivationBaseline();
                    OpenDirectStyledDraft(Math.Max(60, timeoutSeconds), token);
                } finally { recoveringCreatedDraft = false; }
                draftCreationPhase = "creation_completed";
                draftCreationCheckpoint.CreationPhase = draftCreationPhase;
                draftCreationCheckpoint.VerifiedAt = DateTime.Now.ToString("o");
                NotifyDraftCreationPersistence();
                logger.Info("已明确打开并验证创建断点草稿；磁盘存在不再替代界面激活证据");
                return;
            }
            IntPtr resumeHomeHandle = IntPtr.Zero;
            try {
                RefreshWindow();
                if (ClassifyCapCutWindow(mainWindow) == CapCutUiState.Home)
                    resumeHomeHandle = GetLockedWindowHandle();
            } catch (InvalidOperationException) {
                // A persisted create command commonly resumes while CapCut is
                // replacing its landing window.  The bounded transition gate
                // below owns rediscovery and process-exit classification.
            }
            mainWindow = null;
            WaitForDraftCreationReady(token, Math.Max(30, timeoutSeconds), resumeHomeHandle, false);
            EnsureBoundEditorAutomationRoot(token, "恢复草稿创建后");
            draftCreationPhase = "creation_completed";
            draftCreationCheckpoint.CreationPhase = draftCreationPhase;
            draftCreationCheckpoint.VerifiedAt = DateTime.Now.ToString("o");
            NotifyDraftCreationPersistence();
        }

        private void NotifyDraftCreationPersistence()
        {
            Action<DraftCreationCheckpoint> callback = draftCreationPersistence;
            if (callback == null || draftCreationCheckpoint == null) return;
            try { callback(draftCreationCheckpoint); } catch (Exception ex) {
                logger.Warn("CapCut创建检查点回调失败（不改变UI操作）: " + ex.Message);
            }
        }

        public void AcquireTaskDraftRoot(VideoTask task, CancellationToken token)
        {
            if (draftRootLeaseManager == null || !draftRootLeaseManager.IsEnabled) return;
            Process visibleEditor = FindProcessWithUsableMainWindow();
            if (visibleEditor != null) {
                try {
                    throw new CapCutOperationFailure("lease",
                        "CAPCUT_USER_CAPCUT_WINDOW_OCCUPIED",
                        CapCutFailureClassification.UserActionRequired,
                        "检测到用户正在使用CapCut编辑器（PID " + visibleEditor.Id + "）");
                } finally {
                    try { visibleEditor.Dispose(); } catch { }
                }
            }
            TerminateHeadlessCapCutProcesses(12, token);
            DraftRootLeaseRecord lease = draftRootLeaseManager.Acquire(task, token);
            if (lease == null) return;
            draftRoot = lease.TaskDraftRoot;
            draftIndexPath = Path.Combine(draftRoot, "root_meta_info.json");
            boundEditorHandle = IntPtr.Zero;
            boundEditorProcessId = 0;
            boundEditorClassName = "";
            nativeEditorBound = false;
            directDraftEditorHandle = IntPtr.Zero;
            directDraftDirectory = null;
            directDraftStyleResult = null;
            directDraftIdentity = null;
            directDraftActivationBaseline = null;
            directDraftActivatedDirectory = null;
            logger.Info("CapCut will see only the current task draft root: " + lease.LeaseId);
        }

        public void UpdateTaskDraftRootStage(string stage)
        {
            if (draftRootLeaseManager == null || !draftRootLeaseManager.HasLease) return;
            draftRootLeaseManager.Touch(stage);
        }

        public void ReleaseTaskDraftRoot(CancellationToken token)
        {
            if (draftRootLeaseManager == null || !draftRootLeaseManager.HasLease) return;
            RegisterOwnedSessionProcesses();
            Stopwatch watch = Stopwatch.StartNew();
            while (CountOwnedSessionProcesses() > 0 &&
                watch.Elapsed < TimeSpan.FromSeconds(20)) {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(250);
                RegisterOwnedSessionProcesses();
            }
            List<Process> remainingOwned = GetConfiguredCapCutProcesses()
                .Where(IsOwnedSessionProcess).ToList();
            try {
                if (remainingOwned.Count > 0)
                    throw new InvalidOperationException(
                        "CAPCUT_OWNED_PROCESS_EXIT_TIMEOUT: this task's CapCut process is still running; draft directory recovery is pending");
            } finally {
                foreach (Process item in remainingOwned) {
                    try { item.Dispose(); } catch { }
                }
            }
            List<Process> anyRemaining = GetConfiguredCapCutProcesses();
            try {
                if (anyRemaining.Count > 0)
                    throw new InvalidOperationException(
                        "CAPCUT_DRAFT_ROOT_RELEASE_PENDING: CapCut is still running; original draft directory recovery is pending");
            } finally {
                foreach (Process item in anyRemaining) {
                    try { item.Dispose(); } catch { }
                }
            }
            draftRootLeaseManager.Release(token);
            draftRoot = ResolveDraftRoot();
            directDraftDirectory = null;
            directDraftStyleResult = null;
            directDraftIdentity = null;
            directDraftActivationBaseline = null;
            directDraftActivatedDirectory = null;
        }

        private int CountConfiguredCapCutProcesses()
        {
            List<Process> processes = GetConfiguredCapCutProcesses();
            try { return processes.Count; }
            finally {
                foreach (Process process in processes) {
                    try { process.Dispose(); } catch { }
                }
            }
        }

        private int CountOwnedSessionProcesses()
        {
            List<Process> processes = GetConfiguredCapCutProcesses();
            try { return processes.Count(IsOwnedSessionProcess); }
            finally {
                foreach (Process process in processes) {
                    try { process.Dispose(); } catch { }
                }
            }
        }

        public void SetDraftTaskIdentity(string videoPath)
        {
            draftExpectedVideoPath = NormalizeDraftMediaPath(videoPath);
            audioPlacementAttempted = false;
            importedAudioPath = "";
            importedSamplePath = "";
            timelineAssemblyResult = null;
            timelineAssemblyDirectory = null;
            timelineAssemblyIdentity = null;
            directDraftCaptionTargetY = null;
            directDraftDirectory = null;
            directDraftStyleResult = null;
            directDraftIdentity = null;
            directDraftOpenAttemptUtc = DateTime.MinValue;
            directDraftStyleRequested = false;
            directDraftActivationBaseline = null;
            directDraftActivatedDirectory = null;
            directDraftHomeHandle = IntPtr.Zero;
            directDraftEditorHandle = IntPtr.Zero;
            activeExportHandle = IntPtr.Zero;
            if (String.IsNullOrWhiteSpace(draftExpectedVideoPath)) {
                throw new InvalidOperationException("当前任务缺少可用于草稿核验的源视频路径，已阻止草稿清理");
            }
            logger.Info("已记录本轮草稿源视频身份: " + Path.GetFileName(videoPath));
        }

        public void SetCaptionPlacement(string placement, double targetY)
        {
            directDraftCaptionTargetY = String.Equals(
                placement ?? "", "original_band", StringComparison.OrdinalIgnoreCase)
                ? (double?)Math.Max(-0.82, Math.Min(0.75, targetY))
                : null;
            logger.Info(directDraftCaptionTargetY.HasValue
                ? String.Format(CultureInfo.InvariantCulture,
                    "字幕位置策略: 已验证去字幕，移动到原字幕带 y={0:0.000}",
                    directDraftCaptionTargetY.Value)
                : "字幕位置策略: 模糊兜底或缺少可靠字幕带，保留公司默认位置");
        }

        public void LaunchOrAttach(int timeoutSeconds, CancellationToken token)
        {
            // The isolated laboratory must never take over an editor that a user
            // can actually see.  CapCut does, however, leave headless helper
            // processes behind after an export/session close.  Treating those as
            // a user editor made draft-reopen verification fail before it ever
            // reached the locked draft.  We therefore block only a real editor
            // window and otherwise clear headless residual processes before a
            // fresh isolated launch.
            sessionWasFreshlyLaunched = false;
            CaptureSessionProcessBaselineIfNeeded();
            RegisterOwnedSessionProcesses();
            bool forceFreshLabLaunch = false;
            bool adoptedOwnedSession = false;
            Process visibleEditor = FindProcessWithUsableMainWindow();
            if (visibleEditor != null) {
                if (IsOwnedSessionProcess(visibleEditor)) {
                    process = visibleEditor;
                    ownedSessionProcessIds.Add(visibleEditor.Id);
                    adoptedOwnedSession = true;
                    logger.Warn("CAPCUT_DELAYED_OWNED_PROCESS_ADOPTED: a CapCut window appeared after the initial wait and remains owned by this task (PID " +
                        visibleEditor.Id + ")");
                } else {
                    try {
                        throw new InvalidOperationException(
                            "A visible CapCut editor already existed before this batch session (PID " + visibleEditor.Id +
                            "); batch launch stopped to protect the user's draft.");
                    } finally {
                        try { visibleEditor.Dispose(); } catch { }
                    }
                }
            }
            if (!adoptedOwnedSession && sessionStartedUtc != DateTime.MinValue) {
                Process delayed = FindOwnedSessionProcess();
                if (delayed != null) {
                    process = delayed;
                    adoptedOwnedSession = true;
                    logger.Info("Reattached to this task's delayed CapCut process before retry: PID " + delayed.Id);
                }
            }
            if (!adoptedOwnedSession) {
                Process existingCandidate = FindRunningProcess();
                if (existingCandidate != null) {
                    try {
                        logger.Warn("Only headless CapCut residual processes were found before launch; they will be closed safely (PID " +
                            existingCandidate.Id + ")");
                    } finally {
                        try { existingCandidate.Dispose(); } catch { }
                    }
                }
                TerminateHeadlessCapCutProcesses(
                    RuntimeModes.IsolatedLabRuntime ? timeoutSeconds : 12, token);
                forceFreshLabLaunch = true;
            }
            process = forceFreshLabLaunch ? null : FindRunningProcess();
            if (process == null) {
                string executable = ResolveExecutable();
                if (String.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) {
                    throw new FileNotFoundException("未找到剪映/CapCut。请在 capcut.config.json 设置 CapCutPath，或在界面中选择程序路径。");
                }
                logger.Info("启动程序: " + executable);
                var startInfo = new ProcessStartInfo(executable) {
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    Arguments = config.CapCutArguments ?? "",
                    UseShellExecute = true
                };
                sessionStartedUtc = DateTime.UtcNow;
                sessionLaunchTimedOut = false;
                sessionWasFreshlyLaunched = true;
                process = Process.Start(startInfo);
                if (process != null) ownedSessionProcessIds.Add(process.Id);
                Thread.Sleep(1200);
                if (FindRunningProcess() == null) {
                    logger.Warn("直接启动后未检测到剪映进程，改用 Windows 命令外壳启动");
                    string shellArguments = String.Format(
                        "/d /c start \"\" \"{0}\" {1}",
                        executable,
                        config.CapCutArguments ?? "");
                    string commandShell = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                    Process.Start(new ProcessStartInfo(commandShell, shellArguments) {
                        WorkingDirectory = Path.GetDirectoryName(executable),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
            } else {
                logger.Info("已连接正在运行的程序: " + process.ProcessName);
            }

            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                RegisterOwnedSessionProcesses();
                DismissEnvironmentCheckWindow();
                // Use native handle/PID filtering first.  The old locator
                // enumerated every UIA top-level window on every 500ms poll;
                // a busy CapCut QML tree could make this look like a launch
                // hang and also delayed Stop() from releasing the lease.
                mainWindow = FindOwnedCapCutWindowNative();
                if (mainWindow != null) {
                    int handle = mainWindow.Current.NativeWindowHandle;
                    if (handle != 0) NativeMethods.ActivateWindow(new IntPtr(handle));
                    RegisterOwnedSessionProcesses();
                    sessionLaunchTimedOut = false;
                    return;
                }
                try {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero) {
                        NativeMethods.ActivateWindow(process.MainWindowHandle);
                        mainWindow = locator.ElementFromWindow(process.MainWindowHandle);
                        if (mainWindow != null) {
                            RegisterOwnedSessionProcesses();
                            sessionLaunchTimedOut = false;
                            return;
                        }
                    }
                } catch { }
                Thread.Sleep(500);
            }
            RegisterOwnedSessionProcesses();
            sessionLaunchTimedOut = true;
            throw new TimeoutException("等待剪映主窗口超时");
        }

        public void CreateProject(int timeoutSeconds, CancellationToken token)
        {
            CaptureDraftBaseline();
            draftCreationPhase = "baseline_captured";
            draftCreationCheckpoint = CreateDraftCreationCheckpoint();
            NotifyDraftCreationPersistence();
            RefreshWindow();
            // A completed export can leave CapCut on its full-size success
            // page.  It is in the same process as the editor and therefore
            // is not a normal popup; close it before trying to create the
            // next independent draft.
            CloseExportCompletionWindow(5, token);
            RefreshWindow();
            FocusMainWindow();
            IntPtr ownedHomeHandle = GetLockedWindowHandle();
            CloseForegroundCapCutPopup();
            SleepChecked(500, token);
            Stopwatch watch = Stopwatch.StartNew();
            bool started = false;
            bool returnedFromEditor = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                FocusForCoordinateAction(token);
                if (ClassifyCapCutWindow(mainWindow) == CapCutUiState.Home) {
                    // Commit the intent before the actual mouse click.  A
                    // process crash after this point must reconcile the
                    // baseline instead of clicking the button again.
                    draftCreationPhase = "create_intent_committed";
                    if (draftCreationCheckpoint == null)
                        draftCreationCheckpoint = CreateDraftCreationCheckpoint();
                    draftCreationCheckpoint.CreationPhase = draftCreationPhase;
                    draftCreationCheckpoint.CreateCommandIssuedAt = DateTime.Now.ToString("o");
                    draftCreationCheckpoint.CreateCommandId = Guid.NewGuid().ToString("N");
                    NotifyDraftCreationPersistence();
                    if (locator.ClickRelativeIfCyan("CreateProject", mainWindow)) {
                    logger.Info("检测到首页“开始创作”区域并点击");
                    draftCreationPhase = "create_command_sent";
                    draftCreationCheckpoint.CreationPhase = draftCreationPhase;
                    started = true;
                    NotifyDraftCreationPersistence();
                    break;
                    }
                    draftCreationPhase = "baseline_captured";
                    draftCreationCheckpoint.CreationPhase = draftCreationPhase;
                    NotifyDraftCreationPersistence();
                }
                if (!returnedFromEditor && watch.Elapsed >= TimeSpan.FromSeconds(2)) {
                    logger.Info("当前不是 CapCut 首页，使用 Ctrl+Alt+Q 返回草稿页后再创建新项目");
                    NativeMethods.PressEscape();
                    SleepChecked(250, token);
                    NativeMethods.PressEscape();
                    SleepChecked(250, token);
                    NativeMethods.SendControlAltChord(NativeMethods.VK_Q);
                    returnedFromEditor = true;
                    SleepChecked(1800, token);
                    mainWindow = null;
                    RefreshWindow();
                    CloseForegroundCapCutPopup();
                    SleepChecked(500, token);
                    continue;
                }
                SleepChecked(1000, token);
            }
            if (!started) throw new TimeoutException("CapCut 首页内容未加载，未检测到“开始创作”，已阻止盲点操作");
            // Clicking Start creating normally destroys/replaces the landing
            // window.  Persist that transition before looking for the editor:
            // a stale UIA handle is expected here and is not a task failure.
            draftCreationPhase = "editor_window_transition";
            draftCreationCheckpoint.CreationPhase = draftCreationPhase;
            NotifyDraftCreationPersistence();
            mainWindow = null;
            WaitForDraftCreationReady(token, Math.Max(30, timeoutSeconds), ownedHomeHandle,
                sessionWasFreshlyLaunched);
            // Native HWND/PID evidence is persisted independently from the
            // UIA root. Attach the root only after the draft transition has
            // completed, when CapCut's QML provider is normally responsive.
            EnsureBoundEditorAutomationRoot(token, "创建草稿后");
            CloseIsolatedOwnedHomeShellAfterEditorLaunch(ownedHomeHandle, token);
            draftCreationPhase = "creation_completed";
            if (draftCreationCheckpoint == null)
                draftCreationCheckpoint = CreateDraftCreationCheckpoint();
            draftCreationCheckpoint.CreationPhase = draftCreationPhase;
            draftCreationCheckpoint.VerifiedAt = DateTime.Now.ToString("o");
            NotifyDraftCreationPersistence();
            CaptureMainWindow("capcut-after-create-project.png");
        }

        private void WaitForDraftCreationReady(CancellationToken token,
            int timeoutSeconds, IntPtr homeHandle, bool coldStart)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int stableReads = 0;
            string lastDiagnostic = "";
            bool sawIndex = false;
            bool sawNewDraft = false;
            bool sawMaterializedNewDraft = false;
            bool sawEditorWindow = false;
            DateTime noProcessSinceUtc = DateTime.MinValue;
            string lastIndexHash = "";
            string previousIndexSemanticSignature = "";
            string previousNewDraftPath = "";
            int stableNewDraftReads = 0;
            int budget = CapCutHomeShellPolicy.DraftCreationTransitionTimeoutSeconds(
                timeoutSeconds, coldStart);
            logger.Info("CapCut 草稿创建门禁开始等待编辑器切换: " + budget +
                "s，coldStart=" + coldStart);
            while (watch.Elapsed < TimeSpan.FromSeconds(budget)) {
                token.ThrowIfCancellationRequested();
                // File-system evidence is checked before UIA. A new draft is
                // the durable fact; window semantics are only a binding gate.
                bool editorBound = (nativeEditorBound && IsNativeEditorBindingAlive()) ||
                    TryBindDraftCreationEditor(homeHandle);
                if (editorBound) {
                    sawEditorWindow = true;
                    noProcessSinceUtc = DateTime.MinValue;
                } else if (CountConfiguredCapCutProcesses() == 0) {
                    if (noProcessSinceUtc == DateTime.MinValue)
                        noProcessSinceUtc = DateTime.UtcNow;
                    if (DateTime.UtcNow - noProcessSinceUtc >= TimeSpan.FromSeconds(2))
                        throw new CapCutOperationFailure("draft_creation",
                            "CAPCUT_PROCESS_EXITED_DURING_CREATE",
                            CapCutFailureClassification.Transient,
                            "开始创作命令已发送，但本轮CapCut进程已经退出");
                } else {
                    noProcessSinceUtc = DateTime.MinValue;
                }
                string hardError;
                if (TryFindCapCutDraftPathError(out hardError)) {
                    CaptureMainWindow("capcut-draft-path-error.png");
                    throw new InvalidOperationException(
                        "CAPCUT_DRAFT_PATH_OPEN_FAILED: " + hardError);
                }

                DraftRootLeaseRecord lease = draftRootLeaseManager == null
                    ? null : draftRootLeaseManager.Current;
                if (lease == null || String.IsNullOrWhiteSpace(lease.ActiveDraftRoot)) {
                    lastDiagnostic = "active draft lease is not available";
                    SleepChecked(300, token);
                    continue;
                }
                string active = Path.GetFullPath(lease.ActiveDraftRoot);
                try {
                    if (!Directory.Exists(active))
                        throw new CapCutOperationFailure("draft_creation",
                            "CAPCUT_ACTIVE_DRAFT_ROOT_MISSING",
                            CapCutFailureClassification.HardSafety,
                            "活动草稿目录不存在: " + active);
                    if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0)
                        throw new CapCutOperationFailure("draft_creation",
                            "CAPCUT_ACTIVE_DRAFT_ROOT_REPARSE_POINT_BLOCKED",
                            CapCutFailureClassification.HardSafety,
                            "活动草稿目录不能是联接或符号链接: " + active);
                    DraftRootOwnershipValidationResult ownership =
                        draftRootLeaseManager.ValidateActiveRootOwnership(lease);
                    if (!ownership.IsValid)
                        throw new CapCutOperationFailure("draft_creation",
                            ownership.ErrorCode,
                            CapCutFailureClassification.HardSafety,
                            ownership.Diagnostic);

                    string index = Path.Combine(active, "root_meta_info.json");
                    if (!File.Exists(index)) {
                        lastDiagnostic = "root_meta_info.json is not present";
                        stableReads = 0;
                        SleepChecked(300, token);
                        continue;
                    }
                    string currentIndex = File.ReadAllText(index, Encoding.UTF8);
                    sawIndex = true;
                    lastIndexHash = HashText(currentIndex);

                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Dictionary<string, object> root = serializer.DeserializeObject(
                        currentIndex) as Dictionary<string, object>;
                    object rawStores;
                    object[] stores = root != null && root.TryGetValue(
                        "all_draft_store", out rawStores)
                        ? ObjectArrayValue(rawStores) : new object[0];
                    // TryBindDraftCreationEditor already performed the
                    // bounded role/semantic classification for this exact
                    // handle.  Re-running the full QML classifier here can
                    // block on the same busy editor and turn a valid bind
                    // into a false timeout.
                    bool editorVisible = editorBound &&
                        (mainWindow != null || nativeEditorBound);
                    bool foldersReady = stores.Length > 0;
                    string[] indexedDraftPaths = stores.Select(value => {
                        Dictionary<string, object> store = value as Dictionary<string, object>;
                        string folder = store == null ? "" : TextValue(store, "draft_fold_path");
                        return String.IsNullOrWhiteSpace(folder) ? "" :
                            Path.GetFullPath(Path.IsPathRooted(folder)
                                ? folder : Path.Combine(active, folder));
                    }).Where(path => !String.IsNullOrWhiteSpace(path)).ToArray();
                    foreach (object value in stores) {
                        Dictionary<string, object> store = value as Dictionary<string, object>;
                        string folder = store == null ? "" : TextValue(store, "draft_fold_path");
                        if (String.IsNullOrWhiteSpace(folder)) {
                            foldersReady = false;
                            break;
                        }
                        string folderPath = Path.IsPathRooted(folder) ? folder :
                            Path.Combine(active, folder);
                        if (!Directory.Exists(folderPath)) {
                            foldersReady = false;
                            break;
                        }
                    }
                    // CapCut rewrites root_meta_info.json while the newly
                    // created card is settling (tm_draft_modified,
                    // timeline-material size, and other bookkeeping fields
                    // can change on every read).  Comparing the raw JSON made
                    // a valid editor remain at stableReads=1 until the gate
                    // timed out.  Stability here means that the registered
                    // draft identity/path set is stable, not that mutable
                    // bookkeeping timestamps have stopped moving.
                    string indexSemanticSignature = BuildDraftIndexSemanticSignature(
                        stores, active);
                    if (String.Equals(previousIndexSemanticSignature,
                        indexSemanticSignature, StringComparison.Ordinal))
                        stableReads++;
                    else stableReads = 1;
                    previousIndexSemanticSignature = indexSemanticSignature;
                    bool uniqueNewDraftReady = false;
                    string uniqueNewDraftPath = "";
                    if (draftCreationCheckpoint != null && indexedDraftPaths.Length > 0) {
                        string discovered = indexedDraftPaths.FirstOrDefault(path =>
                            draftBaseline == null || !draftBaseline.Contains(path));
                        string[] newDrafts = indexedDraftPaths
                            .Where(path => draftBaseline == null || !draftBaseline.Contains(path))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        // A released lease can restore the same discovered
                        // draft under a new active-root path. Its path is no
                        // longer in the old baseline, but its stable draft ID
                        // is. Treat that as an existing committed result,
                        // never as a reason to click Start again.
                        if (newDrafts.Length == 0 &&
                            draftCreationCheckpoint != null &&
                            !String.IsNullOrWhiteSpace(draftCreationCheckpoint.DiscoveredDraftId)) {
                            string knownPath = stores
                                .Select(value => value as Dictionary<string, object>)
                                .Where(store => store != null &&
                                    String.Equals(TextValue(store, "draft_id"),
                                        draftCreationCheckpoint.DiscoveredDraftId,
                                        StringComparison.OrdinalIgnoreCase))
                                .Select(store => TextValue(store, "draft_fold_path"))
                                .Where(folder => !String.IsNullOrWhiteSpace(folder))
                                .Select(folder => Path.GetFullPath(Path.IsPathRooted(folder)
                                    ? folder : Path.Combine(active, folder)))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .SingleOrDefault();
                            if (!String.IsNullOrWhiteSpace(knownPath)) {
                                newDrafts = new[] { knownPath };
                                discovered = knownPath;
                            }
                        }
                        if (newDrafts.Length > 1)
                            throw new CapCutOperationFailure("draft_creation",
                                "CAPCUT_DRAFT_CREATION_MULTIPLE_DRAFTS_BLOCKED",
                                CapCutFailureClassification.HardSafety,
                                "创建命令产生多个新增草稿，已停止自动选择");
                        if (newDrafts.Length == 1) {
                            sawNewDraft = true;
                            uniqueNewDraftPath = newDrafts[0];
                            if (String.Equals(previousNewDraftPath,
                                uniqueNewDraftPath, StringComparison.OrdinalIgnoreCase))
                                stableNewDraftReads++;
                            else stableNewDraftReads = 1;
                            previousNewDraftPath = uniqueNewDraftPath;
                            // A draft card can be registered before its first
                            // content file is written.  Do not accept that
                            // transient state; the content file is the
                            // minimum proof that the editor has materialized
                            // the newly-created draft.
                            string draftContentPath = Path.Combine(
                                uniqueNewDraftPath, "draft_content.json");
                            uniqueNewDraftReady = File.Exists(draftContentPath) &&
                                new FileInfo(draftContentPath).Length > 0;
                            if (uniqueNewDraftReady) sawMaterializedNewDraft = true;
                        } else {
                            previousNewDraftPath = "";
                            stableNewDraftReads = 0;
                        }
                        if (!String.IsNullOrWhiteSpace(discovered) && newDrafts.Length == 1) {
                            draftCreationPhase = "draft_discovered";
                            draftCreationCheckpoint.CreationPhase = draftCreationPhase;
                            draftCreationCheckpoint.DiscoveredDraftPath = newDrafts[0];
                            Dictionary<string, object> discoveredStore = stores
                                .Select(value => value as Dictionary<string, object>)
                                .FirstOrDefault(store => {
                                    string folder = store == null ? "" : TextValue(store, "draft_fold_path");
                                    if (String.IsNullOrWhiteSpace(folder)) return false;
                                    string full = Path.GetFullPath(Path.IsPathRooted(folder)
                                        ? folder : Path.Combine(active, folder));
                                    return String.Equals(full, newDrafts[0], StringComparison.OrdinalIgnoreCase);
                                });
                            draftCreationCheckpoint.DiscoveredDraftId = discoveredStore == null
                                ? "" : TextValue(discoveredStore, "draft_id");
                            NotifyDraftCreationPersistence();
                        }
                    }
                    lastDiagnostic = "stores=" + stores.Length +
                        ", stableReads=" + stableReads +
                        ", newDraftReads=" + stableNewDraftReads +
                        ", draftContent=" + uniqueNewDraftReady +
                        ", editor=" + editorVisible;
                    if (!String.IsNullOrWhiteSpace(lastDraftEditorProbeDiagnostic))
                        lastDiagnostic += ", probe=" + lastDraftEditorProbeDiagnostic;
                    // UIA discovery of the CapCut editor can consume most of
                    // the polling budget because the window exposes a very
                    // large descendant tree.  Once the same unique draft has
                    // been discovered, its content is non-empty, and the
                    // editor/root ownership gates have passed, a second raw
                    // polling turn is not a safety requirement.  The later
                    // media-stage gates perform the strict identity checks.
                    bool creationEvidenceStable = stableReads >= 2 ||
                        (stableNewDraftReads >= 1 && uniqueNewDraftReady);
                    if (creationEvidenceStable && editorVisible && foldersReady) {
                        logger.Info("CapCut 草稿创建门禁通过：普通活动目录、根索引连续稳定且草稿目录已登记（" +
                            lastDiagnostic + "）");
                        return;
                    }
                    } catch (CapCutOperationFailure) { throw; }
                    catch (InvalidOperationException) { throw; }
                catch (Exception ex) {
                    lastDiagnostic = ex.Message;
                    stableReads = 0;
                }
                SleepChecked(300, token);
            }
            if (GetLockedWindowHandle() != IntPtr.Zero)
                CaptureMainWindow("capcut-draft-creation-timeout.png");
            else if (homeHandle != IntPtr.Zero)
                CaptureWindowHandle(homeHandle, "capcut-draft-creation-timeout.png");
            else
                logger.Warn("草稿创建超时时没有可截图的CapCut窗口句柄");
            string timeoutCode = sawMaterializedNewDraft
                ? "CAPCUT_DRAFT_CREATED_EDITOR_PENDING"
                : sawNewDraft
                ? "CAPCUT_EDITOR_WINDOW_NOT_READY"
                : !sawEditorWindow
                ? "CAPCUT_MAIN_WINDOW_TRANSITION_TIMEOUT"
                : sawIndex && !sawNewDraft &&
                draftCreationCheckpoint != null &&
                String.Equals(lastIndexHash, draftCreationCheckpoint.BaselineIndexHash,
                    StringComparison.OrdinalIgnoreCase)
                ? "CAPCUT_DRAFT_CREATION_COMMAND_UNCONFIRMED"
                : "CAPCUT_DRAFT_INDEX_NOT_STABLE";
            CapCutFailureClassification timeoutClassification = timeoutCode ==
                "CAPCUT_DRAFT_CREATION_COMMAND_UNCONFIRMED"
                ? CapCutFailureClassification.HardSafety
                : CapCutFailureClassification.Transient;
            throw new CapCutOperationFailure("draft_creation", timeoutCode,
                timeoutClassification, "草稿创建门禁超时；" + lastDiagnostic);
        }

        private void EnsureBoundEditorAutomationRoot(CancellationToken token, string phase)
        {
            if (mainWindow != null && IsBoundEditorWindowUsable()) return;
            IntPtr handle = boundEditorHandle != IntPtr.Zero
                ? boundEditorHandle : directDraftEditorHandle;
            if (handle == IntPtr.Zero)
                throw new CapCutOperationFailure("draft_creation",
                    "CAPCUT_EDITOR_BINDING_MISSING", CapCutFailureClassification.HardSafety,
                    phase + "没有已验证的编辑器句柄");
            for (int attempt = 0; attempt < 3; attempt++) {
                token.ThrowIfCancellationRequested();
                AutomationElement root = TryGetWindowElementBounded(handle, 2000);
                if (root != null) {
                    mainWindow = root;
                    directDraftEditorHandle = handle;
                    return;
                }
                Thread.Sleep(250);
            }
            throw new CapCutOperationFailure("draft_creation",
                "CAPCUT_EDITOR_PROBE_TIMEOUT", CapCutFailureClassification.Transient,
                phase + "已找到CapCut编辑器窗口，但UIA根节点在限定时间内不可用；" +
                "handle=" + handle);
        }

        private bool IsNativeEditorBindingAlive()
        {
            if (!nativeEditorBound || boundEditorHandle == IntPtr.Zero) return false;
            try {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(boundEditorHandle, out pid);
                return pid != 0 && pid == (uint)boundEditorProcessId &&
                    NativeMethods.IsWindowVisible(boundEditorHandle) &&
                    !NativeMethods.IsHungAppWindow(boundEditorHandle) &&
                    IsConfiguredCapCutProcess((int)pid);
            } catch { return false; }
        }

        private bool TryBindDraftCreationEditor(IntPtr homeHandle)
        {
            lastDraftEditorProbeDiagnostic = "";
            try {
                var candidates = new List<Tuple<IntPtr, string, string, CapCutWindowRole>>();
                bool materializedDraft = HasMaterializedNewDraftEvidence();
                IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
                int visibleCandidates = 0;
                int ownedCandidates = 0;
                int homeCandidates = 0;
                int transitionCandidates = 0;
                int editorCandidates = 0;
                var observed = new List<string>();
                var rejected = new List<string>();
                foreach (IntPtr windowHandle in NativeMethods.GetTopLevelWindowHandles()) {
                    try {
                        IntPtr handle = windowHandle;
                        uint nativePid;
                        NativeMethods.GetWindowThreadProcessId(handle, out nativePid);
                        if (nativePid == 0 || !IsConfiguredCapCutProcess((int)nativePid))
                            continue;
                        visibleCandidates++;
                        if (!IsOwnedSessionProcess((int)nativePid)) continue;
                        ownedCandidates++;
                        if (NativeMethods.IsHungAppWindow(handle)) { rejected.Add(handle + ":hung"); continue; }
                        NativeMethods.RECT nativeBounds;
                        if (!NativeMethods.GetWindowRect(handle, out nativeBounds) ||
                            nativeBounds.Right - nativeBounds.Left < 640 ||
                            nativeBounds.Bottom - nativeBounds.Top < 400) { rejected.Add(handle + ":small"); continue; }
                        string className = NativeMethods.GetWindowClassName(handle);
                        string title = NativeMethods.GetWindowTitle(handle);
                        CapCutWindowRole role = CapCutHomeShellPolicy.Classify(className);
                        if (role == CapCutWindowRole.Home) {
                            homeCandidates++;
                            // The landing page can morph into the editor in
                            // place.  It is rejected while it is still a
                            // confirmed home surface and reconsidered after
                            // the class/semantic role changes.
                            continue;
                        }
                        if (role == CapCutWindowRole.Transition) {
                            transitionCandidates++;
                            continue;
                        }
                        // MainWindow_QMLTYPE_* is CapCut's editor shell.  Do
                        // not enumerate its descendant tree merely to decide
                        // whether it is an editor; that was the source of the
                        // apparent 60-second freeze.  Unknown classes are
                        // probed once, with a hard timeout, only after the
                        // native process/window checks above have passed.
                        bool draftTitle = !String.IsNullOrWhiteSpace(title) &&
                            !title.Equals("CapCut", StringComparison.OrdinalIgnoreCase) &&
                            !title.Equals("剪映", StringComparison.OrdinalIgnoreCase);
                        // Some CapCut 9.x builds use a Qt class instead of
                        // MainWindow_QMLTYPE_*. A non-home owned window whose
                        // title is the newly-created draft is valid native
                        // editor evidence; do not discard it because UIA is
                        // still loading.
                        bool nativeEditor = role == CapCutWindowRole.Editor ||
                            className.IndexOf("MainWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            draftTitle ||
                            // Some CapCut builds expose the editor through a
                            // generic/empty Win32 class and paint the draft
                            // title only inside the QML surface. Once the
                            // filesystem has proved that this create command
                            // produced one unique, materialized draft, the
                            // foreground owned window (or the morphed home
                            // HWND) is safe native editor evidence. This is
                            // deliberately gated by the task-scoped draft
                            // fact; unknown windows before creation remain
                            // rejected.
                            (materializedDraft &&
                                (handle == foregroundHandle || handle == homeHandle));
                        if (!nativeEditor) { rejected.Add(handle + ":unknown"); continue; }
                        editorCandidates++;
                        bool sameHandle = homeHandle != IntPtr.Zero && handle == homeHandle;
                        observed.Add(handle + ":" + className + ":" +
                            (sameHandle ? "same" : "replacement"));
                        candidates.Add(Tuple.Create(handle, className, title, role));
                    } catch { }
                }
                lastDraftEditorProbeDiagnostic = "visible=" + visibleCandidates +
                    ",owned=" + ownedCandidates + ",home=" + homeCandidates +
                    ",transition=" + transitionCandidates + ",editor=" +
                    editorCandidates + (observed.Count == 0 ? "" :
                    ",candidates=" + String.Join("|", observed.Take(3).ToArray())) +
                    (rejected.Count == 0 ? "" : ",rejected=" +
                    String.Join("|", rejected.Take(5).ToArray()));
                var candidate = candidates
                    .OrderByDescending(value => value.Item4 == CapCutWindowRole.Editor ? 2 : 1)
                    .ThenByDescending(value => value.Item1 == foregroundHandle ? 1 : 0)
                    .ThenByDescending(value => value.Item1 == homeHandle ? 0 : 1)
                    .FirstOrDefault();
                if (candidate == null) return false;
                IntPtr editorHandle = candidate.Item1;
                boundEditorHandle = editorHandle;
                boundEditorProcessId = (int)GetWindowProcessId(editorHandle);
                boundEditorClassName = candidate.Item2 ?? "";
                nativeEditorBound = true;
                // UIA is needed by later semantic controls, but it must not
                // decide whether the draft was created. It is retried after
                // the native binding is persisted.
                mainWindow = TryGetWindowElementBounded(editorHandle, 1500);
                directDraftEditorHandle = editorHandle;
                if (editorHandle != IntPtr.Zero) {
                    NativeMethods.ActivateWindow(editorHandle);
                    if (!NativeMethods.EnsureMaximized(editorHandle))
                        rejected.Add(editorHandle + ":maximize_failed");
                    else
                        logger.Info("CapCut编辑器句柄已重新最大化：Handle=" + editorHandle);
                }
                RegisterOwnedSessionProcesses();
                draftCreationPhase = "editor_window_ready";
                if (draftCreationCheckpoint != null) {
                    draftCreationCheckpoint.CreationPhase = draftCreationPhase;
                    draftCreationCheckpoint.EditorWindowHandle = editorHandle.ToInt64();
                    draftCreationCheckpoint.EditorProcessId = boundEditorProcessId;
                    draftCreationCheckpoint.EditorWindowRole = "editor";
                    DraftRootLeaseRecord lease = draftRootLeaseManager == null
                        ? null : draftRootLeaseManager.Current;
                    draftCreationCheckpoint.DraftRoot = lease == null
                        ? draftRoot ?? "" : lease.ActiveDraftRoot ?? "";
                    draftCreationCheckpoint.EditorVerifiedAt = DateTime.Now.ToString("o");
                    NotifyDraftCreationPersistence();
                }
                return true;
            } catch (Exception ex) {
                lastDraftEditorProbeDiagnostic = "bind_exception=" + ex.GetType().Name + ":" + ex.Message;
                return nativeEditorBound;
            }
        }

        private bool HasMaterializedNewDraftEvidence()
        {
            try {
                DraftRootLeaseRecord lease = draftRootLeaseManager == null
                    ? null : draftRootLeaseManager.Current;
                if (lease == null || String.IsNullOrWhiteSpace(lease.ActiveDraftRoot)) return false;
                string active = Path.GetFullPath(lease.ActiveDraftRoot);
                string index = Path.Combine(active, "root_meta_info.json");
                if (!File.Exists(index)) return false;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(
                    File.ReadAllText(index, Encoding.UTF8)) as Dictionary<string, object>;
                object rawStores;
                object[] stores = root != null && root.TryGetValue("all_draft_store", out rawStores)
                    ? ObjectArrayValue(rawStores) : new object[0];
                foreach (object raw in stores) {
                    Dictionary<string, object> store = raw as Dictionary<string, object>;
                    if (store == null) continue;
                    string folder = TextValue(store, "draft_fold_path");
                    if (String.IsNullOrWhiteSpace(folder)) continue;
                    string full = Path.GetFullPath(Path.IsPathRooted(folder)
                        ? folder : Path.Combine(active, folder));
                    bool known = draftBaseline != null && draftBaseline.Contains(full);
                    if (!known && draftCreationCheckpoint != null &&
                        String.Equals(TextValue(store, "draft_id"),
                            draftCreationCheckpoint.DiscoveredDraftId,
                            StringComparison.OrdinalIgnoreCase)) known = true;
                    string content = Path.Combine(full, "draft_content.json");
                    if (Directory.Exists(full) && File.Exists(content) &&
                        new FileInfo(content).Length > 0 &&
                        (draftBaseline == null || !draftBaseline.Contains(full) ||
                         String.Equals(TextValue(store, "draft_id"),
                            draftCreationCheckpoint == null ? "" :
                                draftCreationCheckpoint.DiscoveredDraftId,
                            StringComparison.OrdinalIgnoreCase))) return true;
                }
            } catch { }
            return false;
        }

        private CapCutUiState TryClassifyEditorCandidate(IntPtr handle,
            string title, out AutomationElement window)
        {
            window = null;
            string className = NativeMethods.GetWindowClassName(handle);
            CapCutWindowRole role = CapCutHomeShellPolicy.Classify(className);
            if (role == CapCutWindowRole.Home) return CapCutUiState.Home;
            if (role == CapCutWindowRole.Transition) return CapCutUiState.Transitioning;
            // A non-home, owned CapCut window carrying a draft title is a
            // useful native fallback on builds that do not expose the usual
            // MainWindow_QMLTYPE_* class.  UIA is still required only to
            // obtain the automation root used by later controls.
            bool draftTitle = !String.IsNullOrWhiteSpace(title) &&
                !title.Equals("CapCut", StringComparison.OrdinalIgnoreCase) &&
                !title.Equals("剪映", StringComparison.OrdinalIgnoreCase);
            if (!draftTitle) return CapCutUiState.Ambiguous;
            window = TryGetWindowElementBounded(handle, 1500);
            return window == null ? CapCutUiState.Ambiguous : CapCutUiState.Editor;
        }

        private AutomationElement TryGetWindowElementBounded(IntPtr handle,
            int timeoutMilliseconds)
        {
            if (handle == IntPtr.Zero) return null;
            lock (editorProbeSync) {
                if (editorProbeInFlight.Contains(handle)) return null;
                editorProbeInFlight.Add(handle);
            }
            AutomationElement result = null;
            Thread probe = new Thread(new ThreadStart(delegate {
                try { result = locator.ElementFromWindow(handle); }
                catch { }
                finally {
                    lock (editorProbeSync) { editorProbeInFlight.Remove(handle); }
                }
            }));
            probe.IsBackground = true;
            try { probe.SetApartmentState(ApartmentState.MTA); } catch { }
            probe.Start();
            if (!probe.Join(Math.Max(250, timeoutMilliseconds))) {
                logger.Warn("CapCut编辑器窗口UIA探测超时，已让主流程继续：handle=" +
                    handle);
                // The old implementation left the blocked worker in
                // editorProbeInFlight forever. End the bounded probe and
                // release the handle so the next poll can retry cleanly.
                try { probe.Abort(); } catch { }
                lock (editorProbeSync) { editorProbeInFlight.Remove(handle); }
                return null;
            }
            return result;
        }

        private static string TextValue(Dictionary<string, object> value,
            string key)
        {
            object raw;
            return value != null && value.TryGetValue(key, out raw) && raw != null
                ? Convert.ToString(raw, CultureInfo.InvariantCulture).Trim() : "";
        }

        private static object[] ObjectArrayValue(object value)
        {
            object[] array = value as object[];
            if (array != null) return array;
            System.Collections.IEnumerable values = value as System.Collections.IEnumerable;
            return values == null ? new object[0] : values.Cast<object>().ToArray();
        }

        private static string BuildDraftIndexSemanticSignature(object[] stores,
            string activeRoot)
        {
            if (stores == null || stores.Length == 0) return "stores=0";
            var rows = new List<string>();
            foreach (object value in stores) {
                Dictionary<string, object> store = value as Dictionary<string, object>;
                if (store == null) {
                    rows.Add("invalid-store");
                    continue;
                }
                string id = TextValue(store, "draft_id");
                string name = TextValue(store, "draft_name");
                string folder = TextValue(store, "draft_fold_path");
                string json = TextValue(store, "draft_json_file");
                // Paths in CapCut's index may use either slash style and may
                // be absolute or relative while the card is being created.
                // Normalize them to a stable absolute path for comparison.
                string normalizedFolder = folder;
                string normalizedJson = json;
                try {
                    if (!String.IsNullOrWhiteSpace(folder))
                        normalizedFolder = NormalizePath(Path.IsPathRooted(folder)
                            ? folder : Path.Combine(activeRoot, folder));
                    if (!String.IsNullOrWhiteSpace(json))
                        normalizedJson = NormalizePath(Path.IsPathRooted(json)
                            ? json : Path.Combine(activeRoot, json));
                } catch { }
                rows.Add(String.Join("|", new[] {
                    id, name, normalizedFolder, normalizedJson
                }));
            }
            rows.Sort(StringComparer.OrdinalIgnoreCase);
            return "stores=" + stores.Length + "\n" + String.Join("\n", rows.ToArray());
        }

        private bool TryFindCapCutDraftPathError(out string evidence)
        {
            evidence = "";
            try {
                foreach (IntPtr windowHandle in NativeMethods.GetTopLevelWindowHandles()) {
                    int nativeProcessId;
                    try {
                        uint rawPid;
                        NativeMethods.GetWindowThreadProcessId(windowHandle, out rawPid);
                        nativeProcessId = (int)rawPid;
                    }
                    catch { continue; }
                    if (nativeProcessId <= 0) continue;
                    string processName;
                    try { processName = Process.GetProcessById(nativeProcessId).ProcessName; }
                    catch { continue; }
                    if (!(config.ProcessNames ?? new string[0]).Any(name => String.Equals(
                        Path.GetFileNameWithoutExtension(name), processName,
                        StringComparison.OrdinalIgnoreCase))) continue;

                    // Draft-path failures are modal/overlay text.  Inspect the
                    // native title first and only touch UIA for a likely dialog
                    // or the foreground window.  Never enumerate the complete
                    // editor tree during an import poll; that is the source of
                    // the historical 60-second apparent hang.
                    string nativeTitle = NativeMethods.GetWindowTitle(windowHandle);
                    if (ContainsDraftPathFailureText(nativeTitle)) {
                        evidence = nativeTitle.Length > 600 ? nativeTitle.Substring(0, 600) : nativeTitle;
                        return true;
                    }
                    string className = NativeMethods.GetWindowClassName(windowHandle);
                    IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
                    IntPtr lockedHandle = GetLockedWindowHandle();
                    // A foreground CapCut editor is not a dialog. The former
                    // fallback treated every foreground editor as a likely
                    // dialog while the editor HWND was not yet bound, then
                    // called UIA on the full QML surface on every poll. That
                    // was the direct source of the apparent create freeze.
                    bool likelyDialog = className.IndexOf("#32770", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!likelyDialog) continue;
                    AutomationElement window = locator.ElementFromWindow(windowHandle);
                    if (window == null) continue;
                    string semanticTitle = ControlLocator.SafeName(window);
                    if (ContainsDraftPathFailureText(semanticTitle)) {
                        evidence = semanticTitle.Length > 600 ? semanticTitle.Substring(0, 600) : semanticTitle;
                        return true;
                    }
                    // A native dialog's direct children are bounded and safe to
                    // inspect.  The main editor is intentionally excluded.
                    AutomationElementCollection children;
                    try { children = window.FindAll(TreeScope.Children, Condition.TrueCondition); }
                    catch { children = null; }
                    if (children == null) continue;
                    int childCount = 0;
                    foreach (AutomationElement child in children) {
                        if (++childCount > 128) break;
                        string childName = ControlLocator.SafeName(child);
                        if (ContainsDraftPathFailureText(childName)) {
                            evidence = childName.Length > 600 ? childName.Substring(0, 600) : childName;
                            return true;
                        }
                    }
                }
            } catch { }
            return false;
        }

        private static bool ContainsDraftPathFailureText(string text)
        {
            string value = (text ?? "").ToLowerInvariant();
            return value.Contains("草稿打开失败") ||
                value.Contains("无法正常读取草稿路径") ||
                value.Contains("检查你的磁盘连接") ||
                value.Contains("draft failed to open") ||
                value.Contains("unable to read draft path");
        }

        private void EnsureEditorContextForImport(CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            CapCutUiState lastState = CapCutUiState.Unavailable;
            while (watch.Elapsed < TimeSpan.FromSeconds(10)) {
                token.ThrowIfCancellationRequested();
                string hardError;
                if (TryFindCapCutDraftPathError(out hardError)) {
                    CaptureMainWindow("capcut-draft-path-error.png");
                    throw new CapCutOperationFailure("media_import",
                        "CAPCUT_DRAFT_PATH_OPEN_FAILED",
                        CapCutFailureClassification.HardSafety, hardError);
                }
                // The creation gate has already verified this exact editor
                // window.  Reuse it before doing any refresh; RefreshWindow()
                // enumerates the full UIA top-level tree and can block while
                // CapCut is loading media.  Only rediscover after the bound
                // handle really disappeared.
                if (IsBoundEditorWindowUsable()) {
                    lastState = CapCutUiState.Editor;
                } else {
                    try { RefreshWindow(); } catch { mainWindow = null; }
                    if (IsBoundEditorWindowUsable())
                        lastState = CapCutUiState.Editor;
                    else
                        lastState = ClassifyCapCutWindow(mainWindow);
                }
                if (lastState == CapCutUiState.Editor) break;
                IntPtr observedHandle = GetLockedWindowHandle();
                if (TryBindDraftCreationEditor(
                        lastState == CapCutUiState.Home ? observedHandle : IntPtr.Zero))
                    break;
                if (lastState == CapCutUiState.Home)
                    throw new CapCutOperationFailure("media_import",
                        "CAPCUT_EXPLICITLY_RETURNED_HOME",
                        CapCutFailureClassification.HardSafety,
                        "已通过首页窗口结构和精确“开始创作”控件确认CapCut返回首页");
                if (CountConfiguredCapCutProcesses() == 0)
                    throw new CapCutOperationFailure("media_import",
                        "CAPCUT_PROCESS_EXITED_DURING_IMPORT",
                        CapCutFailureClassification.Transient,
                        "导入前本轮CapCut进程已经退出");
                Thread.Sleep(250);
            }
            if (!IsBoundEditorWindowUsable())
                throw new CapCutOperationFailure("media_import",
                    lastState == CapCutUiState.Ambiguous
                        ? "CAPCUT_EDITOR_CONTEXT_AMBIGUOUS"
                        : "CAPCUT_EDITOR_REBIND_TIMEOUT",
                    CapCutFailureClassification.Transient,
                    "10秒内未能在本轮进程中重新绑定已验证的编辑器；state=" + lastState);
            DraftRootLeaseRecord lease = draftRootLeaseManager == null
                ? null : draftRootLeaseManager.Current;
            if (lease == null || String.IsNullOrWhiteSpace(lease.ActiveDraftRoot) ||
                !Directory.Exists(lease.ActiveDraftRoot) ||
                (File.GetAttributes(lease.ActiveDraftRoot) & FileAttributes.ReparsePoint) != 0)
                throw new CapCutOperationFailure("media_import",
                    "CAPCUT_ACTIVE_DRAFT_ROOT_PROBE_FAILED",
                    CapCutFailureClassification.HardSafety,
                    "当前活动草稿目录不可用");
            DraftRootOwnershipValidationResult ownership =
                draftRootLeaseManager.ValidateActiveRootOwnership(lease);
            if (!ownership.IsValid)
                throw new CapCutOperationFailure("media_import",
                    ownership.ErrorCode, CapCutFailureClassification.HardSafety,
                    ownership.Diagnostic);
        }

        private bool IsBoundEditorWindowUsable()
        {
            try {
                if (mainWindow == null) return false;
                IntPtr handle = new IntPtr(mainWindow.Current.NativeWindowHandle);
                if (handle == IntPtr.Zero || mainWindow.Current.IsOffscreen) return false;
                if (!IsConfiguredCapCutProcess(mainWindow)) return false;
                if (directDraftEditorHandle != IntPtr.Zero &&
                    handle != directDraftEditorHandle) return false;
                Process owner = Process.GetProcessById(mainWindow.Current.ProcessId);
                try { return !owner.HasExited; }
                finally { owner.Dispose(); }
            } catch { return false; }
        }

        private CapCutUiState ClassifyCapCutWindow(AutomationElement window)
        {
            if (window == null) return CapCutUiState.Unavailable;
            try {
                IntPtr handle = new IntPtr(window.Current.NativeWindowHandle);
                if (handle == IntPtr.Zero || window.Current.IsOffscreen)
                    return CapCutUiState.Unavailable;
                return CapCutWindowStateClassifier.Classify(
                    new CapCutWindowEvidence {
                        Available = true,
                        ClassName = window.Current.ClassName ?? "",
                        ExactCreateProjectControl =
                            locator.HasExactSemanticControl("CreateProject", window),
                        ExactExportControl =
                            locator.HasExactSemanticControl("Export", window),
                        TimelineStructure =
                            locator.HasExactSemanticControl("VideoTrackMute", window),
                        MediaPanelStructure =
                            locator.HasExactSemanticControl("Import", window)
                    });
            } catch { return CapCutUiState.Unavailable; }
        }

        private void CloseIsolatedOwnedHomeShellAfterEditorLaunch(IntPtr homeHandle, CancellationToken token)
        {
            // CapCut can keep its landing page alive after "Start creating"
            // opens the actual editor in another top-level process.  The two
            // taskbar entries look like two batch clients, but the second is
            // only this owned, empty home shell.  Close it only after the direct
            // draft flow has bound an exact editor handle.  Homepage and editor
            // may share a PID, so a PID difference is not a safety condition.
            // The formal handoff uses the same direct-draft identity contract as
            // the lab profile.  Keep ordinary/manual CapCut sessions untouched,
            // but never keep an owned landing-page shell once this task's exact
            // editor has been bound.
            if (!directDraftStyleRequested || directDraftIdentity == null ||
                homeHandle == IntPtr.Zero || mainWindow == null) return;
            IntPtr editorHandle = GetLockedWindowHandle();
            uint homePid = GetWindowProcessId(homeHandle);
            uint editorPid = GetWindowProcessId(editorHandle);
            if (homePid != 0 && homePid == editorPid) {
                logger.Info("CapCut 首页与已验证编辑器属于同一进程；为避免结束编辑器，保留首页窗口。 homePid=" +
                    homePid + "，editorPid=" + editorPid);
                return;
            }
            if (!CapCutHomeShellPolicy.CanClose(homeHandle, editorHandle,
                homePid, editorPid,
                IsCapCutHomeShell(homeHandle),
                directDraftEditorHandle != IntPtr.Zero && directDraftEditorHandle == editorHandle)) return;

            if (!NativeMethods.PostMessage(homeHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) {
                logger.Warn("已识别隔离内测遗留 CapCut 首页空壳，但发送关闭请求失败；保留编辑器继续运行。 homePid=" + homePid);
                return;
            }

            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3)) {
                token.ThrowIfCancellationRequested();
                AutomationElement oldHome = locator.ElementFromWindow(homeHandle);
                if (oldHome == null || oldHome.Current.IsOffscreen) {
                    logger.Info("已关闭本轮隔离 CapCut 首页空壳；编辑器保持运行。 homePid=" + homePid + "，editorPid=" + editorPid);
                    return;
                }
                Thread.Sleep(150);
            }
            logger.Warn("隔离 CapCut 首页空壳未在3秒内退出；不会重复关闭或影响当前编辑器。 homePid=" + homePid + "，editorPid=" + editorPid);
        }

        private uint GetWindowProcessId(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return 0;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(handle, out processId);
            return processId;
        }

        private bool IsCapCutHomeShell(IntPtr handle)
        {
            try {
                AutomationElement root = locator.ElementFromWindow(handle);
                if (root == null || root.Current.IsOffscreen) return false;
                return ClassifyCapCutWindow(root) == CapCutUiState.Home;
            } catch {
                return false;
            }
        }

        public void ImportVideo(string videoPath, int timeoutSeconds, CancellationToken token)
        {
            try {
                importedMainVideoSeconds = WindowsMediaMetadataReader.Read(videoPath).DurationSeconds;
                logger.Info(String.Format("主视频时长已读取：{0:0.000}s", importedMainVideoSeconds));
            } catch (Exception ex) {
                importedMainVideoSeconds = 0;
                logger.Warn("无法读取主视频时长，已阻止固定坐标拼接：" + ex.Message);
            }
            ImportMedia(videoPath, timeoutSeconds, token, "MediaItem", "capcut-after-import.png");
        }

        public void ReplaceMainVideo(string videoPath, int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            if (!locator.ClickCoordinateNow("MainVideoClip", mainWindow)) {
                locator.Click("MainVideoClip", mainWindow, config.Timeouts.ControlSeconds, token, true);
            }
            NativeMethods.PressDelete();
            SleepChecked(800, token);
            logger.Info("已从时间线删除上一条主视频片段，保留音频、字幕和结尾样品");

            ImportMedia(videoPath, timeoutSeconds, token, "MediaItem", "capcut-after-replacement-import.png");
            RefreshWindow();
            FocusForCoordinateAction(token);
            if (!locator.ClickCoordinateNow("MediaSearchField", mainWindow)) {
                throw new InvalidOperationException("未校准媒体搜索框，无法准确选择新导入的替换视频");
            }
            ControlLocator.PasteTextToForeground(Path.GetFileNameWithoutExtension(videoPath));
            SleepChecked(700, token);
            if (!locator.WaitForVisualContent("MediaItem", mainWindow, Math.Min(timeoutSeconds, 20), token)) {
                throw new InvalidOperationException("导入后无法在媒体搜索结果中找到替换视频");
            }
            if (!locator.DragRelative("MediaItem", "TimelineZeroDrop", mainWindow, true)) {
                throw new InvalidOperationException("无法把替换视频放到时间线零点");
            }
            SleepChecked(1800, token);
            locator.ClickCoordinateNow("MediaSearchField", mainWindow);
            NativeMethods.SendControlChord(NativeMethods.VK_A);
            NativeMethods.PressDelete();
            SleepChecked(400, token);
            CaptureMainWindow("capcut-after-main-video-replaced.png");
        }

        public void ImportAudio(string audioPath, int timeoutSeconds, CancellationToken token)
        {
            importedAudioPath = NormalizeDraftMediaPath(audioPath);
            importedAudioSeconds = ReadMediaDurationForAlignment(audioPath, "独立音频");
            ImportMedia(audioPath, timeoutSeconds, token, "AudioItem", "capcut-after-audio-import.png");
        }

        public void ImportSample(string samplePath, int timeoutSeconds, CancellationToken token)
        {
            importedSamplePath = NormalizeDraftMediaPath(samplePath);
            importedSampleSeconds = ReadMediaDurationForAlignment(samplePath, "任务专属样品");
            ImportMedia(samplePath, timeoutSeconds, token, "SampleItem", "capcut-after-sample-import.png");
        }

        private double ReadMediaDurationForAlignment(string path, string label)
        {
            try {
                double seconds = WindowsMediaMetadataReader.Read(path).DurationSeconds;
                logger.Info(String.Format("{0}时长已读取：{1:0.000}s", label, seconds));
                return seconds;
            } catch (Exception ex) {
                logger.Warn("无法读取" + label + "时长；末端对齐将使用保守视觉门禁：" + ex.Message);
                return 0;
            }
        }

        public void ReplaceSample(string samplePath, double speed, int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            if (!locator.ClickCoordinateNow("SampleClip", mainWindow)) {
                locator.Click("SampleClip", mainWindow, config.Timeouts.ControlSeconds, token, true);
            }
            NativeMethods.PressDelete();
            SleepChecked(700, token);
            logger.Info("已删除上一条结尾样品，保留主视频、独立音频和字幕");
            ImportSample(samplePath, timeoutSeconds, token);
            PlaceSampleAtEnd(timeoutSeconds, token);
            SetSampleSpeed(speed, timeoutSeconds, token);
        }

        private void ImportMedia(string mediaPath, int timeoutSeconds, CancellationToken token, string visualAction, string screenshotName)
        {
            if (!File.Exists(mediaPath)) throw new FileNotFoundException("媒体文件不存在", mediaPath);
            string normalizedImportPath = Path.GetFullPath(mediaPath);
            bool sameImport = String.Equals(activeImportPath ?? "", normalizedImportPath,
                StringComparison.OrdinalIgnoreCase);
            string restoredImportPhase = sameImport ? activeImportPhase : "import_waiting";
            if (!sameImport) {
                activeImportCommandId = Guid.NewGuid().ToString("N");
                activeImportPhase = "import_waiting";
            }
            activeImportPath = normalizedImportPath;
            if (String.IsNullOrWhiteSpace(activeImportCommandId))
                activeImportCommandId = Guid.NewGuid().ToString("N");
            logger.Info("导入阶段开始：" + visualAction + " / " + Path.GetFileName(mediaPath));
            EnsureEditorContextForImport(token);
            logger.Info("导入门禁通过：编辑器、草稿身份和活动目录均已验证");
            if (WaitForExactImportedMaterial(mediaPath, 1, token)) {
                NotifyMediaImportPersistence("media_bin_verified");
                logger.Info("草稿素材索引已包含当前媒体，接管既有入库结果");
                return;
            }
            // An HDR notice can appear after the previous clip was placed and
            // block Ctrl+I without becoming a separate top-level window.  At
            // this generic import boundary only accept the exact acknowledgement
            // text; do not use visual coordinates for unrelated overlays.
            DismissKnownInlineNotice(token, false);
            if (IsNativeRenameDialogForeground()) {
                NativeMethods.PressEscape();
                logger.Warn("检测到并取消了本轮 CapCut 遗留的重命名弹窗；未向其中写入媒体路径");
                SleepChecked(350, token);
                RefreshWindow();
            }
            logger.Info("导入前弹窗检查完成：" + visualAction);
            WaitForEditorResponsive(token, EditorBusyWaitSeconds, "导入前 " + visualAction);
            FocusForCoordinateAction(token);
            logger.Info("导入前焦点已锁定到 CapCut：" + visualAction);
            if (CloseForegroundCapCutPopup()) {
                SleepChecked(700, token);
                RefreshWindow();
                FocusForCoordinateAction(token);
            }
            HashSet<long> nativeBaseline = CaptureNativeWindowBaseline();
            Stopwatch importWatch = Stopwatch.StartNew();
            MediaDialogWaitResult wait = WaitForNativeMediaDialog(1, token,
                null, importWatch);
            MediaDialogObservation observed = wait.Accepted;
            logger.Info("导入前文件选择器原生预检完成：" +
                (observed == null ? "未发现" : observed.Handle.ToString()) +
                " / " + visualAction);

            // A persisted same-generation command is reconciled before any
            // native input is repeated.  If its picker is gone and no media
            // fact exists, the result is unknown; only a released transaction
            // generation may create a new command and send Ctrl+I again.
            bool resumedUnconfirmedCommand = sameImport &&
                MediaImportFacts.IsUnresolved(activeImportPhase);
            if (observed == null && resumedUnconfirmedCommand) {
                throw new CapCutOperationFailure("media_import",
                    "CAPCUT_IMPORT_COMMAND_UNCONFIRMED",
                    CapCutFailureClassification.AlreadyCommitted,
                    "上一导入命令已发送但未找到存活文件窗口或已提交媒体；已保留命令现场，禁止重复输入");
            }
            if (observed == null) {
                if (!SettleEditorForegroundBeforeShortcut(token, visualAction)) {
                    throw new CapCutOperationFailure("media_import",
                        "CAPCUT_IMPORT_FOREGROUND_CONFLICT",
                        CapCutFailureClassification.Transient,
                        "Ctrl+I 前前台窗口不属于已绑定 CapCut 编辑器；" + DescribeForegroundWindow());
                }
                NotifyMediaImportPersistence("import_intent_committed");
                if (!NativeMethods.SendImportShortcut()) {
                    throw new CapCutOperationFailure("media_import",
                        "CAPCUT_IMPORT_SHORTCUT_SEND_FAILED",
                        CapCutFailureClassification.Transient,
                        "Windows SendInput 未完整发送 Ctrl+I; win32=" +
                        NativeMethods.LastSendInputError + "; inputSize=" +
                        NativeMethods.KeyboardInputStructureSize);
                }
                NotifyMediaImportPersistence("shortcut_sent");
                logger.Info("已通过 Win32 SendInput 发送导入快捷键 Ctrl+I");
                wait = WaitForNativeMediaDialog(6, token, nativeBaseline,
                    importWatch);
                observed = wait.Accepted;
                logger.Info("快捷键后文件选择器原生预检完成：" +
                    (observed == null ? "未发现" : observed.Handle.ToString()) +
                    " / " + visualAction);
                if (observed == null) {
                    // A busy editor (cold start, or right after a heavy
                    // timeline edit) handles the queued Ctrl+I only once it
                    // answers again, so the picker can arrive late. Wait for
                    // the editor, then look for that late picker before the
                    // single coordinate fallback, which must never race it.
                    bool wasBusy = !WaitForEditorResponsive(token, 0, "快捷键后 " + visualAction);
                    if (wasBusy)
                        WaitForEditorResponsive(token, EditorBusyWaitSeconds, "快捷键后 " + visualAction);
                    wait = WaitForNativeMediaDialog(LatePickerWaitSeconds, token, nativeBaseline,
                        importWatch);
                    observed = wait.Accepted;
                    logger.Info("快捷键后延迟文件选择器复查完成：" +
                        (observed == null ? "未发现" : observed.Handle.ToString()) +
                        " / " + visualAction);
                }
                if (observed == null) {
                    // Re-validate the editor before the single coordinate
                    // fallback. A hidden error/dialog or changed foreground
                    // must never receive a blind click.
                    EnsureEditorContextForImport(token);
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    IntPtr editor = GetLockedWindowHandle();
                    if (foreground != editor || wait.Observations.Any(o =>
                        o.Visible && o.NativeDialogClass &&
                        (o.OwnedSessionProcess || o.DirectProcessMatch || o.OwnerChainMatchesEditor)))
                        throw new CapCutOperationFailure("media_import",
                            "CAPCUT_IMPORT_FOREGROUND_CONFLICT",
                            CapCutFailureClassification.Transient,
                            "快捷键后编辑器失去前台，未点击导入按钮；" + DescribeForegroundWindow());
                    logger.Info("快捷键未发现可验证文件选择器，执行一次校准导入按钮：" + visualAction);
                    locator.Click("Import", mainWindow,
                        config.Timeouts.ControlSeconds, token, true);
                    wait = WaitForNativeMediaDialog(
                        Math.Min(timeoutSeconds, 20), token,
                        nativeBaseline, importWatch);
                    observed = wait.Accepted;
                    logger.Info("校准导入按钮后文件选择器原生预检完成：" +
                        (observed == null ? "未发现" : observed.Handle.ToString()) +
                        " / " + visualAction);
                }
            } else {
                logger.Info("接管已经打开且归属可验证的媒体选择窗口");
            }
            if (observed != null) {
                IntPtr dialog = new IntPtr(observed.Handle);
                NotifyMediaImportPersistence("native_dialog_detected");
                FileDialogProbeResult probe = RunBoundedFileDialogProbe(dialog, mediaPath, token, true);
                bool inspectProbeTimedOut = !probe.Success &&
                    String.Equals(probe.ErrorCode, "CAPCUT_FILE_DIALOG_PROBE_TIMEOUT",
                        StringComparison.OrdinalIgnoreCase);
                if (!probe.Success && !inspectProbeTimedOut)
                    throw new CapCutOperationFailure("media_import", probe.ErrorCode,
                        CapCutFailureClassification.Transient, probe.Diagnostic);
                if (inspectProbeTimedOut) {
                    // The native window was already accepted using bounded
                    // Win32 evidence (process/owner/class/visibility).  A
                    // slow UIA provider must not turn that verified dialog
                    // into a false "file picker missing" failure.  Do not
                    // claim the filename field was verified: the bounded
                    // submit worker will perform its own field and Open
                    // button checks.  It is invoked exactly once below.
                    logger.Warn("文件选择器原生身份已确认，但UIA检查超过2秒；转入一次受限提交探测: " +
                        probe.Diagnostic);
                } else {
                    NotifyMediaImportPersistence("filename_field_verified");
                }
                // Commit intent before the worker can fill or submit anything.
                if (sameImport && (restoredImportPhase == "path_submitted" ||
                    restoredImportPhase == "path_submission_intent" || restoredImportPhase == "dialog_closed"))
                    throw new CapCutOperationFailure("media_import", "CAPCUT_IMPORT_COMMAND_UNCONFIRMED",
                        CapCutFailureClassification.AlreadyCommitted, "上一文件路径提交结果未确认，禁止再次提交");
                NotifyMediaImportPersistence("path_submission_intent");
                probe = RunBoundedFileDialogProbe(dialog, mediaPath, token, false);
                // The submit worker is stopped after 8 s, which can happen
                // after it already pressed Enter (a slow first file dialog on
                // a cold start: 10-11, 10-12, 10-16 ×2). Treat the path as
                // possibly submitted, type nothing more, and let the draft
                // decide: picker closed + media in the bin = imported.
                bool submitTimedOut = !probe.Success && String.Equals(probe.ErrorCode,
                    "CAPCUT_IMPORT_COMMAND_UNCONFIRMED", StringComparison.OrdinalIgnoreCase);
                if (!probe.Success && !submitTimedOut)
                    throw new CapCutOperationFailure("media_import", probe.ErrorCode,
                        CapCutFailureClassification.Transient, probe.Diagnostic);
                NotifyMediaImportPersistence("path_submitted");
                if (submitTimedOut)
                    logger.Warn("文件选择器提交探测超时，不再输入，转入草稿核对：" + probe.Diagnostic);
                SleepChecked(900, token);
                if (WaitForNativeDialogClosed(dialog, submitTimedOut ? 20 : 3, token)) {
                    logger.Info("已通过 Enter 提交媒体文件路径");
                    NotifyMediaImportPersistence("dialog_closed");
                    SleepChecked(1000, token);
                    RefreshWindow();
                    FocusForCoordinateAction(token);
                    // Recent CapCut builds can open an in-editor Pro offer
                    // after the first media import. It belongs to the same
                    // editor window, so the normal child-window popup closer
                    // cannot see it. Escape dismisses that overlay without
                    // changing any calibrated coordinate or timeline state.
                    if (String.Equals(visualAction, "AudioItem", StringComparison.OrdinalIgnoreCase)) {
                        NativeMethods.PressEscape();
                        SleepChecked(450, token);
                        RefreshWindow();
                        FocusForCoordinateAction(token);
                        logger.Info("音频导入后已发送 Escape，关闭可能遮挡素材区的内嵌推广层");
                    }
                    if (!WaitForExactImportedMaterial(mediaPath, Math.Min(timeoutSeconds, 60), token)) {
                        CaptureMainWindow("capcut-import-timeout-" + visualAction + ".png");
                        throw new CapCutOperationFailure("media_import",
                            "CAPCUT_MEDIA_BIN_IDENTITY_UNCONFIRMED", CapCutFailureClassification.Transient,
                            "文件窗口已关闭，但草稿素材索引未确认目标媒体: " + Path.GetFileName(mediaPath));
                    }
                    CaptureMainWindow(screenshotName);
                    // The file is only known to be in CapCut's media bin at
                    // this point.  It has not been committed to a timeline;
                    // the placement operation owns that later checkpoint.
                    NotifyMediaImportPersistence("media_bin_verified");
                    return;
                }
                if (submitTimedOut)
                    throw new CapCutOperationFailure("media_import", probe.ErrorCode,
                        CapCutFailureClassification.Transient,
                        probe.Diagnostic + "；文件窗口 20s 内仍未关闭，未再输入");
            } else {
                throw new CapCutOperationFailure("media_import", "CAPCUT_FILE_DIALOG_NATIVE_NOT_FOUND",
                    CapCutFailureClassification.Transient,
                    "未找到与当前编辑器归属匹配的文件选择窗口；候选诊断: " +
                    (wait == null ? "none" : wait.Diagnostic));
            }
            throw new CapCutOperationFailure("media_import", "CAPCUT_IMPORT_COMMAND_UNCONFIRMED",
                CapCutFailureClassification.Transient, "文件选择器未确认关闭");
        }

        private bool WaitForExactImportedMaterial(string mediaPath, int seconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            string lastDiagnostic = "";
            do {
                token.ThrowIfCancellationRequested();
                try {
                    string draft = ResolveImportDraftDirectory();
                    string metadata = Path.Combine(draft, "draft_meta_info.json");
                    if (File.Exists(metadata)) {
                        string id = MediaImportFacts.FindMaterialId(File.ReadAllText(metadata), mediaPath);
                        if (!String.IsNullOrWhiteSpace(id)) {
                            logger.Info("精确素材入库验证通过: materialId=" + id + "; file=" + Path.GetFileName(mediaPath));
                            return true;
                        }
                    }
                } catch (IOException ex) { lastDiagnostic = ex.Message; }
                  catch (ArgumentException ex) { lastDiagnostic = ex.Message; }
                SleepChecked(200, token);
            } while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, seconds)));
            if (!String.IsNullOrWhiteSpace(lastDiagnostic)) logger.Warn("素材索引读取暂未稳定: " + lastDiagnostic);
            return false;
        }

        private string ResolveImportDraftDirectory()
        {
            if (!String.IsNullOrWhiteSpace(directDraftDirectory) && directDraftIdentity != null) {
                DirectDraftIdentity current = ReadDirectDraftIdentity(directDraftDirectory);
                if (current == null || !String.Equals(current.DraftId, directDraftIdentity.DraftId,
                    StringComparison.OrdinalIgnoreCase) || !String.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(directDraftDirectory)), Path.GetFullPath(draftRoot),
                    StringComparison.OrdinalIgnoreCase))
                    throw new CapCutOperationFailure("media_import", "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH",
                        CapCutFailureClassification.HardSafety, "已恢复草稿的当前路径或身份发生变化");
                return directDraftDirectory;
            }
            // Import starts while the draft is empty. The creation checkpoint
            // binds that draft before source media exists; cleanup's source-
            // matching resolver cannot be used at this earlier boundary.
            if (draftCreationCheckpoint != null &&
                !String.IsNullOrWhiteSpace(draftCreationCheckpoint.DiscoveredDraftPath)) {
                string path = Path.GetFullPath(draftCreationCheckpoint.DiscoveredDraftPath);
                if (!String.Equals(Path.GetDirectoryName(path), Path.GetFullPath(draftRoot),
                    StringComparison.OrdinalIgnoreCase))
                    throw new CapCutOperationFailure("media_import", "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH",
                        CapCutFailureClassification.HardSafety, "导入草稿不属于当前活动根目录");
                var root = new JavaScriptSerializer().DeserializeObject(
                    ReadTextAllowingWriters(Path.Combine(draftRoot, "root_meta_info.json"))) as Dictionary<string, object>;
                object raw;
                object[] rows = root != null && root.TryGetValue("all_draft_store", out raw)
                    ? raw as object[] : null;
                int matches = rows == null ? 0 : rows.OfType<Dictionary<string, object>>().Count(row =>
                    String.Equals(TextValue(row, "draft_id"), draftCreationCheckpoint.DiscoveredDraftId,
                        StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(Path.GetFullPath(TextValue(row, "draft_fold_path")), path,
                        StringComparison.OrdinalIgnoreCase));
                if (matches != 1 || !Directory.Exists(path))
                    throw new CapCutOperationFailure("media_import", "CAPCUT_RECOVERY_DRAFT_IDENTITY_MISMATCH",
                        CapCutFailureClassification.HardSafety, "当前创建断点未匹配唯一根索引记录");
                return path;
            }
            return GetUniqueCreatedDraftDirectory();
        }

        // CapCut may hold root_meta_info.json briefly while saving it.
        private static string ReadTextAllowingWriters(string path)
        {
            for (int attempt = 1; ; attempt++) {
                try {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        return reader.ReadToEnd();
                } catch (FileNotFoundException) { throw; }
                  catch (DirectoryNotFoundException) { throw; }
                  catch (IOException) {
                    if (attempt >= 5) throw;
                }
                Thread.Sleep(200);
            }
        }

        private bool TryArchiveStaleNoEffectPlacementJournal(string journal,
            string placementDraft, string sourcePath, string placementDraftId)
        {
            // A placement journal is an intent, not proof that CapCut created a
            // timeline segment.  Reconcile only an old intent whose owner is
            // gone and whose same draft/source is still stably empty.
            if (!File.Exists(journal)) return false;
            try
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(journal);
                if ((DateTime.UtcNow - lastWrite).TotalSeconds < 30)
                    return false;

                var serializer = new JavaScriptSerializer();
                var record = serializer.DeserializeObject(
                    File.ReadAllText(journal, Encoding.UTF8)) as Dictionary<string, object>;
                if (record == null) return false;

                object rawDraft, rawSource, rawDraftId, rawOwnerPid, rawOwnerStartedAt;
                record.TryGetValue("draftDirectory", out rawDraft);
                record.TryGetValue("sourcePath", out rawSource);
                record.TryGetValue("draftId", out rawDraftId);
                record.TryGetValue("ownerProcessId", out rawOwnerPid);
                record.TryGetValue("ownerStartedAt", out rawOwnerStartedAt);

                if (!String.Equals(NormalizePath(Convert.ToString(rawDraft)),
                    NormalizePath(placementDraft), StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(NormalizePath(Convert.ToString(rawSource)),
                    NormalizePath(sourcePath), StringComparison.OrdinalIgnoreCase))
                    return false;

                string recordedDraftId = Convert.ToString(rawDraftId) ?? "";
                if (!String.IsNullOrWhiteSpace(recordedDraftId) &&
                    !String.IsNullOrWhiteSpace(placementDraftId) &&
                    !String.Equals(recordedDraftId, placementDraftId,
                        StringComparison.OrdinalIgnoreCase))
                    return false;

                int ownerPid = 0;
                if (rawOwnerPid != null)
                    Int32.TryParse(Convert.ToString(rawOwnerPid,
                        CultureInfo.InvariantCulture), out ownerPid);
                if (ownerPid > 0)
                {
                    try
                    {
                        using (Process owner = Process.GetProcessById(ownerPid))
                        {
                            if (!owner.HasExited)
                            {
                                string recordedStartedAt = Convert.ToString(rawOwnerStartedAt) ?? "";
                                DateTime recorded;
                                if (!DateTime.TryParse(recordedStartedAt,
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out recorded))
                                    return false;
                                DateTime actual = owner.StartTime.ToUniversalTime();
                                if (Math.Abs((actual - recorded.ToUniversalTime()).TotalSeconds) < 2)
                                    return false;
                            }
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { return false; }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                }

                string archived = journal + ".no-effect-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",
                        CultureInfo.InvariantCulture) + ".json";
                File.Move(journal, archived);
                logger.Warn("已将上一轮未产生时间线片段的加入意图归档，允许安全重试：" +
                    archived);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.Warn("加入意图对账失败，保留现场并阻止重试：" + ex.Message);
                return false;
            }
        }
        public void PlaceOnTimeline(int timeoutSeconds, CancellationToken token)
        {
            string placementDraft = ResolveImportDraftDirectory();
            double placementSeconds = importedMainVideoSeconds > 0 ? importedMainVideoSeconds :
                WindowsMediaMetadataReader.Read(draftExpectedVideoPath).DurationSeconds;
            string observed = draftTimelineVerifier.ObserveVideoPlacement(placementDraft, draftExpectedVideoPath, placementSeconds);
            SleepChecked(500, token);
            string confirmed = draftTimelineVerifier.ObserveVideoPlacement(placementDraft, draftExpectedVideoPath, placementSeconds);
            string commandKey;
            string placementDraftId = directDraftIdentity == null ? "" : directDraftIdentity.DraftId;
            if (String.IsNullOrWhiteSpace(placementDraftId) && draftCreationCheckpoint != null)
                placementDraftId = draftCreationCheckpoint.DiscoveredDraftId ?? "";
            // The CapCut draft directory name (for example, 0908) can be
            // reused after a released lease.  A placement journal must be
            // scoped to the actual draft identity, not just that reusable
            // path, otherwise a prior attempt can block a clean new draft.
            using (var hash = System.Security.Cryptography.SHA256.Create())
                commandKey = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    Path.GetFullPath(placementDraft).ToUpperInvariant() + "|" +
                    Path.GetFullPath(draftExpectedVideoPath).ToUpperInvariant() + "|" +
                    (placementDraftId ?? "").ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
            string journalDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "placement-commands");
            string journal = Path.Combine(journalDirectory, commandKey + ".json");
            if (observed == "video_present_valid" && confirmed == observed) {
                logger.Info("已接管唯一主视频片段，不重复加入时间线");
                return;
            }
            if (observed != "empty_confirmed" || confirmed != observed)
                throw new CapCutOperationFailure("video_placement", "CAPCUT_VIDEO_PLACEMENT_RESULT_PENDING",
                    CapCutFailureClassification.HardSafety,
                    "加入前草稿未确认稳定为空，已停止: " + observed + " / " + confirmed);
            if (File.Exists(journal) &&
                !TryArchiveStaleNoEffectPlacementJournal(journal, placementDraft,
                    draftExpectedVideoPath, placementDraftId))
                throw new CapCutOperationFailure("video_placement", "CAPCUT_VIDEO_PLACEMENT_RESULT_PENDING",
                    CapCutFailureClassification.HardSafety, "已有加入命令，草稿结果尚未确认，禁止重新发送: " + journal);
            Directory.CreateDirectory(journalDirectory);
            string commandId = Guid.NewGuid().ToString("N");
            string temporaryJournal = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
            DurableFile.WriteAllText(temporaryJournal, new JavaScriptSerializer().Serialize(new {
                commandId = commandId, phase = "placement_intent_committed",
                draftDirectory = placementDraft, sourcePath = draftExpectedVideoPath,
                draftId = placementDraftId,
                baseline = confirmed, createdAt = DateTime.UtcNow.ToString("o"),
                ownerProcessId = Process.GetCurrentProcess().Id,
                ownerStartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o") }),
                new UTF8Encoding(false));
            File.Move(temporaryJournal, journal);
            logger.Info("主视频加入意图已落盘: command=" + commandId + "; draft=" + placementDraft);
            RefreshWindow();
            FocusForCoordinateAction(token);
            // drag_after_settle: on 2026-10-16 (V1, V2) the button and the
            // double-click target were not found, so the single drag on a
            // cold-started editor was the only action sent, and it left the
            // draft empty. Give it one more drop once the editor answers and
            // is foreground, and only after the draft is still confirmed empty.
            string[] strategies = { "semantic_add", "drag", "double_click", "drag_after_settle" };
            foreach (string strategy in strategies) {
                bool sent = false;
                if (String.Equals(strategy, "drag_after_settle", StringComparison.Ordinal)) {
                    string settled = ObserveVideoPlacementSettled(placementDraft, placementSeconds, token);
                    if (String.Equals(settled, "video_present_valid", StringComparison.Ordinal)) {
                        logger.Info("前一次加入已延迟落盘为唯一主视频片段，直接接管，不再拖拽");
                        return;
                    }
                    if (!String.Equals(settled, "empty_confirmed", StringComparison.Ordinal))
                        throw new CapCutOperationFailure("video_placement", "CAPCUT_VIDEO_PLACEMENT_RESULT_PENDING",
                            CapCutFailureClassification.HardSafety,
                            "补拖拽前草稿未持续确认为空，禁止再次加入: " + settled);
                    if (!SettleEditorForegroundBeforeShortcut(token, "主视频加入时间线", "补拖拽")) {
                        logger.Warn("补拖拽前编辑器未能稳定在前台，不再加入");
                        continue;
                    }
                    RefreshWindow();
                    FocusForCoordinateAction(token);
                }
                if (String.Equals(strategy, "semantic_add", StringComparison.Ordinal))
                    sent = locator.Click("AddToTimeline", mainWindow, 3, token, false);
                else if (String.Equals(strategy, "drag", StringComparison.Ordinal) ||
                         String.Equals(strategy, "drag_after_settle", StringComparison.Ordinal)) {
                    // One calibrated drop per strategy. An unconfirmed write is
                    // never permission to add another copy on a different lane;
                    // the second drop needs a repeatedly empty draft first.
                    LocatorRule source = config.GetRule("MediaItem");
                    LocatorRule target = config.GetRule("TimelineDrop");
                    if (source.HasCoordinate && target.HasCoordinate && mainWindow != null) {
                        Rect bounds = mainWindow.Current.BoundingRectangle;
                        int fromX = (int)(bounds.Left + bounds.Width * source.RelativeX);
                        int fromY = (int)(bounds.Top + bounds.Height * source.RelativeY);
                        double[] trackBands = { target.RelativeY };
                        foreach (double band in trackBands.Distinct()) {
                            int toX = (int)(bounds.Left + bounds.Width * 0.071);
                            int toY = (int)(bounds.Top + bounds.Height * band);
                            NativeMethods.Drag(fromX, fromY, toX, toY);
                            logger.Info("使用实时窗口拖拽候选轨道：" +
                                String.Format("y={0:0.000}", band));
                            SleepChecked(650, token);
                            // The common postcondition check below decides whether
                            // this action produced a valid segment.  Do not run a
                            // second 30-second verifier here: a stable empty draft
                            // means this strategy had no effect and the next
                            // finite strategy may be tried exactly once.
                            sent = true;
                            break;
                        }
                    }
                }
                else
                    sent = locator.DoubleClick("ImportedMedia", mainWindow,
                        config.Timeouts.ControlSeconds, token, false);
                if (!sent) {
                    logger.Info("加入策略未发送（未找到可用的界面元素或坐标）：" + strategy);
                    continue;
                }

                SleepChecked(strategy.StartsWith("drag", StringComparison.Ordinal)
                    ? 1800 : 1200, token);
                DismissKnownInlineNotice(token, true);
                SleepChecked(350, token);
                CaptureMainWindow("capcut-after-timeline-" + strategy + ".png");
                try {
                    string draftDirectory = ResolveImportDraftDirectory();
                    double duration = importedMainVideoSeconds > 0
                        ? importedMainVideoSeconds
                        : WindowsMediaMetadataReader.Read(draftExpectedVideoPath).DurationSeconds;
                    DraftVideoTimelineResult verified =
                        draftTimelineVerifier.VerifyVideoPlacementStable(
                            draftDirectory, draftExpectedVideoPath, duration, 30, token);
                    logger.Info("主视频已由草稿事实确认进入时间线：strategy=" + strategy +
                        "; segment=" + (verified.MainSegmentId ?? ""));
                    return;
                } catch (DraftTimelineValidationException ex) {
                    // A sent action that leaves two stable empty snapshots did
                    // not take effect.  It is safe to try the next bounded
                    // strategy; any observed segment, identity mismatch, or
                    // structural conflict remains a hard stop.
                    bool stableEmpty = false;
                    if (String.Equals(ex.ErrorCode,
                        "CAPCUT_VIDEO_PLACEMENT_RESULT_PENDING",
                        StringComparison.Ordinal)) {
                        try {
                            string pendingDraft = ResolveImportDraftDirectory();
                            double pendingDuration = importedMainVideoSeconds > 0
                                ? importedMainVideoSeconds
                                : WindowsMediaMetadataReader.Read(draftExpectedVideoPath).DurationSeconds;
                            string pendingFirst = draftTimelineVerifier.ObserveVideoPlacement(
                                pendingDraft, draftExpectedVideoPath, pendingDuration);
                            SleepChecked(500, token);
                            string pendingSecond = draftTimelineVerifier.ObserveVideoPlacement(
                                pendingDraft, draftExpectedVideoPath, pendingDuration);
                            stableEmpty = String.Equals(pendingFirst, "empty_confirmed",
                                    StringComparison.Ordinal) &&
                                String.Equals(pendingSecond, pendingFirst,
                                    StringComparison.Ordinal);
                        } catch (Exception observeError) {
                            logger.Warn("加入动作结果待核对，无法确认是否稳定为空：" +
                                observeError.Message);
                        }
                    }
                    if (stableEmpty) {
                        logger.Warn("加入策略未产生时间线片段，允许继续下一种有限策略：" +
                            strategy);
                        continue;
                    }
                    throw new CapCutOperationFailure("video_placement", ex.ErrorCode,
                        CapCutFailureClassification.HardSafety,
                        "加入动作结果未通过验证，禁止再次追加: " + strategy + " / " + ex.Message);
                }
            }
            throw new CapCutOperationFailure("video_placement",
                "CAPCUT_VIDEO_PLACEMENT_UNCONFIRMED",
                CapCutFailureClassification.Transient,
                "已尝试有限的添加、双击和拖拽策略，但草稿中没有形成当前任务唯一主视频片段");
        }

        // A late save of the earlier drop must not become a second copy: the
        // draft has to read the same state three times over about five
        // seconds. Returns that state, or "" when it is unreadable or moving.
        private string ObserveVideoPlacementSettled(string draftDirectory, double seconds,
            CancellationToken token)
        {
            string first = null;
            for (int check = 0; check < 3; check++) {
                if (check > 0) SleepChecked(2500, token);
                string observed;
                try {
                    observed = draftTimelineVerifier.ObserveVideoPlacement(
                        draftDirectory, draftExpectedVideoPath, seconds);
                } catch (Exception ex) {
                    logger.Warn("补拖拽前无法读取草稿时间线：" + ex.Message);
                    return "";
                }
                if (first == null) first = observed;
                else if (!String.Equals(observed, first, StringComparison.Ordinal)) {
                    logger.Warn("补拖拽前草稿时间线仍在变化：" + first + " -> " + observed);
                    return "";
                }
            }
            return first ?? "";
        }

        public void MuteOriginalVideo(int timeoutSeconds, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(draftExpectedVideoPath) ||
                importedMainVideoSeconds <= 0)
                throw new CapCutOperationFailure("video_mute",
                    "CAPCUT_VIDEO_SEGMENT_MISSING",
                    CapCutFailureClassification.HardSafety,
                    "静音前没有已验证的主视频时间线片段");
            RefreshWindow();
            FocusForCoordinateAction(token);
            LocatorRule muteRule = config.GetRule("VideoTrackMute");
            if (!muteRule.HasCoordinate) throw new InvalidOperationException("缺少原视频轨静音按钮坐标");
            Rect rect = mainWindow.Current.BoundingRectangle;
            int muteX = (int)(rect.Left + rect.Width * muteRule.RelativeX);
            // The control is centered on the video-track header, 0.825 on the current
            // full-screen layout.  The legacy calibration at 0.817 lands on the header edge.
            int muteY = (int)(rect.Top + rect.Height * 0.825);
            NativeMethods.Click(muteX, muteY, 1);
            logger.Info("已点击原视频轨静音按钮中心，等待截图复核");
            SleepChecked(600, token);
            CaptureMainWindow("capcut-after-original-muted.png");
            try {
                string draftDirectory = ResolveImportDraftDirectory();
                draftTimelineVerifier.VerifyVideoStable(draftDirectory,
                    draftExpectedVideoPath, importedMainVideoSeconds, 10, token);
                logger.Info("草稿事实已确认主视频原声静音");
            } catch (DraftTimelineValidationException ex) {
                throw new CapCutOperationFailure("video_mute",
                    "CAPCUT_VIDEO_MUTE_UNCONFIRMED",
                    CapCutFailureClassification.Transient,
                    "静音动作已发送，但草稿未确认主视频静音: " + ex.Message,
                    "", "", ex);
            }
        }

        public void PlaceAudioOnTimeline(int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            if (audioPlacementAttempted) {
                VerifyPersistedAudioPlacement(timeoutSeconds, token);
                logger.Info("草稿真实轨道已确认当前独立音频；步骤重试不会重复拖入音频。");
                return;
            }
            LocatorRule source = config.GetRule("AudioItem");
            if (!source.HasCoordinate) throw new InvalidOperationException("缺少独立音频素材缩略图坐标");
            Rect rect = mainWindow.Current.BoundingRectangle;
            int fromX = (int)(rect.Left + rect.Width * source.RelativeX);
            int fromY = (int)(rect.Top + rect.Height * source.RelativeY);
            // On this calibrated full-screen timeline the project zero line is x=0.071.
            // The previous 0.13 value placed the clip around four seconds in, so use the
            // zero line itself and let CapCut snap the audio clip to it.
            int toX = (int)(rect.Left + rect.Width * 0.071);
            int toY = (int)(rect.Top + rect.Height * 0.875);
            audioPlacementAttempted = true;
            NativeMethods.Drag(fromX, fromY, toX, toY);
            // Audio is placed before any sample is appended.  A previous
            // diagnostic message dereferenced the later sample snapshot here,
            // turning a successful audio drag into a NullReferenceException.
            if (sampleTimelineBefore != null) {
                logger.Info(String.Format("音频落点：样品前主视频右边缘X={0}，视频轨Y={1}，落点=({2},{3})",
                    sampleTimelineBefore.VideoRun.Right, sampleTimelineBefore.VideoTrackCenterY, toX, toY));
            } else {
                logger.Info(String.Format("独立音频按时间线零点拖入：落点=({0},{1})", toX, toY));
            }
            logger.Info("已把指定 MP3 拖入一次，开始等待草稿保存并核验真实音频轨；后续步骤重试不会再次拖入。");
            SleepChecked(1200, token);
            CaptureMainWindow("capcut-after-audio-timeline.png");
            // Screenshot analysis is diagnostic only. The video thumbnails and
            // media-bin waveform can share CapCut's navy colours and previously
            // masqueraded as an offset narration lane. Persisted draft tracks
            // are the only correctness gate.
            try {
                AudioTimelineEvidence visual = locator.InspectAudioTimeline(mainWindow);
                logger.Info(String.Format(
                    "音频视觉诊断（不参与判定）：lanes={0}, waveformX={1}, zeroX={2}",
                    visual.LaneCount, visual.FirstWaveformX,
                    visual.TimelineZeroX));
            } catch (Exception visualError) {
                logger.Warn("音频视觉诊断不可用；继续按草稿真实轨道验证: " +
                    visualError.Message);
            }
            VerifyPersistedAudioPlacement(timeoutSeconds, token);
            logger.Info("草稿真实轨道确认：当前任务独立音频只有一条，且从时间线零点开始。");
        }

        private void VerifyPersistedAudioPlacement(int timeoutSeconds,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(draftExpectedVideoPath) ||
                String.IsNullOrWhiteSpace(importedAudioPath))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_AUDIO_IDENTITY_MISMATCH: 当前任务缺少主视频或独立音频路径");
            string draftDirectory = ResolveImportDraftDirectory();
            DraftAudioPlacementResult result =
                draftTimelineVerifier.VerifyAudioStable(draftDirectory,
                    draftExpectedVideoPath, importedAudioPath,
                    importedMainVideoSeconds, importedAudioSeconds,
                    Math.Min(15, Math.Max(3, timeoutSeconds)), token);
            logger.Info(String.Format(
                "独立音频草稿结构验证通过：主视频={0:0.000}s，音频={1:0.000}s，semantic={2}",
                result.MainDurationUs / 1000000.0,
                result.AudioDurationUs / 1000000.0,
                result.SemanticHash.Substring(0, 12)));
        }

        public void PlaceSampleAtEnd(int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            sampleTimelineBefore = timelineVision.Capture(mainWindow, importedMainVideoSeconds);
            if (importedMainVideoSeconds <= 0) {
                throw new InvalidOperationException("未读取到主视频时长，已阻止把样品放到固定时间线位置");
            }
            // Endpoint and track are derived from the live screenshot below.
            LocatorRule source = config.GetRule("SampleItem");
            if (!source.HasCoordinate) throw new InvalidOperationException("缺少样品素材缩略图坐标");
            Rect rect = mainWindow.Current.BoundingRectangle;
            int fromX = (int)(rect.Left + rect.Width * source.RelativeX);
            int fromY = (int)(rect.Top + rect.Height * source.RelativeY);
            // The visual tracker owns the dynamic endpoint.  These legacy values
            // remain only for backwards-compatible logging during the migration.
            int toX = sampleTimelineBefore.VideoRun.Right + Math.Max(8, (int)(rect.Width * 0.004));
            int toY = sampleTimelineBefore.VideoTrackCenterY;
            NativeMethods.Drag(fromX, fromY, toX, toY);
            logger.Info(String.Format("样品已按实时识别的主视频右边缘拖到同轨末尾：{0:0.000}s -> ({1}, {2})",
                importedMainVideoSeconds, toX, toY));
            SleepChecked(1800, token);
            // CapCut can show an inline HDR capability notice immediately after
            // a clip is appended.  It darkens the entire editor rather than
            // opening a normal top-level dialog, so dismiss only its explicit
            // acknowledgement button before taking the verification screenshot.
            DismissKnownInlineNotice(token, true);
            SleepChecked(500, token);
            CaptureMainWindow("capcut-after-sample-timeline.png");
            // Capture the exact original main-clip region again.  The full
            // post-append run includes the sample and therefore cannot be
            // compared with the pre-append run without false positives.
            // The screenshot is placement evidence only. CapCut can move the
            // entire timeline vertically or auto-fit it after a drop, and the
            // old comparison then mistook the audio lane for the video lane.
            // Persisted draft tracks are the sole correctness gate.
            TimelineSnapshot after = null;
            try {
                after = timelineVision.Capture(
                    mainWindow, importedMainVideoSeconds, sampleTimelineBefore);
            } catch (Exception visualError) {
                logger.Warn("样品追加后的时间线视觉诊断不可用；将只按草稿真实轨道验证: " +
                    visualError.Message);
            }
            if (String.IsNullOrWhiteSpace(importedAudioPath) ||
                String.IsNullOrWhiteSpace(importedSamplePath))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED: 当前任务缺少音频或专属样品路径，无法核验草稿轨道");
            string draftDirectory = ResolveImportDraftDirectory();
            try {
                timelineAssemblyResult = draftTimelineVerifier.VerifyStable(
                    draftDirectory, draftExpectedVideoPath, importedAudioPath,
                    importedSamplePath, importedMainVideoSeconds, importedAudioSeconds,
                    importedSampleSeconds, Math.Min(15, Math.Max(3, timeoutSeconds)), token);
            } catch (DraftTimelineValidationException ex) {
                if (!String.Equals(ex.ErrorCode,
                        "CAPCUT_DRAFT_SAMPLE_WRONG_TRACK",
                        StringComparison.OrdinalIgnoreCase)) throw;
                timelineAssemblyResult = RepairMisplacedSampleAndReopen(
                    draftDirectory, timeoutSeconds, token);
            }
            timelineAssemblyIdentity = ReadDirectDraftIdentityWithRetry(
                draftDirectory, Math.Min(10, Math.Max(3, timeoutSeconds)), token);
            if (timelineAssemblyIdentity == null ||
                String.IsNullOrWhiteSpace(timelineAssemblyIdentity.DraftId))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED: 样品轨道已正确，但草稿ID尚未保存完成");
            timelineAssemblyDirectory = Path.GetFullPath(draftDirectory);
            appendedSampleLeft = sampleTimelineBefore.VideoRun.Right;
            appendedSampleRight = after != null && after.HasVideo
                ? after.VideoRun.Right : appendedSampleLeft + 1;
            logger.Info(String.Format(
                "草稿真实轨道验证通过：主视频={0:0.000}s，样品={1:0.000}s，音频={2:0.000}s，semantic={3}",
                timelineAssemblyResult.MainDurationUs / 1000000.0,
                timelineAssemblyResult.SampleDurationUs / 1000000.0,
                timelineAssemblyResult.AudioDurationUs / 1000000.0,
                timelineAssemblyResult.SemanticHash.Substring(0, 12)));
            if (after != null) {
                logger.Info(String.Format(
                    "视觉坐标仅作诊断：拖入前Y={0}，拖入后Y={1}，不参与成功判定",
                    sampleTimelineBefore.VideoTrackCenterY, after.VideoTrackCenterY));
            }
        }

        private DraftTimelineAssemblyResult RepairMisplacedSampleAndReopen(
            string draftDirectory, int timeoutSeconds, CancellationToken token)
        {
            logger.Warn(
                "检测到当前专属样品落入第二视频轨；将关闭本轮 CapCut，按当前任务精确素材身份原子纠正后重开同一草稿");
            DirectDraftIdentity before = ReadDirectDraftIdentityWithRetry(
                draftDirectory, Math.Min(10, Math.Max(3, timeoutSeconds)), token);
            if (before == null || String.IsNullOrWhiteSpace(before.DraftId) ||
                !DraftContainsExpectedVideo(draftDirectory))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_SAMPLE_REPAIR_UNSAFE: 不能锁定当前任务草稿身份，已保留现场");

            CloseCapCutSession();
            token.ThrowIfCancellationRequested();
            DraftTimelineRepairResult repair =
                draftTimelineRepairer.RepairMisplacedSample(
                    draftDirectory, draftExpectedVideoPath, importedAudioPath,
                    importedSamplePath, importedMainVideoSeconds,
                    importedAudioSeconds, importedSampleSeconds);
            DirectDraftIdentity after = ReadDirectDraftIdentityWithRetry(
                draftDirectory, Math.Min(10, Math.Max(3, timeoutSeconds)), token);
            if (after == null ||
                !String.Equals(before.DraftId, after.DraftId,
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(before.DisplayName, after.DisplayName,
                    StringComparison.Ordinal) ||
                !DraftContainsExpectedVideo(draftDirectory))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_SAMPLE_REPAIR_UNSAFE: 修复前后草稿身份发生变化，已停止重开");

            timelineAssemblyDirectory = Path.GetFullPath(draftDirectory);
            timelineAssemblyIdentity = after;
            directDraftDirectory = timelineAssemblyDirectory;
            directDraftIdentity = after;
            directDraftStyleResult = null;
            directDraftCardWasExplicitlyOpened = false;
            logger.Info("样品轨道原子纠正并复核通过：backup=" +
                repair.BackupDirectory + "，semantic=" +
                repair.Assembly.SemanticHash.Substring(0, 12));
            CaptureDirectDraftActivationBaseline();
            LaunchOrAttach(timeoutSeconds, token);
            OpenDirectStyledDraft(timeoutSeconds, token);
            logger.Info("已精确重开纠正后的同一草稿；后续将从字幕识别继续，不重复导入视频、音频或样品");
            return repair.Assembly;
        }

        public void SetSampleSpeed(double speed, int timeoutSeconds, CancellationToken token)
        {
            // Samples are rendered to their exact target duration before import.
            // The production path must never return to CapCut's coordinate-driven
            // Speed/Animation panel. A value other than 1.00 means the dedicated
            // FFmpeg sample was not prepared correctly and must be stopped before
            // export rather than guessed from timeline pixels.
            if (Math.Abs(speed - 1.0) >= 0.001)
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_TIMING_MISMATCH: 任务专属样品未预处理为目标时长；禁止在 CapCut 内按坐标变速");
            if (timelineAssemblyResult == null)
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED: 任务专属样品尚未通过草稿真实轨道验证");

            double mediaDifference = Math.Abs(
                importedMainVideoSeconds + importedSampleSeconds - importedAudioSeconds);
            logger.Info(String.Format(
                "Prepared sample imported at 1.00x; persisted draft end verified: video={0:0.000}s, sample={1:0.000}s, audio={2:0.000}s, delta={3:0.000}s.",
                importedMainVideoSeconds, importedSampleSeconds, importedAudioSeconds, mediaDifference));
            CaptureMainWindow("capcut-prepared-sample-aligned.png");
        }

        public TimelineAssemblyCheckpoint CaptureTimelineAssemblyCheckpoint(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            if (timelineAssemblyResult == null || timelineAssemblyIdentity == null ||
                String.IsNullOrWhiteSpace(timelineAssemblyDirectory))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED: 草稿真实轨道尚未通过验证，不能建立时间线检查点");
            return BuildTimelineAssemblyCheckpoint(task, timelineAssemblyDirectory,
                timelineAssemblyIdentity, timelineAssemblyResult);
        }

        private TimelineAssemblyCheckpoint BuildTimelineAssemblyCheckpoint(
            VideoTask task, string directory, DirectDraftIdentity identity,
            DraftTimelineAssemblyResult verified)
        {
            DraftRootLeaseRecord lease = draftRootLeaseManager == null
                ? null : draftRootLeaseManager.Current;
            return new TimelineAssemblyCheckpoint {
                State = "timeline_assembled",
                DraftDirectory = Path.GetFullPath(directory),
                DraftId = identity.DraftId,
                DraftName = identity.DisplayName,
                MainVideoPath = NormalizeDraftMediaPath(task.VideoPath),
                AudioPath = NormalizeDraftMediaPath(task.AudioPath),
                SamplePath = NormalizeDraftMediaPath(
                    TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task)),
                MainMaterialId = verified.MainMaterialId,
                SampleMaterialId = verified.SampleMaterialId,
                AudioMaterialId = verified.AudioMaterialId,
                MainSegmentId = verified.MainSegmentId,
                SampleSegmentId = verified.SampleSegmentId,
                AudioSegmentId = verified.AudioSegmentId,
                SemanticHash = verified.SemanticHash,
                VerifiedFiles = verified.VerifiedFiles == null
                    ? new string[0] : verified.VerifiedFiles.ToArray(),
                DraftRootLeaseId = lease == null ? "" : lease.LeaseId ?? "",
                TaskDraftRoot = lease == null ? draftRoot : lease.TaskDraftRoot,
                ArchiveStorageVersion = lease == null ? "" : lease.ArchiveStorageVersion,
                StableTaskArchiveKey = lease == null ? "" : lease.StableTaskArchiveKey,
                ArchiveDraftRoot = lease == null ? "" : lease.ArchiveDraftRoot,
                DraftRelativePath = Path.GetFileName(directory),
                BatchId = task.HandoffBatchId ?? "",
                CanonicalTaskId = task.RemotePipelineTaskId ?? "",
                TaskId = task.Id ?? "",
                ProductionDate = task.ProductionDate ?? "",
                ManifestId = task.HandoffManifestId ?? "",
                AttemptId = task.HandoffAttemptId ?? "",
                UpdatedAt = DateTime.Now.ToString("o")
            };
        }

        public TimelineAssemblyCheckpoint CaptureVideoTimelineCheckpoint(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            string directory = ResolveImportDraftDirectory();
            double mainSeconds = WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
            DraftVideoTimelineResult verified = draftTimelineVerifier.VerifyVideoStable(
                directory, task.VideoPath, mainSeconds, 15, CancellationToken.None);
            DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                directory, 10, CancellationToken.None);
            if (identity == null || String.IsNullOrWhiteSpace(identity.DraftId))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_TIMELINE_NOT_FLUSHED: 主视频草稿身份尚未稳定");
            return BuildVideoTimelineCheckpoint(task, directory, identity, verified);
        }

        public TimelineAssemblyCheckpoint CaptureAudioTimelineCheckpoint(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            string directory = ResolveImportDraftDirectory();
            double mainSeconds = WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
            double audioSeconds = WindowsMediaMetadataReader.Read(task.AudioPath).DurationSeconds;
            DraftAudioPlacementResult verified = draftTimelineVerifier.VerifyAudioStable(
                directory, task.VideoPath, task.AudioPath, mainSeconds, audioSeconds,
                15, CancellationToken.None);
            DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                directory, 10, CancellationToken.None);
            if (identity == null || String.IsNullOrWhiteSpace(identity.DraftId))
                throw new CapCutOperationFailure("timeline_checkpoint",
                    "CAPCUT_AUDIO_CHECKPOINT_PROMOTION_FAILED",
                    CapCutFailureClassification.HardSafety,
                    "独立音频时间线草稿身份尚未稳定");
            return BuildAudioTimelineCheckpoint(task, directory, identity, verified);
        }

        public bool TryCaptureExistingTimelineAssemblyCheckpoint(VideoTask task,
            out TimelineAssemblyCheckpoint checkpoint)
        {
            checkpoint = null;
            if (task == null || String.IsNullOrWhiteSpace(draftRoot) ||
                !Directory.Exists(draftRoot)) return false;
            string samplePath = TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task);
            // A complete timeline cannot be proved without the exact sample
            // identity.  Let the audio-stage probe handle a video+audio draft
            // when no sample has been planned yet.
            if (String.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath))
                return false;
            string[] candidates = Directory.GetDirectories(draftRoot)
                .Where(DraftContainsExpectedVideo).ToArray();
            if (candidates.Length == 0) return false;
            if (candidates.Length != 1)
                throw new CapCutOperationFailure("checkpoint_recovery",
                    "CAPCUT_VIDEO_TIMELINE_MULTIPLE_DRAFTS_BLOCKED",
                    CapCutFailureClassification.HardSafety,
                    "当前任务草稿包含多个候选，禁止自动选择完整时间线");
            try {
                string directory = candidates[0];
                double mainSeconds = WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
                double audioSeconds = WindowsMediaMetadataReader.Read(task.AudioPath).DurationSeconds;
                double sampleSeconds = WindowsMediaMetadataReader.Read(samplePath).DurationSeconds;
                DraftTimelineAssemblyResult verified = draftTimelineVerifier.VerifyStable(
                    directory, task.VideoPath, task.AudioPath, samplePath,
                    mainSeconds, audioSeconds, sampleSeconds, 10,
                    CancellationToken.None);
                DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                    directory, 10, CancellationToken.None);
                if (identity == null || String.IsNullOrWhiteSpace(identity.DraftId))
                    throw new CapCutOperationFailure("checkpoint_recovery",
                        "CAPCUT_AUDIO_CHECKPOINT_PROMOTION_FAILED",
                        CapCutFailureClassification.HardSafety,
                        "完整时间线草稿身份尚未稳定");
                checkpoint = BuildTimelineAssemblyCheckpoint(task, directory,
                    identity, verified);
                logger.Info("检测到当前任务视频、独立音频和样品均已提交；已提升为完整时间线断点，不重复导入任何媒体");
                return true;
            } catch (DraftTimelineValidationException ex) {
                // A video+audio draft is a normal lower stage.  VerifyStable
                // reports this as sample missing; all other structural errors
                // (wrong sample, duplicates, offsets, unknown media) remain a
                // hard stop and must not fall through to a lower verifier.
                if (String.Equals(ex.ErrorCode, "CAPCUT_DRAFT_SAMPLE_MISSING",
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                throw new CapCutOperationFailure("checkpoint_recovery",
                    ex.ErrorCode ?? "CAPCUT_COMMITTED_MEDIA_UNKNOWN_BLOCKED",
                    CapCutFailureClassification.HardSafety,
                    "现有草稿完整时间线验证失败: " + ex.Message, "", "", ex);
            }
        }

        public bool TryCaptureExistingAudioTimelineCheckpoint(VideoTask task,
            out TimelineAssemblyCheckpoint checkpoint)
        {
            checkpoint = null;
            if (task == null || String.IsNullOrWhiteSpace(draftRoot) ||
                !Directory.Exists(draftRoot)) return false;
            string[] candidates = Directory.GetDirectories(draftRoot)
                .Where(DraftContainsExpectedVideo).ToArray();
            if (candidates.Length == 0) return false;
            if (candidates.Length != 1)
                throw new CapCutOperationFailure("checkpoint_recovery",
                    "CAPCUT_AUDIO_CHECKPOINT_DUPLICATED",
                    CapCutFailureClassification.HardSafety,
                    "当前任务草稿包含多个候选，禁止自动选择");
            try {
                string directory = candidates[0];
                double mainSeconds = WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
                double audioSeconds = WindowsMediaMetadataReader.Read(task.AudioPath).DurationSeconds;
                DraftAudioPlacementResult verified = draftTimelineVerifier.VerifyAudioStable(
                    directory, task.VideoPath, task.AudioPath, mainSeconds, audioSeconds, 10,
                    CancellationToken.None);
                DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                    directory, 10, CancellationToken.None);
                if (identity == null || String.IsNullOrWhiteSpace(identity.DraftId)) return false;
                checkpoint = BuildAudioTimelineCheckpoint(task, directory, identity, verified);
                logger.Info("检测到当前任务唯一且已对齐的独立音频时间线；已提升为音频断点，不重复导入视频或音频");
                return true;
            } catch (DraftTimelineValidationException ex) {
                // A verified video-only checkpoint is a normal lower stage,
                // not an audio conflict. Let the caller continue with the
                // video checkpoint; every other audio validation failure is
                // a real identity/structure hard stop.
                if (String.Equals(ex.ErrorCode, "CAPCUT_DRAFT_AUDIO_MISSING",
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                throw new CapCutOperationFailure("checkpoint_recovery",
                    ex.ErrorCode ?? "CAPCUT_AUDIO_CHECKPOINT_PROMOTION_FAILED",
                    CapCutFailureClassification.HardSafety,
                    "现有草稿音频阶段验证失败: " + ex.Message, "", "", ex);
            }
        }

        private TimelineAssemblyCheckpoint BuildAudioTimelineCheckpoint(
            VideoTask task, string directory, DirectDraftIdentity identity,
            DraftAudioPlacementResult verified)
        {
            DraftRootLeaseRecord lease = draftRootLeaseManager == null
                ? null : draftRootLeaseManager.Current;
            return new TimelineAssemblyCheckpoint {
                State = "audio_timeline_committed",
                DraftDirectory = Path.GetFullPath(directory),
                DraftId = identity.DraftId,
                DraftName = identity.DisplayName,
                MainVideoPath = NormalizeDraftMediaPath(task.VideoPath),
                AudioPath = NormalizeDraftMediaPath(task.AudioPath),
                SamplePath = NormalizeDraftMediaPath(TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task)),
                MainMaterialId = verified.MainMaterialId,
                AudioMaterialId = verified.AudioMaterialId,
                MainSegmentId = verified.MainSegmentId,
                AudioSegmentId = verified.AudioSegmentId,
                SemanticHash = verified.SemanticHash,
                VerifiedFiles = verified.VerifiedFiles ?? new string[0],
                DraftRootLeaseId = lease == null ? "" : lease.LeaseId ?? "",
                TaskDraftRoot = lease == null ? draftRoot : lease.TaskDraftRoot,
                ArchiveStorageVersion = lease == null ? "" : lease.ArchiveStorageVersion,
                StableTaskArchiveKey = lease == null ? "" : lease.StableTaskArchiveKey,
                ArchiveDraftRoot = lease == null ? "" : lease.ArchiveDraftRoot,
                DraftRelativePath = Path.GetFileName(directory),
                BatchId = task.HandoffBatchId ?? "",
                CanonicalTaskId = task.RemotePipelineTaskId ?? "",
                TaskId = task.Id ?? "",
                ProductionDate = task.ProductionDate ?? "",
                ManifestId = task.HandoffManifestId ?? "",
                AttemptId = task.HandoffAttemptId ?? "",
                VideoTrackCount = 1,
                AudioTrackCount = 1,
                MuteVerified = true,
                UpdatedAt = DateTime.Now.ToString("o")
            };
        }

        public bool TryCaptureExistingVideoTimelineCheckpoint(VideoTask task,
            out TimelineAssemblyCheckpoint checkpoint)
        {
            checkpoint = null;
            if (task == null || String.IsNullOrWhiteSpace(draftRoot) ||
                !Directory.Exists(draftRoot)) return false;
            string[] candidates = Directory.GetDirectories(draftRoot)
                .Where(DraftContainsExpectedVideo)
                .ToArray();
            if (candidates.Length == 0) return false;
            if (candidates.Length != 1)
                throw new CapCutOperationFailure("checkpoint_recovery",
                    "CAPCUT_VIDEO_TIMELINE_MULTIPLE_DRAFTS_BLOCKED",
                    CapCutFailureClassification.HardSafety,
                    "当前稳定归档包含多个属于本任务的草稿，禁止自动选择");
            try {
                string directory = candidates[0];
                double mainSeconds = WindowsMediaMetadataReader.Read(
                    task.VideoPath).DurationSeconds;
                DraftVideoTimelineResult verified =
                    draftTimelineVerifier.VerifyVideoStable(directory,
                        task.VideoPath, mainSeconds, 10, CancellationToken.None);
                DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                    directory, 10, CancellationToken.None);
                if (identity == null || String.IsNullOrWhiteSpace(identity.DraftId))
                    return false;
                checkpoint = BuildVideoTimelineCheckpoint(task, directory,
                    identity, verified);
                logger.Info("检测到当前任务唯一且已静音的主视频时间线；已提升为通用断点，不重复导入主视频");
                return true;
            } catch (Exception ex) {
                CapCutOperationFailure typed = ex as CapCutOperationFailure;
                if (typed != null) throw;
                throw new CapCutOperationFailure("checkpoint_recovery",
                    "CAPCUT_VIDEO_TIMELINE_CHECKPOINT_INVALID",
                    CapCutFailureClassification.HardSafety,
                    "现有任务草稿不满足安全断点门禁，已保留现场: " + ex.Message,
                    "", "", ex);
            }
        }

        private TimelineAssemblyCheckpoint BuildVideoTimelineCheckpoint(
            VideoTask task, string directory, DirectDraftIdentity identity,
            DraftVideoTimelineResult verified)
        {
            DraftRootLeaseRecord lease = draftRootLeaseManager == null
                ? null : draftRootLeaseManager.Current;
            return new TimelineAssemblyCheckpoint {
                State = "video_timeline_committed",
                DraftDirectory = Path.GetFullPath(directory),
                DraftId = identity.DraftId,
                DraftName = identity.DisplayName,
                MainVideoPath = NormalizeDraftMediaPath(task.VideoPath),
                AudioPath = NormalizeDraftMediaPath(task.AudioPath),
                SamplePath = NormalizeDraftMediaPath(
                    TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task)),
                MainMaterialId = verified.MainMaterialId,
                MainSegmentId = verified.MainSegmentId,
                SemanticHash = verified.SemanticHash,
                VerifiedFiles = verified.VerifiedFiles ?? new string[0],
                VideoTrackCount = verified.VideoTrackCount,
                AudioTrackCount = verified.AudioTrackCount,
                MuteVerified = verified.MuteVerified,
                DraftRootLeaseId = lease == null ? "" : lease.LeaseId ?? "",
                TaskDraftRoot = lease == null ? draftRoot : lease.TaskDraftRoot,
                ArchiveStorageVersion = lease == null ? "" : lease.ArchiveStorageVersion,
                StableTaskArchiveKey = lease == null ? "" : lease.StableTaskArchiveKey,
                ArchiveDraftRoot = lease == null ? "" : lease.ArchiveDraftRoot,
                DraftRelativePath = Path.GetFileName(directory),
                BatchId = task.HandoffBatchId ?? "",
                CanonicalTaskId = task.RemotePipelineTaskId ?? "",
                TaskId = task.Id ?? "",
                ProductionDate = task.ProductionDate ?? "",
                ManifestId = task.HandoffManifestId ?? "",
                AttemptId = task.HandoffAttemptId ?? "",
                UpdatedAt = DateTime.Now.ToString("o")
            };
        }

        public void RestoreAndReopenVideoTimeline(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            if (checkpoint == null || task == null ||
                !String.Equals(checkpoint.State, "video_timeline_committed",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "主视频时间线恢复检查点不存在或尚未验证");
            RequireTimelineCheckpointIdentity(checkpoint, task);
            string currentRoot = draftRootLeaseManager != null &&
                draftRootLeaseManager.Current != null
                ? Path.GetFullPath(draftRootLeaseManager.Current.TaskDraftRoot)
                : Path.GetFullPath(draftRoot ?? "");
            string relative = !String.IsNullOrWhiteSpace(checkpoint.DraftRelativePath)
                ? checkpoint.DraftRelativePath
                : Path.GetFileName((checkpoint.DraftDirectory ?? "").TrimEnd('\\', '/'));
            string currentDirectory = Path.Combine(currentRoot, relative);
            if (!Directory.Exists(currentDirectory))
                throw new DirectoryNotFoundException(
                    "video_timeline_checkpoint_current_lease_directory_missing: " +
                    currentDirectory);
            double mainSeconds = WindowsMediaMetadataReader.Read(
                task.VideoPath).DurationSeconds;
            DraftVideoTimelineResult verified = draftTimelineVerifier.VerifyVideoStable(
                currentDirectory, task.VideoPath, mainSeconds,
                Math.Min(15, Math.Max(3, timeoutSeconds)), token);
            if (!String.Equals(verified.SemanticHash, checkpoint.SemanticHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_UNEXPECTED_SEGMENT: 主视频时间线断点之后结构发生变化");
            DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                currentDirectory, Math.Min(10, Math.Max(3, timeoutSeconds)), token);
            if (identity == null ||
                !String.Equals(identity.DraftId, checkpoint.DraftId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_UNEXPECTED_SEGMENT: 主视频时间线草稿身份发生变化");
            checkpoint.DraftDirectory = currentDirectory;
            checkpoint.TaskDraftRoot = currentRoot;
            importedMainVideoSeconds = mainSeconds;
            draftExpectedVideoPath = NormalizeDraftMediaPath(task.VideoPath);
            directDraftDirectory = currentDirectory;
            directDraftIdentity = identity;
            timelineAssemblyDirectory = currentDirectory;
            timelineAssemblyIdentity = identity;
            // The persisted draft verified above equals the video-only
            // checkpoint, which rejects any audio track: this fresh session's
            // single audio drag is the first one on this draft, and no draft
            // card click is on record yet (same state as a new client run).
            audioPlacementAttempted = false;
            importedAudioPath = "";
            directDraftCardWasExplicitlyOpened = false;
            directDraftOpenAttemptUtc = DateTime.MinValue;
            CaptureDirectDraftActivationBaseline();
            LaunchOrAttach(timeoutSeconds, token);
            OpenDirectStyledDraft(timeoutSeconds, token);
            logger.Info("已从主视频时间线断点恢复同一草稿；下一步直接导入独立音频");
        }

        public void RestoreAndReopenAudioTimeline(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            if (checkpoint == null || task == null ||
                !String.Equals(checkpoint.State, "audio_timeline_committed",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("独立音频时间线恢复检查点不存在或尚未验证");
            RequireTimelineCheckpointIdentity(checkpoint, task);
            string currentRoot = draftRootLeaseManager != null &&
                draftRootLeaseManager.Current != null
                ? Path.GetFullPath(draftRootLeaseManager.Current.TaskDraftRoot)
                : Path.GetFullPath(draftRoot ?? "");
            string relative = !String.IsNullOrWhiteSpace(checkpoint.DraftRelativePath)
                ? checkpoint.DraftRelativePath
                : Path.GetFileName((checkpoint.DraftDirectory ?? "").TrimEnd('\\', '/'));
            string currentDirectory = Path.Combine(currentRoot, relative);
            if (!Directory.Exists(currentDirectory))
                throw new DirectoryNotFoundException("audio_timeline_checkpoint_current_lease_directory_missing: " + currentDirectory);
            double mainSeconds = WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
            double audioSeconds = WindowsMediaMetadataReader.Read(task.AudioPath).DurationSeconds;
            DraftAudioPlacementResult verified = draftTimelineVerifier.VerifyAudioStable(
                currentDirectory, task.VideoPath, task.AudioPath, mainSeconds, audioSeconds,
                Math.Min(15, Math.Max(3, timeoutSeconds)), token);
            if (!String.Equals(verified.SemanticHash, checkpoint.SemanticHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CAPCUT_AUDIO_CHECKPOINT_IDENTITY_MISMATCH: 音频时间线语义哈希发生变化");
            DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                currentDirectory, Math.Min(10, Math.Max(3, timeoutSeconds)), token);
            if (identity == null || !String.Equals(identity.DraftId, checkpoint.DraftId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CAPCUT_AUDIO_CHECKPOINT_IDENTITY_MISMATCH: 音频时间线草稿身份发生变化");
            checkpoint.DraftDirectory = currentDirectory;
            checkpoint.TaskDraftRoot = currentRoot;
            importedMainVideoSeconds = mainSeconds;
            importedAudioSeconds = audioSeconds;
            importedAudioPath = NormalizeDraftMediaPath(task.AudioPath);
            draftExpectedVideoPath = NormalizeDraftMediaPath(task.VideoPath);
            directDraftDirectory = currentDirectory;
            directDraftIdentity = identity;
            timelineAssemblyDirectory = currentDirectory;
            timelineAssemblyIdentity = identity;
            audioPlacementAttempted = true;
            CaptureDirectDraftActivationBaseline();
            LaunchOrAttach(timeoutSeconds, token);
            OpenDirectStyledDraft(timeoutSeconds, token);
            logger.Info("已从独立音频时间线断点恢复同一草稿；下一步直接准备结尾样品");
        }

        public void RestoreAndReopenTimelineAssembly(TimelineAssemblyCheckpoint checkpoint,
            VideoTask task, int timeoutSeconds, CancellationToken token)
        {
            if (checkpoint == null || task == null ||
                !String.Equals(checkpoint.State, "timeline_assembled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("时间线恢复检查点不存在或尚未完成真实轨道验证");
            RequireTimelineCheckpointIdentity(checkpoint, task);

            string effectiveSamplePath =
                TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task);
            if (!String.IsNullOrWhiteSpace(task.PreparedSamplePath)) {
                task.SamplePath = effectiveSamplePath;
                task.SampleSpeed = 1.0;
            }

            string currentRoot = draftRootLeaseManager != null &&
                draftRootLeaseManager.Current != null
                ? Path.GetFullPath(draftRootLeaseManager.Current.TaskDraftRoot)
                : Path.GetFullPath(draftRoot ?? "");
            string previousDirectory = Path.GetFullPath(checkpoint.DraftDirectory ?? "");
            string currentDirectory = Path.Combine(currentRoot, Path.GetFileName(previousDirectory));
            if (!Directory.Exists(currentDirectory))
                throw new DirectoryNotFoundException(
                    "timeline_checkpoint_current_lease_directory_missing: " + currentDirectory);
            checkpoint.DraftDirectory = currentDirectory;
            checkpoint.TaskDraftRoot = currentRoot;
            if (draftRootLeaseManager != null && draftRootLeaseManager.Current != null)
                checkpoint.DraftRootLeaseId = draftRootLeaseManager.Current.LeaseId;

            importedMainVideoSeconds = WindowsMediaMetadataReader.Read(task.VideoPath).DurationSeconds;
            importedAudioSeconds = WindowsMediaMetadataReader.Read(task.AudioPath).DurationSeconds;
            importedSampleSeconds = WindowsMediaMetadataReader.Read(effectiveSamplePath).DurationSeconds;
            importedAudioPath = NormalizeDraftMediaPath(task.AudioPath);
            importedSamplePath = NormalizeDraftMediaPath(effectiveSamplePath);
            draftExpectedVideoPath = NormalizeDraftMediaPath(task.VideoPath);

            DraftTimelineAssemblyResult verified = draftTimelineVerifier.VerifyStable(
                currentDirectory, task.VideoPath, task.AudioPath, effectiveSamplePath,
                importedMainVideoSeconds, importedAudioSeconds, importedSampleSeconds,
                Math.Min(15, Math.Max(3, timeoutSeconds)), token);
            if (!String.Equals(verified.SemanticHash, checkpoint.SemanticHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_UNEXPECTED_SEGMENT: 时间线检查点之后草稿轨道结构发生变化，已阻止恢复");
            DirectDraftIdentity identity = ReadDirectDraftIdentityWithRetry(
                currentDirectory, Math.Min(10, Math.Max(3, timeoutSeconds)), token);
            if (identity == null ||
                !String.Equals(identity.DraftId, checkpoint.DraftId, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(identity.DisplayName, checkpoint.DraftName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "CAPCUT_DRAFT_UNEXPECTED_SEGMENT: 时间线检查点的草稿ID或名称已变化，已阻止恢复");

            timelineAssemblyResult = verified;
            timelineAssemblyDirectory = currentDirectory;
            timelineAssemblyIdentity = identity;
            directDraftDirectory = currentDirectory;
            directDraftIdentity = identity;
            directDraftStyleResult = null;
            directDraftCardWasExplicitlyOpened = false;
            CaptureDirectDraftActivationBaseline();
            LaunchOrAttach(timeoutSeconds, token);
            OpenDirectStyledDraft(timeoutSeconds, token);
            logger.Info("已从草稿真实轨道检查点恢复编辑器；主视频、音频和样品均未重复导入");
        }

        private static void RequireTimelineCheckpointIdentity(
            TimelineAssemblyCheckpoint checkpoint, VideoTask task)
        {
            if (!TimelineCheckpointIdentityPolicy.StableIdentityMatches(checkpoint, task)) {
                bool videoOnly = String.Equals(checkpoint == null ? "" : checkpoint.State,
                    "video_timeline_committed", StringComparison.OrdinalIgnoreCase);
                bool audioOnly = String.Equals(checkpoint == null ? "" : checkpoint.State,
                    "audio_timeline_committed", StringComparison.OrdinalIgnoreCase);
                throw new InvalidOperationException(
                    (videoOnly ? "CAPCUT_VIDEO_CHECKPOINT_IDENTITY_MISMATCH" :
                        audioOnly ? "CAPCUT_AUDIO_CHECKPOINT_IDENTITY_MISMATCH" :
                        "CAPCUT_COMMITTED_SAMPLE_IDENTITY_MISMATCH") +
                    ": 时间线检查点不属于当前任务身份，已阻止串任务恢复");
            }

            // A safe-continue attempt owns a new manifest/attempt and lease,
            // but the verified media/task identity above is stable. Rebind
            // only after every stable identity field has matched.
            TimelineCheckpointIdentityPolicy.RebindTimelineEnvelope(checkpoint, task);
        }

        private static bool SameIdentity(string checkpoint, string current)
        {
            return String.Equals((checkpoint ?? "").Trim(), (current ?? "").Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        public void RunAutoCaption(int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            locator.Click("TextPanel", mainWindow, Math.Min(config.Timeouts.ControlSeconds, 4), token, false);
            SleepChecked(700, token);
            CaptureMainWindow("capcut-after-text-panel.png");
            locator.Click("AutoCaption", mainWindow, config.Timeouts.ControlSeconds, token, true);
            SleepChecked(700, token);
            CaptureMainWindow("capcut-after-auto-caption.png");
            bool clicked = locator.ClickSemanticOnly("StartRecognition", mainWindow);
            if (!clicked) {
                clicked = locator.ClickDynamicCaptionRecognitionButton(
                    mainWindow, Math.Min(6, Math.Max(2, timeoutSeconds)), token);
            }
            // A busy editor can drop the panel clicks (2026-10-12 01:48: after
            // a UIA probe timeout the left panel was still on 素材).  The
            // recognition button has not been clicked yet, so opening the
            // panel again is safe; retry the navigation at most twice.
            for (int retry = 1; !clicked && retry <= 2; retry++) {
                logger.Warn(String.Format(
                    "未找到开始识别按钮，可能是面板点击未生效；第 {0}/2 次重新打开 文本 → 智能字幕", retry));
                SleepChecked(2000, token);
                DismissEnvironmentCheckWindow();
                RefreshWindow();
                FocusForCoordinateAction(token);
                locator.Click("TextPanel", mainWindow, Math.Min(config.Timeouts.ControlSeconds, 4), token, false);
                SleepChecked(1200, token);
                locator.Click("AutoCaption", mainWindow, config.Timeouts.ControlSeconds, token, true);
                SleepChecked(1200, token);
                CaptureMainWindow("capcut-after-auto-caption-retry" + retry + ".png");
                clicked = locator.ClickSemanticOnly("StartRecognition", mainWindow) ||
                    locator.ClickDynamicCaptionRecognitionButton(
                        mainWindow, Math.Min(8, Math.Max(3, timeoutSeconds)), token);
            }
            if (!clicked) {
                throw new InvalidOperationException(
                    "CAPTION_START_BUTTON_NOT_FOUND: 智能字幕面板中未找到唯一可确认的“开始识别”按钮；未执行旧坐标盲点，已阻止后续导出。");
            }
            EnsureCaptionRecognitionStarted(token);
            CaptureMainWindow("capcut-after-start-recognition.png");

            Stopwatch watch = Stopwatch.StartNew();
            string previousCaptionSignature = "";
            int stableCaptionReads = 0;
            int nextWaitNoticeSeconds = 30;
            bool stallChecked = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                string signature = ReadCommittedCaptionSignature();
                stableCaptionReads = !String.IsNullOrWhiteSpace(signature) &&
                    String.Equals(signature, previousCaptionSignature, StringComparison.Ordinal)
                    ? stableCaptionReads + 1 : 0;
                previousCaptionSignature = signature;
                if (stableCaptionReads >= 2) {
                    logger.Info("草稿字幕片段已连续三次稳定落盘，允许保存字幕检查点");
                    return;
                }
                if (!stallChecked && String.IsNullOrWhiteSpace(signature) &&
                    watch.Elapsed.TotalSeconds >= CaptionRecognitionStatePolicy.StallCheckSeconds) {
                    stallChecked = true;
                    RecoverStalledCaptionRecognition(timeoutSeconds, token);
                }
                // CapCut can keep a visible recognition dialog open while its
                // cloud transcription is still running.  Do not replace the
                // configured 600-second caption step with the legacy 90-second
                // fallback: a progress dialog at 45% is not a failed result.
                // Keep the operation single-shot and bounded by RunStep's
                // configured timeout; periodic messages make a slow but live
                // recognition distinguishable from a frozen UI.
                if (watch.Elapsed.TotalSeconds >= nextWaitNoticeSeconds) {
                    logger.Info(String.Format(CultureInfo.InvariantCulture,
                        "智能字幕仍在等待草稿字幕轨；已等待 {0}s / 上限 {1}s；仅在界面空闲停滞时补点一次",
                        (int)watch.Elapsed.TotalSeconds, timeoutSeconds));
                    nextWaitNoticeSeconds += 30;
                }
                Thread.Sleep(1000);
            }
            throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                "CAPCUT_CAPTION_RECOGNITION_TIMEOUT: 等待 {0}s 后草稿仍无可识别字幕轨；已保留草稿，停滞补点最多一次",
                timeoutSeconds));
        }

        // One bounded look at the UI when the draft still has no captions long
        // after every successful recognition would have finished. Re-clicks at
        // most once per caption step, and only on a visibly idle panel.
        private void RecoverStalledCaptionRecognition(int timeoutSeconds, CancellationToken token)
        {
            DismissEnvironmentCheckWindow();
            RefreshWindow();
            string evidence;
            bool progress = TryDetectCaptionRecognitionProgress(true, out evidence);
            bool buttonVisible = locator.IsDynamicCaptionRecognitionButtonVisible(mainWindow);
            string shot = "capcut-caption-stall-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png";
            CaptureMainWindow(shot);
            if (!CaptionRecognitionStatePolicy.ShouldReclickStalledRecognition(
                    progress, buttonVisible, false)) {
                logger.Info(String.Format(CultureInfo.InvariantCulture,
                    "智能字幕已等待 {0}s 草稿仍无字幕；界面{1}，继续等待且不补点。证据截图 {2}",
                    CaptionRecognitionStatePolicy.StallCheckSeconds,
                    progress ? "仍在识别：" + evidence : "开始按钮不可见、状态不明", shot));
                return;
            }
            logger.Warn(String.Format(CultureInfo.InvariantCulture,
                "智能字幕已等待 {0}s：草稿无字幕、无识别进度且开始按钮仍可见，判定上次点击未生效；补点一次（本步骤仅此一次）。证据截图 {1}",
                CaptionRecognitionStatePolicy.StallCheckSeconds, shot));
            FocusForCoordinateAction(token);
            bool clicked = locator.ClickSemanticOnly("StartRecognition", mainWindow) ||
                locator.ClickDynamicCaptionRecognitionButton(
                    mainWindow, Math.Min(6, Math.Max(2, timeoutSeconds)), token);
            if (!clicked) {
                throw new InvalidOperationException(
                    "CAPTION_START_BUTTON_NOT_FOUND: 字幕识别停滞后补点时未找到唯一可确认的“开始识别”按钮；已停止。");
            }
            EnsureCaptionRecognitionStarted(token);
            CaptureMainWindow("capcut-after-start-recognition-retry.png");
        }

        private string ReadCommittedCaptionSignature()
        {
            string directory;
            // CapCut rewrites root_meta_info.json while it transcribes; a
            // sharing violation or half-written index is not a result
            // (10-14 10:17, 17:15 and 10-16 15:31 failed the caption step).
            try { directory = ResolveImportDraftDirectory(); }
            catch (IOException) { return ""; }
            catch (ArgumentException) { return ""; }
            var signatures = new List<string>();
            foreach (string path in Directory.GetFiles(directory, "draft_content.json", SearchOption.AllDirectories)) {
                try {
                    string signature = MediaImportFacts.CaptionSignature(File.ReadAllText(path));
                    if (!String.IsNullOrWhiteSpace(signature)) signatures.Add(path + ":" + signature);
                } catch (IOException) { return ""; }
                  catch (ArgumentException) { return ""; }
            }
            signatures.Sort(StringComparer.Ordinal);
            return String.Join("\n", signatures.ToArray());
        }

        // A cyan button click alone is not evidence that CapCut accepted the
        // command: a stale calibration can still land on an inactive edge of
        // the control.  Before entering the longer wait loop, require either
        // the visible recognition state or a newly-created subtitle track.
        private void EnsureCaptionRecognitionStarted(CancellationToken token)
        {
            string captionsBeforeClick = ReadCaptionSignatureOrEmpty();
            Stopwatch watch = Stopwatch.StartNew();
            int absentFrames = 0;
            DateTime lastOcrProbeUtc = DateTime.MinValue;
            while (watch.Elapsed < TimeSpan.FromSeconds(15)) {
                token.ThrowIfCancellationRequested();
                if (DismissEnvironmentCheckWindow()) {
                    // The window covered the progress dialog; probe again now.
                    lastOcrProbeUtc = DateTime.MinValue;
                }
                RefreshWindow();
                string progressEvidence;
                bool allowOcr = watch.ElapsedMilliseconds >= 900 &&
                    (lastOcrProbeUtc == DateTime.MinValue ||
                    DateTime.UtcNow - lastOcrProbeUtc >= TimeSpan.FromSeconds(5));
                bool progressSurface = TryDetectCaptionRecognitionProgress(
                    allowOcr, out progressEvidence);
                if (allowOcr) lastOcrProbeUtc = DateTime.UtcNow;
                // Only captions newly persisted in the draft count. The pixel
                // probe reported a track 0.1 s after the click on 10-22 01:23
                // while the draft never received one (600 s timeout).
                string captionsNow = ReadCaptionSignatureOrEmpty();
                bool subtitleTrack = !String.IsNullOrWhiteSpace(captionsNow) &&
                    !String.Equals(captionsNow, captionsBeforeClick, StringComparison.Ordinal);
                bool completionMarker = locator.Exists("SubtitleTrack", mainWindow) ||
                    locator.Exists("AutoCaptionComplete", mainWindow);
                if (!locator.IsDynamicCaptionRecognitionButtonVisible(mainWindow)) absentFrames++;
                else absentFrames = 0;
                if (CaptionRecognitionStatePolicy.IsCommandAccepted(progressSurface,
                    subtitleTrack, completionMarker, absentFrames)) {
                    if (progressSurface) {
                        logger.Info("已确认智能字幕识别进度已启动：" + progressEvidence +
                            "；弹窗后的开始按钮即使仍可见也不再否决本次命令。");
                    } else if (subtitleTrack) {
                        logger.Info("开始识别后草稿已写入新的字幕轨");
                    } else if (completionMarker) {
                        logger.Info("开始识别后已检测到完成标记");
                    } else {
                        logger.Info("开始识别按钮已连续两帧消失，确认 CapCut 已接受本次字幕识别命令。");
                    }
                    return;
                }
                SleepChecked(300, token);
            }

            // A single slow UIA pass can use up the 15 s window, leaving one
            // OCR attempt (2026-10-12 01:27: one pass took 40 s while the
            // dialog showed 45%).  Before failing, spend up to 20 s on the two
            // reliable signals only: centred OCR of the progress dialog and
            // captions persisted in the draft.  Nothing is clicked again.
            Stopwatch grace = Stopwatch.StartNew();
            DateTime lastGraceOcrUtc = DateTime.MinValue;
            while (grace.Elapsed < TimeSpan.FromSeconds(20)) {
                token.ThrowIfCancellationRequested();
                DismissEnvironmentCheckWindow();
                string captionsNow = ReadCaptionSignatureOrEmpty();
                if (!String.IsNullOrWhiteSpace(captionsNow) &&
                    !String.Equals(captionsNow, captionsBeforeClick, StringComparison.Ordinal)) {
                    logger.Info("开始识别后15秒内未确认进度，补充确认期内草稿已写入字幕；继续等待识别完成");
                    return;
                }
                if (DateTime.UtcNow - lastGraceOcrUtc >= TimeSpan.FromSeconds(4)) {
                    lastGraceOcrUtc = DateTime.UtcNow;
                    RefreshWindow();
                    string graceEvidence;
                    if (TryDetectCaptionRecognitionProgress(true, out graceEvidence)) {
                        logger.Info("开始识别后15秒内未确认进度，补充确认期内已确认：" + graceEvidence);
                        return;
                    }
                }
                SleepChecked(500, token);
            }

            CaptureMainWindow("capcut-recognition-command-not-accepted.png");
            throw new InvalidOperationException(
                "CAPTION_RECOGNITION_NOT_STARTED: 点击后15秒及20秒补充确认内没有进度弹窗、动态百分比、字幕轨或完成证据；已停止且不会重复点击。");
        }

        private string ReadCaptionSignatureOrEmpty()
        {
            try { return ReadCommittedCaptionSignature() ?? ""; }
            catch { return ""; }
        }

        private bool TryDetectCaptionRecognitionProgress(bool allowOcr,
            out string evidence)
        {
            evidence = "";
            int processId = 0;
            try { processId = mainWindow == null ? 0 : mainWindow.Current.ProcessId; }
            catch { }
            try {
                // Use bounded Win32 top-level discovery.  The old RootElement
                // descendant walk could hang while CapCut was generating
                // captions, making a live progress dialog look like a timeout.
                foreach (IntPtr handle in NativeMethods.GetTopLevelWindowHandles()) {
                    uint nativePid;
                    NativeMethods.GetWindowThreadProcessId(handle, out nativePid);
                    if (processId > 0 && (int)nativePid != processId) continue;
                    string nativeTitle = NativeMethods.GetWindowTitle(handle);
                    string windowName = nativeTitle;
                    if (CaptionRecognitionStatePolicy.ContainsProgressMarker(windowName)) {
                        evidence = "Win32 window: " + windowName;
                        return true;
                    }
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    IntPtr locked = GetLockedWindowHandle();
                    bool likelyDialog = NativeMethods.GetWindowClassName(handle)
                        .IndexOf("#32770", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (handle == foreground && handle != locked);
                    if (!likelyDialog) continue;
                    AutomationElement window = locator.ElementFromWindow(handle);
                    if (window == null) continue;
                    string semanticName = ControlLocator.SafeName(window);
                    if (!String.IsNullOrWhiteSpace(semanticName)) windowName = semanticName;
                    if (CaptionRecognitionStatePolicy.ContainsProgressMarker(windowName)) {
                        evidence = "UIA window: " + windowName;
                        return true;
                    }
                    AutomationElementCollection children = null;
                    try { children = window.FindAll(TreeScope.Children, Condition.TrueCondition); }
                    catch { }
                    if (children == null) continue;
                    int childCount = 0;
                    foreach (AutomationElement element in children) {
                        if (++childCount > 128) break;
                        string name = ControlLocator.SafeName(element);
                        if (CaptionRecognitionStatePolicy.ContainsProgressMarker(name)) {
                            evidence = "UIA element: " + name;
                            return true;
                        }
                        string normalized = CaptionRecognitionStatePolicy.Normalize(name);
                        if (normalized != "取消" && normalized != "cancel") continue;
                        Rect buttonBounds = element.Current.BoundingRectangle;
                        Rect editorBounds = mainWindow.Current.BoundingRectangle;
                        double centerX = buttonBounds.Left + buttonBounds.Width / 2.0;
                        double centerY = buttonBounds.Top + buttonBounds.Height / 2.0;
                        bool centeredProgressCancel = buttonBounds.Width >= 45 &&
                            centerX >= editorBounds.Left + editorBounds.Width * 0.35 &&
                            centerX <= editorBounds.Left + editorBounds.Width * 0.65 &&
                            centerY >= editorBounds.Top + editorBounds.Height * 0.35 &&
                            centerY <= editorBounds.Top + editorBounds.Height * 0.70;
                        if (!centeredProgressCancel) continue;
                        evidence = "UIA centered recognition cancel button";
                        return true;
                    }
                }
            } catch { }

            if (!allowOcr) return false;
            string screenshot = CaptureMainWindowForOcr(
                "capcut-caption-recognition-progress-probe.png");
            string marker;
            string diagnostic;
            bool ocrMatched = VisibleTextOcr.TryContainsAny(screenshot,
                new[] {
                    "字幕识别中", "正在识别", "识别中",
                    "Generating captions", "Recognizing captions"
                }, out marker, out diagnostic);
            if (ocrMatched ||
                CaptionRecognitionStatePolicy.ContainsProgressMarker(diagnostic)) {
                evidence = ocrMatched ? "Windows OCR: " + marker :
                    "Windows OCR normalized progress text";
                logger.Info("Windows OCR 已确认字幕识别进度弹窗: " +
                    evidence + "；" + diagnostic);
                return true;
            }
            // Whole-window OCR of a 2560-wide dimmed editor can miss the small
            // centred progress dialog (2026-10-12 01:27: only "PAINEL" was
            // read while the dialog showed 45%).  Retry on an enlarged crop of
            // the editor centre, where that dialog is drawn.
            string centered = CropCenterForOcr(screenshot,
                "capcut-caption-recognition-progress-center.png");
            string centeredDiagnostic;
            if (!String.IsNullOrWhiteSpace(centered) &&
                VisibleTextOcr.TryContainsAny(centered, CaptionProgressOcrMarkers,
                    out marker, out centeredDiagnostic)) {
                evidence = "Windows OCR (centre crop): " + marker;
                logger.Info("Windows OCR 已在编辑器中心区域确认字幕识别进度: " + centeredDiagnostic);
                return true;
            }
            logger.Info("字幕识别进度OCR暂未命中: " + diagnostic);
            return false;
        }

        private static readonly string[] CaptionProgressOcrMarkers = {
            "字幕识别中", "正在识别", "识别中",
            "Generating captions", "Recognizing captions"
        };

        // Crop x 30-70 %, y 30-75 % of the saved editor screenshot and scale
        // it 2x for OCR.  Returns "" when the crop cannot be produced.
        internal static string CropCenterForOcr(string screenshotPath, string fileName)
        {
            if (String.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath)) return "";
            try {
                using (var source = new System.Drawing.Bitmap(screenshotPath)) {
                    var crop = new System.Drawing.Rectangle(
                        (int)(source.Width * 0.30), (int)(source.Height * 0.30),
                        (int)(source.Width * 0.40), (int)(source.Height * 0.45));
                    if (crop.Width < 10 || crop.Height < 10) return "";
                    using (var scaled = new System.Drawing.Bitmap(crop.Width * 2, crop.Height * 2))
                    using (var graphics = System.Drawing.Graphics.FromImage(scaled)) {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(source,
                            new System.Drawing.Rectangle(0, 0, scaled.Width, scaled.Height),
                            crop, System.Drawing.GraphicsUnit.Pixel);
                        string path = Path.Combine(Path.GetDirectoryName(screenshotPath), fileName);
                        scaled.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        return path;
                    }
                }
            } catch { return ""; }
        }

        public void ApplySubtitleStyle(string stylePreset, int timeoutSeconds, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(stylePreset) ||
                String.Equals(stylePreset, "Default", StringComparison.OrdinalIgnoreCase)) {
                logger.Info("字幕样式使用 CapCut 默认设置");
                return;
            }
            bool styleLab = String.Equals(stylePreset, "LabZYModern", StringComparison.OrdinalIgnoreCase);
            if (!styleLab && !String.Equals(stylePreset, "CompanyStandard", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("不支持的字幕样式: " + stylePreset);
            }

            RefreshWindow();
            FocusForCoordinateAction(token);
            if (!locator.ClickFirstCaptionClip("SubtitleTrackProbe", "FirstCaptionAtZeroProbe", mainWindow)) {
                throw new InvalidOperationException("未能在时间线上选择第一条字幕，无法应用公司字幕样式");
            }
            SleepChecked(700, token);
            CaptureMainWindow("capcut-after-caption-selected.png");

            // These four controls were added after the original 32-point calibration.
            // The fallback coordinates below were verified on this home PC's unscaled
            // 2560x1400 CapCut window; existing user calibration still takes precedence.
            LocatorRule applyAll = CaptionRule("CaptionApplyAll", 0.684, 0.096);
            LocatorRule font = CaptionRule("CaptionFontField", 0.837, 0.212);
            LocatorRule size = CaptionRule("CaptionFontSizeField", 0.979, 0.252);
            // Company style is the eleventh item from the left on the first preset row.
            LocatorRule preset = CaptionRule("CaptionCompanyPreset", 0.885, 0.460);

            if (!IsCyanNear(applyAll)) {
                ClickRelative(applyAll);
                SleepChecked(350, token);
            }
            if (!IsCyanNear(applyAll)) {
                throw new InvalidOperationException("未能确认“对全部主字幕生效”已开启");
            }

            if (styleLab) {
                logger.Info("隔离字幕样式实验：尝试搜索并选择 ZY Modern；正式客户端不会执行该操作");
                SelectCaptionFontFromPopup(font, token);
            } else {
                logger.Info("当前批次按用户要求保留 CapCut 现有字体，不自动搜索或切换字体");
            }
            SetCaptionSize(size, config.CompanySubtitleFontSize, token);
            NativeMethods.PressEnter();
            SleepChecked(350, token);
            CaptureMainWindow("capcut-subtitle-style-before-scroll.png");
            System.Drawing.Point presetPoint = FindTransparentSubtitlePreset(preset, token);
            CaptureMainWindow("capcut-subtitle-style-after-scroll.png");
            NativeMethods.Click(presetPoint.X, presetPoint.Y, 1);
            logger.Info(String.Format("已点击“预设样式”中的透明背景字幕项: ({0}, {1})",
                presetPoint.X, presetPoint.Y));
            SleepChecked(500, token);
            CaptureMainWindow("capcut-subtitle-style-after-click.png");
            if (!IsCyanNear(preset)) {
                throw new InvalidOperationException(
                    "点击公司字幕预设后未检测到青色选中边框，已停止导出；不会执行后续导出");
            }
            logger.Info("已确认公司字幕预设显示青色选中边框");

            VerifyCompanyCaptionStyle(font, size, styleLab, token);

            logger.Info(String.Format(styleLab
                ? "隔离字幕样式实验已应用：请求字体 {0} / 字号 {1} / 公司预设"
                : "字幕样式已应用到全部主字幕：保留当前字体 / 字号 {1} / 公司预设",
                config.CompanySubtitleFont, config.CompanySubtitleFontSize));
            CaptureMainWindow("capcut-after-subtitle-style.png");
        }

        private System.Drawing.Point FindTransparentSubtitlePreset(LocatorRule preset, CancellationToken token)
        {
            Rect bounds = mainWindow.Current.BoundingRectangle;
            int presetX = (int)(bounds.Left + bounds.Width * preset.RelativeX);
            int presetY = (int)(bounds.Top + bounds.Height * preset.RelativeY);
            int textTabX = (int)(bounds.Left + bounds.Width * 0.744);
            int textTabY = (int)(bounds.Top + bounds.Height * 0.055);
            int scrollX = (int)(bounds.Left + bounds.Width * 0.86);
            int scrollY = (int)(bounds.Top + bounds.Height * 0.40);

            // CapCut 9.0 keeps the real preset grid two wheel notches below the text
            // controls. Re-enter the Text tab first; otherwise a wheel event can be
            // delivered to the multi-selected material properties panel.
            // The visible caption panel is already active after the first real caption was
            // selected.  This CapCut version does not expose its font combobox to UIA.
            if (false) {
                throw new InvalidOperationException("点击“文本”后字幕文本面板不可见");
            }

            for (int notch = 0; notch < 0; notch++) {
                NativeMethods.ReleaseModifierKeys();
                NativeMethods.ScrollVertical(scrollX, scrollY, -120);
                SleepChecked(450, token);
            }
            CaptureMainWindow("capcut-subtitle-preset-after-two-scrolls.png");
            logger.Info("已按“文本 -> 向下滚动两格”恢复字幕预设区域，并使用本机记忆坐标");
            return new System.Drawing.Point(presetX, presetY);
        }

        private LocatorRule CaptionRule(string action, double fallbackX, double fallbackY)
        {
            LocatorRule configured = config.GetRule(action);
            if (configured.HasCoordinate) return configured;
            return new LocatorRule { RelativeX = fallbackX, RelativeY = fallbackY };
        }

        public bool UsesDirectDraftSubtitleStyle(string stylePreset)
        {
            // Isolated laboratories always use the direct-draft workflow.
            // Formal workbench handoffs explicitly carry CompanyStandard; that
            // value selects the same identity-locked and verified workflow.
            // Manual Default tasks retain CapCut's original behavior.
            directDraftStyleRequested =
                RuntimeModes.ShouldUseDirectDraftSubtitleStyle(stylePreset);
            return directDraftStyleRequested;
        }

        public void SaveDraftForDirectSubtitleStyle(int timeoutSeconds, CancellationToken token)
        {
            if (!directDraftStyleRequested)
                throw new InvalidOperationException("草稿直改仅允许在隔离字幕样式实验客户端中运行");
            // The timeline checkpoint already proved the exact draft and its
            // source media. Keep that directory through caption save; a new
            // safe-continue lease has migrated it, so it is no longer a
            // newly-created folder relative to this process baseline.
            string candidate = !String.IsNullOrWhiteSpace(timelineAssemblyDirectory) &&
                Directory.Exists(timelineAssemblyDirectory) &&
                DraftContainsExpectedVideo(timelineAssemblyDirectory)
                ? Path.GetFullPath(timelineAssemblyDirectory)
                : GetUniqueCreatedDraftDirectory();
            directDraftCardWasExplicitlyOpened = false;
            WaitForDraftFilesToSettle(candidate, Math.Max(4, Math.Min(timeoutSeconds, 15)), token);
            logger.Info("已保存并唯一锁定本轮实验草稿: " + Path.GetFileName(candidate));
            CloseCapCutSession();
            token.ThrowIfCancellationRequested();
            string confirmed = Path.GetFullPath(candidate);
            if (!Directory.Exists(confirmed) ||
                !DraftContainsExpectedVideo(confirmed))
                throw new InvalidOperationException("关闭 CapCut 前后唯一草稿身份变化，已停止直改字幕样式");
            directDraftDirectory = confirmed;
            // CapCut can keep the just-saved metadata file locked for a few
            // seconds after its visible window has closed.  Do not weaken the
            // identity check or guess another draft card: wait for this exact
            // file to become readable, then continue only with its draft id.
            directDraftIdentity = ReadDirectDraftIdentityWithRetry(confirmed,
                Math.Max(3, Math.Min(timeoutSeconds, 15)), token);
            if (directDraftIdentity == null || String.IsNullOrWhiteSpace(directDraftIdentity.DraftId) ||
                String.IsNullOrWhiteSpace(directDraftIdentity.DisplayName)) {
                throw new InvalidOperationException("无法锁定本轮草稿的 draft_id 与名称，已停止直改字幕样式实验");
            }
            logger.Info("已锁定实验草稿身份: draft=" + directDraftIdentity.DisplayName +
                "，draftId=" + directDraftIdentity.DraftId +
                "，created=" + directDraftIdentity.CreatedOrdinal);
        }

        public CaptionDraftCheckpoint CaptureCaptionDraftCheckpoint(VideoTask task)
        {
            if (task == null) throw new ArgumentNullException("task");
            if (directDraftIdentity == null ||
                String.IsNullOrWhiteSpace(directDraftDirectory) ||
                String.IsNullOrWhiteSpace(draftExpectedVideoPath))
                throw new InvalidOperationException(
                    "字幕草稿尚未完成唯一身份锁定，不能建立字幕检查点");

            // SaveDraftForDirectSubtitleStyle has closed our CapCut session.
            // Reconcile only an exactly validated pre-caption root cache; the
            // engine retains its original bytes as a backup before writing.
            new CapCutCliDraftStyleEngine().ReconcileSavedCaptionMirrors(directDraftDirectory, logger);
            CapCutCliDraftStyleEngineResult inspected =
                new CapCutCliDraftStyleEngine().Inspect(directDraftDirectory, logger);
            CapCutCliDraftStyleGroup group = RequireConsistentCaptionInspection(inspected);
            return new CaptionDraftCheckpoint {
                State = "captions_saved",
                DraftDirectory = Path.GetFullPath(directDraftDirectory),
                DraftId = directDraftIdentity.DraftId,
                DraftName = directDraftIdentity.DisplayName,
                SourceVideoPath = NormalizeDraftMediaPath(task.VideoPath),
                AudioPath = NormalizeDraftMediaPath(task.AudioPath),
                SamplePath = NormalizeDraftMediaPath(task.SamplePath),
                CaptionCount = group.CaptionCount,
                CaptionIdentityHash = group.CaptionIdentityHash,
                TimingAndTrackHash = group.TimingAndTrackHash,
                ProtectedMediaHash = group.ProtectedMediaHash,
                VerifiedFiles = inspected.Groups.SelectMany(item =>
                    item.Files ?? new string[0]).Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray(),
                AppVersion = group.AppVersion,
                SchemaVersion = group.SchemaVersion,
                NewVersion = group.NewVersion,
                DraftRootLeaseId = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.LeaseId : "",
                TaskDraftRoot = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.TaskDraftRoot : draftRoot,
                ArchiveStorageVersion = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.ArchiveStorageVersion : "",
                StableTaskArchiveKey = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.StableTaskArchiveKey : "",
                ArchiveDraftRoot = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.ArchiveDraftRoot : "",
                DraftRelativePath = Path.GetFileName(directDraftDirectory),
                BatchId = task.HandoffBatchId ?? "",
                CanonicalTaskId = task.RemotePipelineTaskId ?? "",
                TaskId = task.Id ?? "",
                ProductionDate = task.ProductionDate ?? "",
                ManifestId = task.HandoffManifestId ?? "",
                AttemptId = task.HandoffAttemptId ?? "",
                UpdatedAt = DateTime.Now.ToString("o")
            };
        }

        public void RestoreCaptionDraftCheckpoint(CaptionDraftCheckpoint checkpoint,
            VideoTask task)
        {
            if (checkpoint == null || task == null ||
                !String.Equals(checkpoint.State, "captions_saved",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "字幕检查点不存在或尚未完成字幕保存");
            if (!TimelineCheckpointIdentityPolicy.CaptionStableIdentityMatches(
                    checkpoint, task))
                throw new InvalidOperationException(
                    "CAPCUT_CAPTION_CHECKPOINT_IDENTITY_MISMATCH: 字幕检查点不属于当前任务或媒体，已阻止继承");

            if (!String.IsNullOrWhiteSpace(task.PreparedSamplePath)) {
                task.SamplePath = TimelineCheckpointIdentityPolicy.EffectiveSamplePath(task);
                task.SampleSpeed = 1.0;
            }
            RebindCaptionDraftCheckpointToCurrentLease(checkpoint);
            string directory = Path.GetFullPath(checkpoint.DraftDirectory ?? "");
            DirectDraftIdentity identity = ReadDirectDraftIdentityOrTaskIndex(
                directory);
            draftExpectedVideoPath = NormalizeDraftMediaPath(task.VideoPath);
            if (identity == null ||
                !SameIdentity(identity.DraftId, checkpoint.DraftId) ||
                !String.Equals(identity.DisplayName, checkpoint.DraftName,
                    StringComparison.Ordinal) ||
                !DraftContainsExpectedVideo(directory))
                throw new InvalidOperationException(
                    "CAPCUT_CAPTION_CHECKPOINT_DRAFT_CHANGED: 字幕草稿身份或源视频已变化，已阻止恢复");

            CapCutCliDraftStyleEngineResult inspected =
                new CapCutCliDraftStyleEngine().Inspect(directory, logger);
            CapCutCliDraftStyleGroup group = RequireConsistentCaptionInspection(inspected);
            if (group.CaptionCount != checkpoint.CaptionCount ||
                !SameIdentity(group.CaptionIdentityHash, checkpoint.CaptionIdentityHash) ||
                !SameIdentity(group.TimingAndTrackHash, checkpoint.TimingAndTrackHash) ||
                !SameIdentity(group.ProtectedMediaHash, checkpoint.ProtectedMediaHash) ||
                !SameIdentity(group.AppVersion, checkpoint.AppVersion) ||
                group.SchemaVersion != checkpoint.SchemaVersion ||
                !SameIdentity(group.NewVersion, checkpoint.NewVersion))
                throw new InvalidOperationException(
                    "CAPCUT_CAPTION_CHECKPOINT_CONTENT_CHANGED: 字幕、时间线、媒体或版本指纹已变化，已阻止恢复");

            TimelineCheckpointIdentityPolicy.RebindCaptionEnvelope(checkpoint, task);
            directDraftDirectory = directory;
            directDraftIdentity = identity;
            directDraftStyleResult = null;
            directDraftStyleRequested = true;
            logger.Info("已恢复字幕保存检查点，下一步只应用公司字幕样式: draft=" +
                identity.DisplayName + ", captions=" + group.CaptionCount);
        }

        private void RebindCaptionDraftCheckpointToCurrentLease(
            CaptionDraftCheckpoint checkpoint)
        {
            if (draftRootLeaseManager == null || draftRootLeaseManager.Current == null)
                return;
            DraftRootLeaseRecord lease = draftRootLeaseManager.Current;
            string previousDirectory = Path.GetFullPath(checkpoint.DraftDirectory ?? "");
            string currentRoot = Path.GetFullPath(lease.TaskDraftRoot ?? "");
            string currentDirectory = Path.Combine(currentRoot,
                Path.GetFileName(previousDirectory));
            if (!Directory.Exists(currentDirectory))
                throw new DirectoryNotFoundException(
                    "caption_checkpoint_current_lease_directory_missing: " + currentDirectory);
            checkpoint.VerifiedFiles = (checkpoint.VerifiedFiles ?? new string[0])
                .Select(path => RebaseDirectDraftCheckpointPath(path,
                    previousDirectory, currentDirectory)).ToArray();
            checkpoint.DraftDirectory = currentDirectory;
            checkpoint.TaskDraftRoot = currentRoot;
            checkpoint.DraftRootLeaseId = lease.LeaseId;
            if (checkpoint.VerifiedFiles.Length == 0 ||
                checkpoint.VerifiedFiles.Any(path =>
                    String.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                    !Path.GetFullPath(path).StartsWith(
                        currentDirectory.TrimEnd('\\') + "\\",
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "caption_checkpoint_current_lease_files_missing");
        }

        private static CapCutCliDraftStyleGroup RequireConsistentCaptionInspection(
            CapCutCliDraftStyleEngineResult result)
        {
            CapCutCliDraftStyleGroup[] groups = result == null
                ? null : result.Groups;
            if (groups == null || groups.Length == 0 || groups[0].CaptionCount <= 0)
                throw new InvalidDataException(
                    "字幕草稿检查未找到有效字幕或草稿镜像");
            CapCutCliDraftStyleGroup first = groups[0];
            if (groups.Any(item => item.CaptionCount != first.CaptionCount ||
                !SameIdentity(item.CaptionIdentityHash, first.CaptionIdentityHash) ||
                !SameIdentity(item.TimingAndTrackHash, first.TimingAndTrackHash) ||
                !SameIdentity(item.ProtectedMediaHash, first.ProtectedMediaHash) ||
                !SameIdentity(item.AppVersion, first.AppVersion) ||
                item.SchemaVersion != first.SchemaVersion ||
                !SameIdentity(item.NewVersion, first.NewVersion)))
                throw new InvalidDataException(
                    "根草稿与活动Timeline的字幕、轨道、媒体或版本指纹不一致");
            return first;
        }

        public void ApplyDirectDraftSubtitleStyle(int timeoutSeconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(directDraftDirectory))
                throw new InvalidOperationException("尚未锁定唯一草稿，不能写入字幕样式");
            var patcher = new DirectDraftSubtitleStylePatcher(
                config.CompanySubtitleFontPath, directDraftCaptionTargetY);
            directDraftStyleResult = patcher.Apply(directDraftDirectory, logger);
            logger.Info("字幕草稿已原子写入；备份保留在: " + directDraftStyleResult.BackupDirectory);
        }

        public DirectDraftCheckpoint CaptureDirectDraftCheckpoint()
        {
            if (directDraftIdentity == null || directDraftStyleResult == null ||
                String.IsNullOrWhiteSpace(directDraftDirectory) ||
                String.IsNullOrWhiteSpace(draftExpectedVideoPath)) {
                throw new InvalidOperationException(
                    "字幕草稿尚未完成唯一身份锁定和样式写入，不能建立恢复检查点");
            }
            return new DirectDraftCheckpoint {
                State = "style_applied",
                DraftDirectory = Path.GetFullPath(directDraftDirectory),
                DraftId = directDraftIdentity.DraftId,
                DraftName = directDraftIdentity.DisplayName,
                SourceVideoPath = draftExpectedVideoPath,
                BackupDirectory = directDraftStyleResult.BackupDirectory,
                CaptionCount = directDraftStyleResult.CaptionCount,
                PatchedFiles = directDraftStyleResult.PatchedFiles == null
                    ? new string[0]
                    : directDraftStyleResult.PatchedFiles.ToArray(),
                DraftRootLeaseId = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.LeaseId : "",
                TaskDraftRoot = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.TaskDraftRoot : draftRoot,
                ArchiveStorageVersion = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.ArchiveStorageVersion : "",
                StableTaskArchiveKey = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.StableTaskArchiveKey : "",
                ArchiveDraftRoot = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? draftRootLeaseManager.Current.ArchiveDraftRoot : "",
                DraftRelativePath = Path.GetFileName(directDraftDirectory),
                UpdatedAt = DateTime.Now.ToString("o")
            };
        }

        public void RestoreDirectDraftCheckpoint(DirectDraftCheckpoint checkpoint, string expectedVideoPath)
        {
            if (checkpoint == null ||
                !String.Equals(checkpoint.State, "style_applied", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("草稿恢复检查点不存在或尚未完成样式写入");

            string normalizedExpected = NormalizeDraftMediaPath(expectedVideoPath);
            string normalizedCheckpointSource = NormalizeDraftMediaPath(checkpoint.SourceVideoPath);
            if (String.IsNullOrWhiteSpace(normalizedExpected) ||
                !String.Equals(normalizedExpected, normalizedCheckpointSource, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("草稿恢复检查点不属于当前源视频，已阻止串任务恢复");

            RebindDirectDraftCheckpointToCurrentLease(checkpoint);
            string directory = Path.GetFullPath(checkpoint.DraftDirectory ?? "");
            string root = Path.GetFullPath(draftRoot ?? "");
            if (!directory.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory))
                throw new InvalidOperationException("草稿恢复检查点目录不存在或超出本机CapCut草稿根目录");

            draftExpectedVideoPath = normalizedExpected;
            DirectDraftIdentity identity = ReadDirectDraftIdentityOrTaskIndex(
                directory);
            if (identity == null ||
                !String.Equals(identity.DraftId, checkpoint.DraftId, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(identity.DisplayName, checkpoint.DraftName, StringComparison.Ordinal) ||
                !DraftContainsExpectedVideo(directory))
                throw new InvalidOperationException(
                    "草稿恢复检查点的draft_id、草稿名称或源视频身份已经变化，已阻止恢复");

            string[] patchedFiles = checkpoint.PatchedFiles ?? new string[0];
            if (patchedFiles.Length == 0 || patchedFiles.Any(path =>
                String.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !Path.GetFullPath(path).StartsWith(directory.TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("草稿恢复检查点缺少完整的样式文件清单");

            var restoredResult = new DirectDraftStyleResult {
                DraftDirectory = directory,
                BackupDirectory = checkpoint.BackupDirectory,
                CaptionCount = checkpoint.CaptionCount,
                PatchedFiles = patchedFiles
            };
            new DirectDraftSubtitleStylePatcher(
                config.CompanySubtitleFontPath, directDraftCaptionTargetY)
                .VerifyPersisted(directory, patchedFiles);
            directDraftDirectory = directory;
            directDraftIdentity = identity;
            directDraftStyleResult = restoredResult;
            directDraftStyleRequested = true;
            logger.Info("已恢复字幕草稿检查点: draft=" + identity.DisplayName +
                ", draftId=" + identity.DraftId + ", source=" + Path.GetFileName(expectedVideoPath));
        }

        private void RebindDirectDraftCheckpointToCurrentLease(
            DirectDraftCheckpoint checkpoint)
        {
            if (checkpoint == null || draftRootLeaseManager == null ||
                    draftRootLeaseManager.Current == null) return;

            DraftRootLeaseRecord lease = draftRootLeaseManager.Current;
            string currentRoot = Path.GetFullPath(lease.TaskDraftRoot ?? "");
            string previousDirectory = Path.GetFullPath(
                checkpoint.DraftDirectory ?? "");
            string currentDirectory = Path.Combine(currentRoot,
                Path.GetFileName(previousDirectory));
            if (!Directory.Exists(currentDirectory))
                throw new DirectoryNotFoundException(
                    "draft_checkpoint_current_lease_directory_missing: " +
                    currentDirectory);

            if (!String.Equals(previousDirectory, currentDirectory,
                    StringComparison.OrdinalIgnoreCase)) {
                checkpoint.PatchedFiles = (checkpoint.PatchedFiles ??
                        new string[0])
                    .Select(path => RebaseDirectDraftCheckpointPath(path,
                        previousDirectory, currentDirectory)).ToArray();
                checkpoint.DraftDirectory = currentDirectory;
                logger.Info("Rebound direct-draft checkpoint to the current task lease: " +
                    lease.LeaseId);
            }

            checkpoint.TaskDraftRoot = currentRoot;
            checkpoint.DraftRootLeaseId = lease.LeaseId;
            string[] patched = checkpoint.PatchedFiles ?? new string[0];
            if (patched.Length == 0 || patched.Any(path =>
                    String.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                    !Path.GetFullPath(path).StartsWith(
                        currentDirectory.TrimEnd('\\') + "\\",
                        StringComparison.OrdinalIgnoreCase))) {
                throw new InvalidDataException(
                    "draft_checkpoint_current_lease_files_missing");
            }
        }

        private static string RebaseDirectDraftCheckpointPath(string path,
            string previousDirectory, string currentDirectory)
        {
            if (String.IsNullOrWhiteSpace(path)) return path;
            string full = Path.GetFullPath(path);
            string prefix = previousDirectory.TrimEnd('\\') + "\\";
            if (!full.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)) return full;
            return Path.Combine(currentDirectory,
                full.Substring(prefix.Length));
        }

        public void ReopenAndVerifyDirectDraftSubtitleStyle(int timeoutSeconds, CancellationToken token)
        {
            if (directDraftStyleResult == null || String.IsNullOrWhiteSpace(directDraftDirectory))
                throw new InvalidOperationException("缺少草稿样式写入结果，不能继续验证或导出");
            var patcher = new DirectDraftSubtitleStylePatcher(
                config.CompanySubtitleFontPath, directDraftCaptionTargetY);
            try {
                // Every reopen session starts with no card click on record, as
                // in a freshly started client, so an editor that CapCut
                // restores on its own can never pass as the clicked draft when
                // the batch runner retries in a new session.
                directDraftCardWasExplicitlyOpened = false;
                directDraftOpenAttemptUtc = DateTime.MinValue;
                CaptureDirectDraftActivationBaseline();
                LaunchOrAttach(timeoutSeconds, token);
                OpenDirectStyledDraft(timeoutSeconds, token);
                patcher.VerifyPersisted(directDraftDirectory, directDraftStyleResult.PatchedFiles);
                logger.Info("已重新打开同一草稿并通过字体、字号、预设样式持久化验证");
            } catch {
                // Reopen/card-location failure does not invalidate the already
                // verified draft files. Keep them for an exact checkpoint retry.
                logger.Error("草稿重开失败；已保留样式写入结果和恢复检查点，停止导出");
                throw;
            }
        }

        private void OpenDirectStyledDraft(int timeoutSeconds, CancellationToken token)
        {
            string displayName = ResolveDraftDisplayName(directDraftDirectory);
            if (String.IsNullOrWhiteSpace(displayName))
                throw new InvalidOperationException("无法读取唯一草稿显示名称，不能安全重新打开实验草稿");
            if (!HasLockedReopenSourceEvidence(directDraftDirectory))
                throw new InvalidOperationException("唯一实验草稿不再包含本轮源视频身份，已阻止把样式验证误记为重开成功");

            // A continuous coloured region in the timeline is useful evidence that
            // captions exist, but it is not proof that CapCut reopened THIS draft.
            // The direct-style experiment therefore requires all three evidence
            // points below before it can proceed to export:
            //   1. the locked on-disk draft still references this task's source;
            //   2. a real caption clip is visible in the reopened editor; and
            //   3. the editor exposes its top-level Export entry.
            // Screenshots are retained for manual audit rather than being inferred
            // solely from a log line.
            // Exactly one click is allowed.  Repeated blind clicks previously
            // opened an older SPC-floor draft while validating a wall-board task.
            WaitForDirectDraftHomeReady(displayName, timeoutSeconds, token);
            directDraftHomeHandle = GetLockedWindowHandle();
            directDraftEditorHandle = IntPtr.Zero;
            LogCapCutWindow("direct-draft homepage locked", directDraftHomeHandle);
            CaptureMainWindow("capcut-direct-style-reopen-home.png");

            DismissVerifiedHomePromotion(token);
            AutomationElement tile = FindVisibleNamedElement(mainWindow, displayName);
            bool taskScopedDraftRoot = HasTaskDraftRootLease;
            bool opened = false;
            if (tile != null) {
                FocusForCoordinateAction(token);
                directDraftOpenAttemptUtc = DateTime.UtcNow;
                directDraftCardWasExplicitlyOpened = true;
                ControlLocator.ClickElement(tile, DraftCardVision.PhysicalOpenClickCount);
                logger.Info("通过 UIA 草稿显示名仅点击一次打开唯一实验草稿: " + displayName);
                opened = true;
            } else if (taskScopedDraftRoot) {
                opened = TryOpenOnlyTaskDraftCard(displayName, timeoutSeconds, token);
            } else {
                opened = TryOpenLockedDraftFirstCard(displayName, timeoutSeconds, token);
            }
            if (!opened)
                throw new RecoverableDraftReopenException(
                    "draft_card_location_failed",
                    "等待重开草稿，可安全继续：专属目录中的唯一草稿未能在当前页面稳定定位；未执行点击");

            SleepChecked(2500, token);
            if (!BindReopenedDirectDraftEditor(timeoutSeconds, token)) {
                throw new RecoverableDraftReopenException(
                    "draft_editor_binding_failed",
                    "等待重开草稿，可安全继续：草稿卡只点击了一次，但未绑定到本轮已验证编辑器窗口；已停止导出");
            }
            CaptureMainWindow("capcut-direct-style-reopen-after-card-click.png");
            if (!WaitForDirectDraftExportReady(timeoutSeconds, token)) {
                throw new TimeoutException("已绑定本轮正确草稿编辑器，但 CapCut 初始化或导出入口未在安全时限内就绪；已停止导出");
            }
            if (IsDirectStyledDraftEditorReady(displayName, token)) return;
            throw new TimeoutException("草稿卡只允许点击一次，但未以“draft_id + 源视频 + 字幕轨 + 导出入口”重新打开目标草稿；已停止导出");
        }

        private bool WaitForDirectDraftExportReady(int timeoutSeconds, CancellationToken token)
        {
            int waitSeconds = CapCutHomeShellPolicy.EditorInitializationTimeoutSeconds(timeoutSeconds);
            Stopwatch watch = Stopwatch.StartNew();
            int lastLoggedSecond = -1;
            int stableReadyFrames = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(waitSeconds)) {
                token.ThrowIfCancellationRequested();
                IntPtr currentHandle = GetLockedWindowHandle();
                bool editorBound = directDraftEditorHandle != IntPtr.Zero &&
                    currentHandle == directDraftEditorHandle;
                bool responsive = editorBound && !NativeMethods.IsHungAppWindow(currentHandle);
                bool homeVisible = editorBound && IsCapCutHomeShell(currentHandle);
                bool exportVisible = responsive && !homeVisible &&
                    (locator.HasExactSemanticControl("Export", mainWindow) ||
                     CapCutHomeShellPolicy.Classify(mainWindow.Current.ClassName) ==
                        CapCutWindowRole.Editor);
                if (CapCutHomeShellPolicy.CanProceedToExport(editorBound, responsive,
                    homeVisible, exportVisible)) {
                    stableReadyFrames++;
                    if (stableReadyFrames >= 2) {
                        logger.Info("CapCut 正确草稿编辑器与导出入口已连续两次稳定就绪；耗时=" +
                            watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
                        CaptureMainWindow("capcut-direct-style-reopen-export-ready.png");
                        return true;
                    }
                } else {
                    stableReadyFrames = 0;
                }

                int elapsedSecond = (int)watch.Elapsed.TotalSeconds;
                if (elapsedSecond == 0 || elapsedSecond >= lastLoggedSecond + 3) {
                    lastLoggedSecond = elapsedSecond;
                    logger.Info("CapCut 草稿编辑器已绑定，等待窗口响应和导出入口就绪: " +
                        "responsive=" + responsive + ", home=" + homeVisible +
                        ", export=" + exportVisible + ", elapsed=" + elapsedSecond + "s/" + waitSeconds + "s");
                }
                SleepChecked(350, token);
            }

            CaptureMainWindow("capcut-direct-style-reopen-export-timeout.png");
            return false;
        }

        private void WaitForDirectDraftHomeReady(string displayName, int timeoutSeconds,
            CancellationToken token)
        {
            // On this CapCut build the first visible top-level window is often
            // a full-screen dark/empty QML shell.  In the past the reopen code
            // treated that shell as a ready homepage and could either fail
            // immediately or run visual card detection against a blank canvas.
            // The isolated draft-style flow must not click anything until both
            // independent visual facts are present: the cyan Create Project
            // panel and a real first draft-thumbnail sample.
            int readyTimeoutSeconds = Math.Max(20, Math.Min(30, timeoutSeconds));
            Stopwatch watch = Stopwatch.StartNew();
            bool capturedLoadingShell = false;
            bool lastCreateProjectVisible = false;
            string lastDiagnostic = "";
            using (Bitmap taskCover = HasTaskDraftRootLease
                ? LoadDirectDraftCoverReference()
                : null) {
            while (watch.Elapsed < TimeSpan.FromSeconds(readyTimeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                RefreshWindow();
                FocusForCoordinateAction(token);
                bool createProjectVisible = IsCapCutHomeShell(GetLockedWindowHandle()) &&
                    locator.IsRelativeCyan("CreateProject", mainWindow);
                lastCreateProjectVisible = createProjectVisible;
                Rectangle firstCard = Rectangle.Empty;
                bool firstCardDetected;
                if (HasTaskDraftRootLease) {
                    if (taskCover == null) {
                        lastDiagnostic = "task draft cover is unavailable";
                        firstCardDetected = false;
                    } else {
                        firstCardDetected = createProjectVisible &&
                            TryLocateExactDraftCardByTitle(displayName, taskCover,
                                out firstCard, out lastDiagnostic);
                    }
                } else {
                    firstCardDetected = createProjectVisible &&
                        TryLocateFirstDraftCard(out firstCard,
                            out lastDiagnostic);
                }
                if (DraftCardVision.IsHomeReady(createProjectVisible, firstCardDetected)) {
                    logger.Info(HasTaskDraftRootLease
                        ? "CapCut 专属草稿主页已加载：开始创作入口、精确草稿标题和目标封面均已确认"
                        : "CapCut 草稿主页已加载：检测到新建项目入口和首张草稿缩略图；允许开始草稿定位");
                    return;
                }

                if (!capturedLoadingShell) {
                    CaptureMainWindow("capcut-direct-style-reopen-home-loading.png");
                    capturedLoadingShell = true;
                }
                logger.Info("等待 CapCut 草稿主页加载：新建项目入口=" + createProjectVisible +
                    "，首张缩略图=" + firstCardDetected + "，诊断=" + lastDiagnostic);
                SleepChecked(500, token);
            }

            CaptureMainWindow("capcut-direct-style-reopen-home-timeout.png");
            string timeoutReason = lastCreateProjectVisible
                ? "CAPCUT_TASK_DRAFT_CARD_NOT_LOCATED: CapCut 主页已加载，但未能在当前任务的唯一草稿范围内定位目标卡片；未点击任何草稿。"
                : "CAPCUT_HOME_NOT_READY: CapCut 主页入口尚未就绪；未点击任何草稿。";
            throw new TimeoutException(timeoutReason +
                " 请检查 CapCut 页面渲染状态后重试。诊断：" + lastDiagnostic);
            }
        }

        private bool IsDirectStyledDraftEditorReady(string displayName, CancellationToken token)
        {
            // The home page exposes a large cyan "Create project" panel.  It is
            // visually similar to the editor's cyan Export entry, so never let
            // the generic colour probe prove that we are in the editor while the
            // home-page control is still present.
            bool homePageVisible = IsCapCutHomeShell(GetLockedWindowHandle());
            if (homePageVisible) return false;
            bool sourceIdentityMatches = HasLockedReopenSourceEvidence(directDraftDirectory);
            if (!sourceIdentityMatches) {
                logger.Warn("重开验证期间草稿源视频身份不匹配，拒绝继续: " + Path.GetFileName(directDraftDirectory));
                return false;
            }
            bool draftIdMatches = IsExpectedDraftActuallyActivated();
            if (!DirectDraftReopenPolicy.CanAcceptEditor(directDraftCardWasExplicitlyOpened,
                homePageVisible, draftIdMatches, sourceIdentityMatches, draftIdMatches)) {
                return false;
            }
            CaptureMainWindow("capcut-direct-style-reopen-verified.png");
            logger.Info("草稿重开四项证据通过：draft=" + Path.GetFileName(directDraftDirectory) +
                "，draftId=matched，sourceIdentity=matched，activeDraft=" +
                Path.GetFileName(directDraftActivatedDirectory) +
                "，exactCard=opened，homePage=absent；样式文件将在下一步再次验证；截图=capcut-direct-style-reopen-verified.png");
            return true;
        }

        private bool BindReopenedDirectDraftEditor(int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int waitSeconds = CapCutHomeShellPolicy.EditorInitializationTimeoutSeconds(timeoutSeconds);
            int lastLoggedSecond = -1;
            while (watch.Elapsed < TimeSpan.FromSeconds(waitSeconds)) {
                token.ThrowIfCancellationRequested();
                AutomationElement candidate = FindDirectDraftEditorCandidate();
                if (candidate == null) {
                    SleepChecked(250, token);
                    continue;
                }

                IntPtr candidateHandle = new IntPtr(candidate.Current.NativeWindowHandle);
                bool responsive = !NativeMethods.IsHungAppWindow(candidateHandle);
                bool identityMatches = IsExpectedDraftActuallyActivated() &&
                    HasLockedReopenSourceEvidence(directDraftDirectory);
                bool looksLikeHome = IsCapCutHomeShell(candidateHandle);
                CapCutWindowRole windowRole = CapCutHomeShellPolicy.Classify(candidate.Current.ClassName);
                if (windowRole == CapCutWindowRole.Transition) {
                    int elapsedSecond = (int)watch.Elapsed.TotalSeconds;
                    if (elapsedSecond == 0 || elapsedSecond >= lastLoggedSecond + 3) {
                        lastLoggedSecond = elapsedSecond;
                        LogCapCutWindow("direct-draft transition surface ignored; waiting for real editor",
                            candidateHandle);
                    }
                    SleepChecked(250, token);
                    continue;
                }
                bool editorRoleConfirmed =
                    ClassifyCapCutWindow(candidate) == CapCutUiState.Editor;
                if (!CapCutHomeShellPolicy.CanBindEditor(directDraftHomeHandle, candidateHandle,
                    responsive && editorRoleConfirmed, looksLikeHome, identityMatches,
                    editorRoleConfirmed)) {
                    int elapsedSecond = (int)watch.Elapsed.TotalSeconds;
                    if (elapsedSecond == 0 || elapsedSecond >= lastLoggedSecond + 3) {
                        lastLoggedSecond = elapsedSecond;
                        LogCapCutWindow("direct-draft editor candidate waiting; responsive=" + responsive +
                            "; role=" + windowRole + "; home=" + looksLikeHome +
                            "; identity=" + identityMatches,
                            candidateHandle);
                    }
                    SleepChecked(250, token);
                    continue;
                }

                mainWindow = candidate;
                directDraftEditorHandle = candidateHandle;
                RegisterOwnedSessionProcesses();
                NativeMethods.ActivateWindow(directDraftEditorHandle);
                LogCapCutWindow("direct-draft editor bound", directDraftEditorHandle);
                CaptureMainWindow("capcut-direct-style-reopen-editor-bound.png");
                CloseIsolatedOwnedHomeShellAfterEditorLaunch(directDraftHomeHandle, token);
                return true;
            }

            LogCapCutWindow("direct-draft editor bind timeout home", directDraftHomeHandle);
            return false;
        }

        private AutomationElement FindDirectDraftEditorCandidate()
        {
            var candidates = new List<AutomationElement>();
            try {
                foreach (IntPtr windowHandle in NativeMethods.GetTopLevelWindowHandles()) {
                    try {
                        IntPtr handle = windowHandle;
                        AutomationElement item = locator.ElementFromWindow(handle);
                        if (item == null || item.Current.IsOffscreen) continue;
                        Rect bounds = item.Current.BoundingRectangle;
                        if (bounds.Width < 640 || bounds.Height < 400 || !IsConfiguredCapCutProcess(item)) continue;
                        candidates.Add(item);
                    } catch { }
                }
            } catch { return null; }

            // A short-lived AIShortsWebViewWindow can become foreground while
            // the real MainWindow editor is still loading.  Prefer an explicit
            // editor role and never bind that transition surface.
            AutomationElement explicitEditor = candidates.FirstOrDefault(item => {
                try {
                    return CapCutHomeShellPolicy.Classify(item.Current.ClassName) ==
                        CapCutWindowRole.Editor;
                } catch { return false; }
            });
            if (explicitEditor != null) return explicitEditor;

            var bindableCandidates = candidates.Where(item => {
                try {
                    return CapCutHomeShellPolicy.Classify(item.Current.ClassName) !=
                        CapCutWindowRole.Transition;
                } catch { return false; }
            }).ToList();
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            AutomationElement foregroundCandidate = bindableCandidates.FirstOrDefault(item => {
                try { return new IntPtr(item.Current.NativeWindowHandle) == foreground; }
                catch { return false; }
            });
            if (foregroundCandidate != null) return foregroundCandidate;
            return bindableCandidates.Count == 1 ? bindableCandidates[0] : null;
        }

        private void LogCapCutWindow(string reason, IntPtr handle)
        {
            if (handle == IntPtr.Zero) {
                logger.Info("CapCut 窗口切换: " + reason + "; Handle=0");
                return;
            }
            try {
                AutomationElement element = locator.ElementFromWindow(handle);
                string className = element == null ? "" : (element.Current.ClassName ?? "");
                string title = element == null ? "" : ControlLocator.SafeName(element);
                Rect bounds = element == null ? new Rect() : element.Current.BoundingRectangle;
                logger.Info(String.Format(
                    "CapCut 窗口切换: {0}; Handle={1}; PID={2}; Class={3}; Title={4}; Bounds=({5},{6},{7},{8})",
                    reason, handle, GetWindowProcessId(handle), className, title,
                    (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height));
            } catch (Exception ex) {
                logger.Warn("记录 CapCut 窗口切换失败: " + reason + "; Handle=" + handle + "; " + ex.Message);
            }
        }

        private void EnsureDirectDraftEditorBoundForExport()
        {
            // Do not let the export path fall back to an old CapCut home shell.
            // This applies to every direct-draft task, including formal
            // CompanyStandard handoffs; non-direct/manual sessions retain their
            // existing calibrated export behavior.
            if (!directDraftStyleRequested || directDraftIdentity == null) return;
            IntPtr currentHandle = GetLockedWindowHandle();
            bool isExactEditor = currentHandle != IntPtr.Zero &&
                currentHandle == directDraftEditorHandle &&
                !IsCapCutHomeShell(currentHandle) &&
                DraftContainsExpectedVideo(directDraftDirectory) &&
                IsExpectedDraftActuallyActivated();
            if (!isExactEditor) {
                LogCapCutWindow("export blocked: editor binding invalid", currentHandle);
                throw new InvalidOperationException(
                    "导出前未绑定到本轮已验证草稿编辑器；主页空壳、身份不明窗口或旧句柄均不允许导出");
            }
            LogCapCutWindow("export gate accepted verified editor", currentHandle);
        }

        private bool TryOpenLockedDraftFirstCard(string displayName, int timeoutSeconds, CancellationToken token)
        {
            // CapCut's draft cards are rendered by QML and are absent from UIA.
            // Focus first because QML can expand the More tools strip on focus.
            // Then OCR the exact locked draft name and locate only its thumbnail.
            // There is deliberately no dark-region or fixed-coordinate fallback.
            if (!IsLockedDraftFirstInRootIndex()) return false;
            FocusForCoordinateAction(token);
            if (!IsCapCutHomeShell(GetLockedWindowHandle()) ||
                !locator.IsRelativeCyan("CreateProject", mainWindow)) return false;
            FocusForCoordinateAction(token);
            Rectangle card = Rectangle.Empty;
            string diagnostic = "";
            Stopwatch detectionWait = Stopwatch.StartNew();
            const int requiredStableFrames = DraftCardVision.RequiredExactTitleStableFrames;
            var stability = new DraftCardVision.ExactTitleStabilityTracker();
            // Load the target once. Re-reading it on every stability frame was
            // both noisy and vulnerable to transient draft-file updates.
            using (Bitmap targetCover = LoadDirectDraftCoverReference()) {
                if (targetCover == null) {
                    logger.Warn("目标草稿没有有效时间线封面或源视频回退帧；未执行点击。");
                    return false;
                }
                while (true) {
                    token.ThrowIfCancellationRequested();
                    if (!IsCapCutHomeShell(GetLockedWindowHandle()) ||
                        !locator.IsRelativeCyan("CreateProject", mainWindow)) {
                        logger.Warn("草稿主页在视觉定位期间发生变化，拒绝继续点击。");
                        return false;
                    }
                    Rectangle observed;
                    if (TryLocateExactDraftCardByTitle(displayName, targetCover,
                            out observed, out diagnostic)) {
                        Rectangle accepted;
                        string stabilityDiagnostic;
                        if (stability.Observe(observed, 10, requiredStableFrames,
                            out accepted, out stabilityDiagnostic)) {
                            card = accepted;
                            break;
                        }
                        logger.Info("草稿首卡视觉位置等待稳定: " + stabilityDiagnostic + "; " + diagnostic);
                    }
                    if (detectionWait.Elapsed > TimeSpan.FromSeconds(Math.Min(15, Math.Max(8, timeoutSeconds)))) {
                        logger.Warn("精确草稿标题及其封面在 QML 布局中未连续稳定，已零点击停止: " + diagnostic);
                        return false;
                    }
                    SleepChecked(350, token);
                }
            }
            // The accepted rectangle is the third consecutive OCR observation
            // after focus.  Click that exact live observation immediately;
            // performing a separate pre-click probe used to race the QML
            // More-tools expansion and caused the client to exit safely on
            // every fresh launch.
            int cardX = card.Left + Math.Max(1, card.Width / 2);
            int cardY = card.Top + (card.Height / 2);
            if (!DraftCardVision.IsClickInsideThumbnail(card, cardX, cardY))
                throw new InvalidOperationException(
                    "精确草稿标题上方的点击点不在已验证缩略图内部；已停止且未点击。");
            directDraftOpenAttemptUtc = DateTime.UtcNow;
            directDraftCardWasExplicitlyOpened = true;
            CaptureDraftCardOverlay(card, cardX, cardY, "capcut-direct-style-reopen-card-detected.png");
            NativeMethods.Click(cardX, cardY, DraftCardVision.PhysicalOpenClickCount);
            CaptureDraftCardOverlay(card, cardX, cardY, "capcut-direct-style-reopen-card-clicked.png");
            logger.Info(String.Format(
                "草稿卡片未暴露给 UIA；按“root_meta_info 首卡 + 精确草稿名 OCR + 标题上方封面”仅点击一次打开 {0}: ({1},{2}); {3}",
                displayName, cardX, cardY, diagnostic));
            SleepChecked(500, token);
            return true;
        }

        private bool TryOpenOnlyTaskDraftCard(string displayName, int timeoutSeconds,
            CancellationToken token)
        {
            // The task-scoped root is the identity boundary. It contains one
            // registered draft only. CapCut 9.1 can insert arbitrary AI cards
            // above the draft section, so a page-wide cover search is not a
            // safe locator: an unrelated AI card can have a weakly similar
            // colour structure. Locate the exact visible draft title first,
            // then verify only the thumbnail immediately above that title.
            string identityDiagnostic;
            if (!IsOnlyLockedDraftInTaskRoot(out identityDiagnostic)) {
                logger.Warn("task_draft_identity_failed: " + identityDiagnostic);
                return false;
            }
            FocusForCoordinateAction(token);
            if (!IsCapCutHomeShell(GetLockedWindowHandle()) ||
                !locator.IsRelativeCyan("CreateProject", mainWindow)) return false;

            FocusForCoordinateAction(token);
            Rectangle accepted = Rectangle.Empty;
            string diagnostic = "";
            var stability = new DraftCardVision.ExactTitleStabilityTracker();
            var uniqueCoverFallbackStability =
                new DraftCardVision.ExactTitleStabilityTracker();
            Stopwatch wait = Stopwatch.StartNew();
            using (Bitmap targetCover = LoadDirectDraftCoverReference()) {
            if (targetCover == null) {
                logger.Warn("draft_cover_location_failed: no verified draft cover or source-frame reference");
                return false;
            }
            while (wait.Elapsed < TimeSpan.FromSeconds(Math.Max(6,
                Math.Min(timeoutSeconds, 18)))) {
                token.ThrowIfCancellationRequested();
                FocusForCoordinateAction(token);
                if (!IsCapCutHomeShell(GetLockedWindowHandle()) ||
                    !locator.IsRelativeCyan("CreateProject", mainWindow)) {
                    logger.Warn("专属草稿主页在唯一卡片定位期间发生变化；未执行点击");
                    return false;
                }

                Rectangle observed;
                string exactDiagnostic;
                if (!TryLocateExactDraftCardByTitle(displayName,
                        targetCover, out observed, out exactDiagnostic)) {
                    diagnostic = "draft_exact_title_location_failed: " +
                        exactDiagnostic;
                    Thread.Sleep(250);
                    continue;
                }

                Rectangle stable;
                string stableDiagnostic;
                if (!stability.Observe(observed, 5,
                        DraftCardVision.RequiredExactTitleStableFrames,
                        out stable, out stableDiagnostic)) {
                    diagnostic = stableDiagnostic + "; " +
                        exactDiagnostic;

                    // When Windows OCR omits the short draft title, the
                    // locator deliberately falls back to the unique cover in
                    // the filesystem-verified task root.  That fallback has a
                    // stronger identity boundary than OCR, but it used to be
                    // incorrectly forced through the three-frame OCR gate.
                    // Accept only two consecutive identical fallback frames;
                    // the click is still followed by full draft/editor/source
                    // verification, and no fallback is allowed outside the
                    // single-task lease path.
                    if (exactDiagnostic.IndexOf(
                            "filesystem-unique lower-draft cover fallback",
                            StringComparison.OrdinalIgnoreCase) >= 0) {
                        Rectangle fallbackStable;
                        string fallbackStableDiagnostic;
                        if (uniqueCoverFallbackStability.Observe(observed, 5,
                                DraftCardVision.RequiredUniqueCoverFallbackStableFrames,
                                out fallbackStable,
                                out fallbackStableDiagnostic)) {
                            accepted = fallbackStable;
                            diagnostic = fallbackStableDiagnostic + "; " +
                                exactDiagnostic +
                                "; unique-cover fallback accepted; post-click identity verification remains mandatory";
                            logger.Info("唯一任务缩略图兜底已连续稳定两帧，允许单击并继续执行草稿身份复核: " + diagnostic);
                            CaptureDraftCardOverlay(accepted,
                                accepted.Left + Math.Max(2, accepted.Width / 2),
                                accepted.Top + Math.Max(2, accepted.Height / 2),
                                "capcut-task-scoped-only-draft-card.png");
                            break;
                        }
                    }
                    Thread.Sleep(250);
                    continue;
                }
                accepted = stable;
                diagnostic = stableDiagnostic + "; " +
                    exactDiagnostic;
                CaptureDraftCardOverlay(accepted,
                    accepted.Left + Math.Max(2, accepted.Width / 2),
                    accepted.Top + Math.Max(2, accepted.Height / 2),
                    "capcut-task-scoped-only-draft-card.png");
                break;
            }
            }

            if (accepted.IsEmpty) {
                logger.Warn("draft_card_location_failed: exact task draft title and its verified thumbnail did not stabilize; no click was sent; " + diagnostic);
                return false;
            }

            System.Drawing.Point click = new System.Drawing.Point(
                accepted.Left + Math.Max(2, accepted.Width / 2),
                accepted.Top + Math.Max(2, accepted.Height / 2));
            FocusForCoordinateAction(token);
            directDraftOpenAttemptUtc = DateTime.UtcNow;
            directDraftCardWasExplicitlyOpened = true;
            NativeMethods.Click(click.X, click.Y,
                DraftCardVision.PhysicalOpenClickCount);
            logger.Info("专属草稿目录已由文件系统证明唯一；按精确草稿标题及其上方目标封面点击一次打开 " +
                displayName + ": (" + click.X + "," + click.Y + "); " + diagnostic);
            return true;
        }

        private bool IsOnlyLockedDraftInTaskRoot(out string diagnostic)
        {
            diagnostic = "";
            try {
                if (directDraftIdentity == null ||
                    String.IsNullOrWhiteSpace(directDraftDirectory) ||
                    !HasTaskDraftRootLease) {
                    diagnostic = "missing active task lease or locked draft identity";
                    return false;
                }
                // CapCut can create a zero-duration bootstrap card and then the
                // real task draft during the same CreateProject call.  Prove and
                // quarantine that empty card here, before enforcing the single
                // active-card invariant.  This is deliberately filesystem based;
                // no UI coordinate or title guess participates in the decision.
                if (!recoveringCreatedDraft) draftRootLeaseManager.ReconcileActiveDraft(
                    directDraftIdentity.DraftId,
                    directDraftIdentity.DisplayName,
                    directDraftDirectory,
                    draftExpectedVideoPath);
                string activeRoot = Path.GetFullPath(draftRoot);
                string activeDraft = Path.GetFullPath(directDraftDirectory);
                if (!activeDraft.StartsWith(activeRoot.TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase)) {
                    diagnostic = "draft_checkpoint_not_bound_to_current_lease: " +
                        activeDraft;
                    return false;
                }

                var serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.DeserializeObject(
                    File.ReadAllText(draftIndexPath, Encoding.UTF8))
                    as Dictionary<string, object>;
                object raw;
                System.Collections.IEnumerable values = root != null &&
                    root.TryGetValue("all_draft_store", out raw)
                    ? raw as System.Collections.IEnumerable : null;
                if (values == null) {
                    diagnostic = "task draft index has no all_draft_store";
                    return false;
                }
                var stores = values.Cast<object>()
                    .Select(value => value as Dictionary<string, object>)
                    .Where(value => value != null).ToList();
                if (stores.Count != 1) {
                    diagnostic = "task draft index must contain exactly one draft; current=" +
                        stores.Count;
                    return false;
                }
                string indexedId = Convert.ToString(stores[0].ContainsKey("draft_id")
                    ? stores[0]["draft_id"] : "", CultureInfo.InvariantCulture);
                string indexedPath = Convert.ToString(
                    stores[0].ContainsKey("draft_fold_path")
                        ? stores[0]["draft_fold_path"] : "",
                    CultureInfo.InvariantCulture);
                string mountedDraft = draftRootLeaseManager != null &&
                    draftRootLeaseManager.Current != null
                    ? Path.Combine(
                        draftRootLeaseManager.Current.OriginalDraftRoot,
                        Path.GetFileName(activeDraft))
                    : "";
                bool matched = String.Equals(indexedId, directDraftIdentity.DraftId,
                        StringComparison.OrdinalIgnoreCase) &&
                    (String.Equals(Path.GetFullPath(indexedPath), activeDraft,
                         StringComparison.OrdinalIgnoreCase) ||
                     (!String.IsNullOrWhiteSpace(mountedDraft) &&
                      String.Equals(Path.GetFullPath(indexedPath),
                          Path.GetFullPath(mountedDraft),
                          StringComparison.OrdinalIgnoreCase)));
                diagnostic = matched
                    ? "task lease, single index item and draft identity matched"
                    : "single draft index does not match locked draft_id/path";
                return matched;
            } catch (Exception ex) {
                diagnostic = "task draft identity verification failed: " + ex.Message;
                return false;
            }
        }

        private bool TryLocateFirstDraftCard(out Rectangle absoluteCard, out string diagnostic)
        {
            absoluteCard = Rectangle.Empty;
            diagnostic = "";
            try {
                Rect bounds = mainWindow.Current.BoundingRectangle;
                if (bounds.Width < 100 || bounds.Height < 100) {
                    diagnostic = "CapCut main window has invalid bounds";
                    return false;
                }
                using (var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height))
                using (var graphics = Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
                    Rectangle relative;
                    if (!DraftCardVision.TryFindFirstCard(bitmap, out relative, out diagnostic)) return false;
                    absoluteCard = new Rectangle((int)bounds.Left + relative.Left, (int)bounds.Top + relative.Top,
                        relative.Width, relative.Height);
                    return true;
                }
            } catch (Exception ex) {
                diagnostic = "draft-card screenshot failed: " + ex.Message;
                return false;
            }
        }

        private bool TryLocateExactDraftCardByTitle(string expectedTitle, Bitmap targetCover,
            out Rectangle absoluteCard,
            out string diagnostic)
        {
            absoluteCard = Rectangle.Empty;
            diagnostic = "";
            try {
                Rect bounds = mainWindow.Current.BoundingRectangle;
                if (bounds.Width < 100 || bounds.Height < 100) {
                    diagnostic = "CapCut main window has invalid bounds";
                    return false;
                }
                using (var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height))
                using (var graphics = Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    string screenshot = Path.Combine(AppPaths.LogDirectory,
                        "capcut-direct-style-reopen-ocr-live.png");
                    bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
                    DraftTitleOcrMatch titleMatch;
                    string titleDiagnostic;
                    if (!DraftTitleOcr.TryFindUnique(screenshot, expectedTitle,
                            out titleMatch, out titleDiagnostic)) {
                        // Exact OCR remains the preferred anchor.  On the
                        // current 9.1 homepage a single task draft can be shown
                        // with an 11-pixel title, which Windows OCR may omit.
                        // A fallback is permitted only inside a filesystem-
                        // verified task-scoped root containing one draft, and
                        // only inside the lower Drafts area.  The opened editor
                        // still has to pass draft_id/name/source/style gates.
                        string identityDiagnostic = "";
                        DraftCardVision.Detection fallback;
                        double fallbackScore;
                        string fallbackDiagnostic = "";
                        if (!HasTaskDraftRootLease ||
                                !IsOnlyLockedDraftInTaskRoot(out identityDiagnostic) ||
                                !DraftCardVision.TryFindUniqueTaskCardByCoverInLowerDraftArea(
                                    bitmap, targetCover, out fallback,
                                    out fallbackScore, out fallbackDiagnostic)) {
                            diagnostic = titleDiagnostic + "; lower-draft fallback=" +
                                (String.IsNullOrWhiteSpace(fallbackDiagnostic)
                                    ? identityDiagnostic : fallbackDiagnostic);
                            return false;
                        }
                        Rectangle lowerCard = fallback.Thumbnail;
                        absoluteCard = new Rectangle((int)bounds.Left + lowerCard.Left,
                            (int)bounds.Top + lowerCard.Top,
                            lowerCard.Width, lowerCard.Height);
                        diagnostic = titleDiagnostic + "; filesystem-unique lower-draft cover fallback; " +
                            fallbackDiagnostic + "; post-click identity verification remains mandatory";
                        return true;
                    }
                    if (titleMatch.Bounds.Top < (int)(bitmap.Height * 0.35)) {
                        diagnostic = titleDiagnostic + "; exact title is outside the draft grid";
                        return false;
                    }
                    Rectangle relative;
                    double coverScore;
                    string thumbnailDiagnostic;
                    if (!DraftTitleOcr.TryResolveThumbnailAboveTitle(bitmap,
                            titleMatch.Bounds, targetCover, out relative,
                            out coverScore, out thumbnailDiagnostic)) {
                        diagnostic = titleDiagnostic + "; " + thumbnailDiagnostic;
                        return false;
                    }
                    absoluteCard = new Rectangle((int)bounds.Left + relative.Left,
                        (int)bounds.Top + relative.Top, relative.Width, relative.Height);
                    diagnostic = titleDiagnostic + "; " + thumbnailDiagnostic +
                        "; no dark-region or fixed-coordinate fallback";
                    return true;
                }
            } catch (Exception ex) {
                diagnostic = "exact draft-title OCR location failed: " + ex.Message;
                return false;
            }
        }

        private Bitmap LoadDirectDraftCoverReference()
        {
            try {
                if (!String.IsNullOrWhiteSpace(directDraftDirectory) && Directory.Exists(directDraftDirectory)) {
                    string cover = Directory.GetFiles(directDraftDirectory, "draft_cover.jpg",
                            SearchOption.AllDirectories)
                        .Where(path => {
                            try { return new FileInfo(path).Length > 0; }
                            catch { return false; }
                        })
                        .OrderByDescending(path => new FileInfo(path).Length)
                        .FirstOrDefault();
                    if (!String.IsNullOrWhiteSpace(cover)) {
                        using (var source = new Bitmap(cover)) {
                            logger.Info("已读取目标草稿有效时间线封面用于首卡唯一匹配: " +
                                source.Width + "x" + source.Height + "，" + new FileInfo(cover).Length + " bytes");
                            return new Bitmap(source);
                        }
                    }
                }
            } catch (Exception ex) {
                logger.Warn("读取目标草稿时间线封面失败，将尝试源视频第0帧: " + ex.Message);
            }
            return ExtractSourceFrameReference();
        }

        private Bitmap ExtractSourceFrameReference()
        {
            string temporary = null;
            try {
                if (String.IsNullOrWhiteSpace(draftExpectedVideoPath) || !File.Exists(draftExpectedVideoPath))
                    return null;
                string ffmpeg = new[] {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
                    @"C:\ffmpeg\bin\ffmpeg.exe"
                }.FirstOrDefault(File.Exists);
                if (String.IsNullOrWhiteSpace(ffmpeg)) return null;
                Directory.CreateDirectory(AppPaths.LogDirectory);
                temporary = Path.Combine(AppPaths.LogDirectory,
                    "draft-cover-source-frame-" + Guid.NewGuid().ToString("N") + ".jpg");
                var start = new ProcessStartInfo {
                    FileName = ffmpeg,
                    Arguments = "-hide_banner -loglevel error -y -i \"" + draftExpectedVideoPath +
                        "\" -frames:v 1 \"" + temporary + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process extractor = Process.Start(start)) {
                    if (extractor == null || !extractor.WaitForExit(15000) || extractor.ExitCode != 0 ||
                        !File.Exists(temporary) || new FileInfo(temporary).Length == 0) return null;
                }
                using (var source = new Bitmap(temporary)) return new Bitmap(source);
            } catch (Exception ex) {
                logger.Warn("从源视频提取第0帧作为草稿封面失败: " + ex.Message);
                return null;
            } finally {
                if (!String.IsNullOrWhiteSpace(temporary)) {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private void CaptureDraftCardOverlay(Rectangle card, int clickX, int clickY, string fileName)
        {
            try {
                Rect bounds = mainWindow.Current.BoundingRectangle;
                using (var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height))
                using (var graphics = Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
                    Rectangle relative = new Rectangle(card.Left - (int)bounds.Left, card.Top - (int)bounds.Top, card.Width, card.Height);
                    using (var pen = new Pen(Color.Red, 4)) graphics.DrawRectangle(pen, relative);
                    using (var brush = new SolidBrush(Color.Yellow)) graphics.FillEllipse(brush, clickX - (int)bounds.Left - 7, clickY - (int)bounds.Top - 7, 14, 14);
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    bitmap.Save(Path.Combine(AppPaths.LogDirectory, fileName), System.Drawing.Imaging.ImageFormat.Png);
                }
            } catch (Exception ex) {
                logger.Warn("保存草稿卡视觉定位截图失败: " + ex.Message);
            }
        }

        private bool IsLockedDraftFirstInRootIndex()
        {
            try {
                if (directDraftIdentity == null || !File.Exists(draftIndexPath)) return false;
                var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 };
                var root = serializer.DeserializeObject(File.ReadAllText(draftIndexPath, Encoding.UTF8)) as Dictionary<string, object>;
                object rawStores;
                if (root == null || !root.TryGetValue("all_draft_store", out rawStores)) return false;
                object[] stores = rawStores as object[];
                var first = stores == null ? null : stores.FirstOrDefault() as Dictionary<string, object>;
                object rawPath;
                object rawId;
                bool matches = first != null &&
                    first.TryGetValue("draft_fold_path", out rawPath) &&
                    first.TryGetValue("draft_id", out rawId) &&
                    String.Equals(NormalizePath(Convert.ToString(rawPath)), NormalizePath(directDraftDirectory),
                        StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(Convert.ToString(rawId), directDraftIdentity.DraftId, StringComparison.OrdinalIgnoreCase);
                if (!matches) {
                    logger.Warn("唯一实验草稿不是 root_meta_info 首页首卡，拒绝猜测其他卡片位置: expected=" +
                        Path.GetFileName(directDraftDirectory));
                }
                return matches;
            } catch (Exception ex) {
                logger.Warn("无法确认唯一实验草稿的创建顺序，拒绝点击首页卡片: " + ex.Message);
                return false;
            }
        }

        private void CaptureDirectDraftActivationBaseline()
        {
            if (String.IsNullOrWhiteSpace(draftRoot) || !Directory.Exists(draftRoot))
                throw new InvalidOperationException("无法读取 CapCut 草稿目录，不能建立重开身份基线");
            directDraftActivationBaseline = Directory.GetDirectories(draftRoot)
                .ToDictionary(Path.GetFullPath, ReadDraftActivationStamp, StringComparer.OrdinalIgnoreCase);
            directDraftActivatedDirectory = null;
            logger.Info("已记录草稿重开前激活基线: " + directDraftActivationBaseline.Count + " 条");
        }

        private bool IsExpectedDraftActuallyActivated()
        {
            try {
                if (directDraftIdentity == null || directDraftActivationBaseline == null ||
                    directDraftOpenAttemptUtc == DateTime.MinValue) return false;
                var activated = Directory.GetDirectories(draftRoot)
                    .Select(Path.GetFullPath)
                    .Where(path => WasDraftActivatedAfterAttempt(path, directDraftOpenAttemptUtc))
                    .ToArray();
                if (activated.Length != 1) {
                    logger.Warn("本轮草稿重开未能识别唯一激活目录，数量=" + activated.Length + "；已阻止导出");
                    return false;
                }
                directDraftActivatedDirectory = activated[0];
                if (!String.Equals(NormalizePath(directDraftActivatedDirectory), NormalizePath(directDraftDirectory),
                    StringComparison.OrdinalIgnoreCase)) {
                    logger.Error("draft_reopen_identity_mismatch：CapCut 实际打开=" +
                        Path.GetFileName(directDraftActivatedDirectory) + "，期望=" +
                        Path.GetFileName(directDraftDirectory) + "；已阻止导出");
                    return false;
                }
                DirectDraftIdentity current = ReadDirectDraftIdentity(directDraftActivatedDirectory);
                if (current == null || !String.Equals(current.DraftId, directDraftIdentity.DraftId,
                    StringComparison.OrdinalIgnoreCase) || !HasLockedReopenSourceEvidence(directDraftActivatedDirectory)) {
                    logger.Error("draft_reopen_identity_mismatch：实际激活草稿的 draft_id 或源视频不匹配；已阻止导出");
                    return false;
                }
                return true;
            } catch (Exception ex) {
                logger.Warn("读取本轮草稿实际激活身份失败，拒绝继续: " + ex.Message);
                return false;
            }
        }

        private bool WasDraftActivatedAfterAttempt(string directory, DateTime attemptUtc)
        {
            DraftActivationStamp before;
            if (!directDraftActivationBaseline.TryGetValue(Path.GetFullPath(directory), out before)) return false;
            DraftActivationStamp after = ReadDraftActivationStamp(directory);
            return DraftActivationEvidencePolicy.ChangedAfterAttempt(
                attemptUtc, before.Values, after.Values);
        }

        private static DraftActivationStamp ReadDraftActivationStamp(string directory)
        {
            return new DraftActivationStamp {
                DirectoryWriteUtc = DirectoryLastWriteUtc(directory),
                MetaWriteUtc = LastWriteUtc(Path.Combine(directory, "draft_meta_info.json")),
                ContentWriteUtc = LastWriteUtc(Path.Combine(directory, "draft_content.json")),
                LockWriteUtc = LastWriteUtc(Path.Combine(directory, ".locked")),
                BizConfigWriteUtc = LastWriteUtc(Path.Combine(directory, "draft_biz_config.json")),
                SettingsWriteUtc = LastWriteUtc(Path.Combine(directory, "draft_settings")),
                TimelineLayoutWriteUtc = LastWriteUtc(Path.Combine(directory, "timeline_layout.json")),
                TimelineProjectWriteUtc = LastWriteUtc(Path.Combine(directory, "Timelines", "project.json"))
            };
        }

        private static DateTime DirectoryLastWriteUtc(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static DateTime LastWriteUtc(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private void WaitForDraftFilesToSettle(string draftDirectory, int timeoutSeconds, CancellationToken token)
        {
            string rootContent = Path.Combine(draftDirectory, "draft_content.json");
            string timelineRoot = Path.Combine(draftDirectory, "Timelines");
            if (!File.Exists(rootContent) || !Directory.Exists(timelineRoot))
                throw new InvalidOperationException("草稿尚未完整写入，缺少根内容或时间线目录");
            long previousLength = -1;
            DateTime previousWrite = DateTime.MinValue;
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(rootContent);
                long length = info.Length;
                DateTime write = info.LastWriteTimeUtc;
                if (length > 0 && length == previousLength && write == previousWrite) return;
                previousLength = length;
                previousWrite = write;
                SleepChecked(900, token);
            }
            throw new TimeoutException("草稿文件仍在变化，未写入字幕样式");
        }

        private bool IsCompanyFontInstalled()
        {
            try {
                var fonts = new System.Drawing.Text.InstalledFontCollection();
                return fonts.Families.Any(font => String.Equals(
                    font.Name, config.CompanySubtitleFont, StringComparison.OrdinalIgnoreCase));
            } catch {
                return false;
            }
        }

        private void ClickRelative(LocatorRule rule)
        {
            Rect bounds = mainWindow.Current.BoundingRectangle;
            NativeMethods.Click((int)(bounds.Left + bounds.Width * rule.RelativeX),
                (int)(bounds.Top + bounds.Height * rule.RelativeY), 1);
        }

        private bool IsCyanNear(LocatorRule rule)
        {
            Rect bounds = mainWindow.Current.BoundingRectangle;
            int centerX = (int)(bounds.Left + bounds.Width * rule.RelativeX);
            int centerY = (int)(bounds.Top + bounds.Height * rule.RelativeY);
            int matches = 0;
            using (var bitmap = new System.Drawing.Bitmap(121, 65))
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen(centerX - 60, centerY - 32, 0, 0, bitmap.Size);
                for (int y = 0; y < bitmap.Height; y += 2) {
                    for (int x = 0; x < bitmap.Width; x += 2) {
                        System.Drawing.Color color = bitmap.GetPixel(x, y);
                        if (color.G >= 125 && color.B >= 125 && color.R <= 110) matches++;
                    }
                }
            }
            return matches >= 8;
        }

        private void SetCaptionSize(LocatorRule size, int value, CancellationToken token)
        {
            FocusForCoordinateAction(token);
            ClickRelative(size);
            NativeMethods.SendControlChord(NativeMethods.VK_A);
            ControlLocator.PasteTextToForeground(value.ToString(CultureInfo.InvariantCulture));
            logger.Info("已填写字幕字号: " + value.ToString(CultureInfo.InvariantCulture));
        }

        private void SelectCaptionFontFromPopup(LocatorRule font, CancellationToken token)
        {
            FocusForCoordinateAction(token);
            ClickRelative(font);
            SleepChecked(700, token);
            CaptureMainWindow("capcut-font-menu-open.png");
            // CapCut 9 does not expose the font popup items to UI Automation on this PC.
            // Selecting a fixed item from "recently used" is unsafe because its order changes.
            // Use the popup's own search box and then click the sole filtered result instead.
            Rect bounds = mainWindow.Current.BoundingRectangle;
            int searchX = (int)(bounds.Left + bounds.Width * 0.850);
            int searchY = (int)(bounds.Top + bounds.Height * 0.231);
            int allFontsX = (int)(bounds.Left + bounds.Width * 0.725);
            int allFontsY = (int)(bounds.Top + bounds.Height * 0.264);
            // The "可商用" filter can hide a locally installed font.  This is an
            // isolated lab-only operation: make the full font list visible before
            // searching, rather than falling through to the current system font.
            NativeMethods.Click(allFontsX, allFontsY, 1);
            SleepChecked(350, token);
            NativeMethods.Click(searchX, searchY, 1);
            NativeMethods.SendControlChord(NativeMethods.VK_A);
            ControlLocator.PasteTextToForeground(config.CompanySubtitleFont);
            NativeMethods.PressEnter();
            SleepChecked(1200, token);
            CaptureMainWindow("capcut-font-search-result.png");

            // The first filtered font row is stable after an exact search.  Do not use the
            // unfiltered recent-font coordinate: that was selecting "System"/Rubik.
            int resultX = (int)(bounds.Left + bounds.Width * 0.754);
            int resultY = (int)(bounds.Top + bounds.Height * 0.321);
            NativeMethods.Click(resultX, resultY, 1);
            SleepChecked(850, token);
            CaptureMainWindow("capcut-after-font-selected.png");
            logger.Info(String.Format("已通过字体搜索框强制选择：{0}；搜索点=({1},{2})，结果点=({3},{4})",
                config.CompanySubtitleFont, searchX, searchY, resultX, resultY));
        }

        private void VerifyCompanyCaptionStyle(LocatorRule font, LocatorRule size, bool styleLab, CancellationToken token)
        {
            string actualFont = styleLab ? ReadCaptionField(font, "CaptionFontField", token) : "";
            string actualSize = ReadCaptionField(size, "CaptionFontSizeField", token);
            bool sizeReadable = !String.IsNullOrWhiteSpace(actualSize);
            bool sizeOk = !sizeReadable || String.Equals(actualSize.Trim(), config.CompanySubtitleFontSize.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            bool fontOk = !styleLab || String.Equals(actualFont.Trim(), config.CompanySubtitleFont, StringComparison.OrdinalIgnoreCase);
            logger.Info(String.Format("字幕样式实际回读：字体={0}，字号={1}，预设=已选中",
                styleLab ? actualFont : "保留当前设置", actualSize));
            if (!fontOk) {
                throw new InvalidOperationException(String.Format(
                    "隔离字幕样式实验未选中目标字体：实际“{0}”，要求“{1}”。已在导出前停止并保留草稿。",
                    actualFont, config.CompanySubtitleFont));
            }
            if (!sizeOk) {
                throw new InvalidOperationException(String.Format(
                    "字幕样式验证失败：实际字号“{0}”；要求字号“{1}”。已阻止导出。",
                    actualSize, config.CompanySubtitleFontSize));
            }
            if (!sizeReadable) {
                logger.Info("CapCut 字号控件不支持文字回读；已使用确定性字号写入及预设青色选中状态作为验证依据");
            }
        }

        private string ReadCaptionField(LocatorRule rule, string action, CancellationToken token)
        {
            FocusForCoordinateAction(token);
            AutomationElement element = locator.FindByRule(rule, mainWindow);
            if (element != null) {
                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) {
                    string value = ((ValuePattern)pattern).Current.Value ?? "";
                    if (!String.IsNullOrWhiteSpace(value)) return value;
                }
                string name = ControlLocator.SafeName(element);
                if (!String.IsNullOrWhiteSpace(name) &&
                    !String.Equals(name, "字体", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(name, "Font", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(name, "字号", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(name, "Size", StringComparison.OrdinalIgnoreCase)) return name;
            }
            if (!locator.IsWideInputVisible(action, mainWindow)) return "";
            ClickRelative(rule);
            NativeMethods.SendControlChord(NativeMethods.VK_A);
            NativeMethods.SendControlChord(NativeMethods.VK_C);
            SleepChecked(120, token);
            string copied = ReadClipboardText();
            NativeMethods.PressEscape();
            return copied;
        }

        private void EnsureCaptionEditorVisible(CancellationToken token, string context)
        {
            FocusForCoordinateAction(token);
            if (locator.IsWideInputVisible("CaptionFontField", mainWindow)) return;
            if (!locator.ClickFirstCaptionClip("SubtitleTrackProbe", "FirstCaptionAtZeroProbe", mainWindow)) {
                throw new InvalidOperationException(context + "无法重新选择第一条字幕");
            }
            SleepChecked(500, token);
            if (!locator.IsWideInputVisible("CaptionFontField", mainWindow)) {
                throw new InvalidOperationException(context + "字幕编辑面板不可见，已停止以避免误点视频属性");
            }
        }

        public void TrimFirstCaptionIfNeeded(int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            Rect bounds = mainWindow.Current.BoundingRectangle;
            LogFirstCaptionEnvironment(bounds);

            LocatorRule probe = RequireFirstCaptionCalibration("FirstCaptionAtZeroProbe");
            LocatorRule zero = RequireFirstCaptionCalibration("FirstCaptionLeftEdge");
            LocatorRule oneFrame = RequireFirstCaptionCalibration("FirstCaptionOneFrameRight");
            System.Drawing.Rectangle beforeClip;
            if (!locator.TryGetFirstCaptionBounds(mainWindow, out beforeClip)) {
                logger.Info("No real caption clip is present; no first-frame trim is needed");
                return;
            }

            int beforeLeft = beforeClip.Left;
            int expectedZeroX = (int)(bounds.Left + bounds.Width * zero.RelativeX);
            int expectedOneFrameX = (int)(bounds.Left + bounds.Width * oneFrame.RelativeX);
            int expectedFramePixels = FirstCaptionFrameValidation.ExpectedFramePixels(expectedZeroX, expectedOneFrameX);
            int startDelta = beforeLeft - expectedZeroX;
            int probeX = (int)(bounds.Left + bounds.Width * probe.RelativeX);
            bool probeInsideFirstCaption = probeX >= beforeClip.Left && probeX < beforeClip.Right;
            logger.Info(String.Format(
                "首帧字幕测量: beforeLeft={0}, beforeRight={1}, expectedZeroX={2}, expectedOneFrameX={3}, expectedFramePixels={4}, startDelta={5}, probeX={6}, probeInsideFirstCaption={7}",
                beforeLeft, beforeClip.Right, expectedZeroX, expectedOneFrameX, expectedFramePixels, startDelta, probeX, probeInsideFirstCaption));

            // A caption which starts late is already clear of the cover frame.
            // It must be left untouched, not treated as a failed trim or retried.
            if (!FirstCaptionFrameValidation.StartsAtZero(beforeLeft, expectedZeroX, expectedFramePixels)) {
                logger.Info(String.Format(
                    "首条字幕不在第0帧开始，跳过首帧裁剪: beforeLeft={0}, expectedZeroX={1}, startDelta={2}, afterLeft={3}, movedPixels=0",
                    beforeLeft, expectedZeroX, startDelta, beforeLeft));
                return;
            }
            if (!probeInsideFirstCaption) {
                throw new InvalidOperationException(String.Format(
                    "首帧字幕探针不在第一条字幕内：probeX={0}, clip=[{1},{2})；已阻止导出",
                    probeX, beforeClip.Left, beforeClip.Right));
            }

            CaptureMainWindow("capcut-before-first-caption-trim.png");
            if (!locator.ClickFirstCaptionClip("SubtitleTrackProbe", "FirstCaptionAtZeroProbe", mainWindow)) {
                throw new InvalidOperationException("Could not select the first caption for the one-frame trim");
            }
            // Use CapCut's own frame navigation, never a guessed screen-pixel drag.
            NativeMethods.PressHome();
            SleepChecked(200, token);
            NativeMethods.PressRight();
            SleepChecked(200, token);
            CaptureMainWindow("capcut-before-first-caption-trim-q-ready.png");
            if (diagnosticFirstCaptionQNoOp) {
                logger.Warn("诊断注入已启用：本次故意不发送 Q，用于验证左边缘未移动时必须阻止导出");
            } else {
                NativeMethods.PressQ();
            }
            SleepChecked(650, token);
            System.Drawing.Rectangle afterClip;
            if (!locator.TryGetFirstCaptionBounds(mainWindow, out afterClip)) {
                CaptureMainWindow("capcut-first-caption-trim-not-confirmed.png");
                throw new InvalidOperationException("Q 后未能识别第一条字幕左边缘；已阻止导出");
            }
            int afterLeft = afterClip.Left;
            int movedPixels = FirstCaptionFrameValidation.MovedPixels(beforeLeft, afterLeft);
            logger.Info(String.Format(
                "首帧字幕裁后测量: beforeLeft={0}, beforeRight={1}, expectedZeroX={2}, expectedOneFrameX={3}, expectedFramePixels={4}, startDelta={5}, afterLeft={6}, afterRight={7}, movedPixels={8}",
                beforeLeft, beforeClip.Right, expectedZeroX, expectedOneFrameX, expectedFramePixels, startDelta,
                afterLeft, afterClip.Right, movedPixels));
            try {
                FirstCaptionFrameValidation.RequireLeftEdgeOnly(
                    beforeClip.Left, beforeClip.Right, afterClip.Left, afterClip.Right, expectedFramePixels);
            } catch {
                CaptureMainWindow("capcut-first-caption-trim-not-confirmed.png");
                throw;
            }
            logger.Info(String.Format(
                "已用 Home、右方向键、Q 将第0帧首条字幕左边缘精确缩短一帧；右边缘保持 {0}px 不变",
                afterClip.Right));
            CaptureMainWindow("capcut-after-first-caption-trim.png");
        }

        private LocatorRule RequireFirstCaptionCalibration(string name)
        {
            LocatorRule rule = config.GetRule(name);
            if (!rule.HasCoordinate) {
                throw new InvalidOperationException("缺少首帧字幕本机校准坐标 " + name + "；已阻止导出");
            }
            return rule;
        }

        private void LogFirstCaptionEnvironment(Rect bounds)
        {
            int screenWidth = NativeMethods.GetSystemMetrics(0);
            int screenHeight = NativeMethods.GetSystemMetrics(1);
            uint dpi = NativeMethods.GetDpiForSystem();
            double scale = dpi * 100.0 / 96.0;
            logger.Info(String.Format(
                "首帧字幕环境: CapCutBounds=({0},{1},{2},{3}), WindowsResolution={4}x{5}, WindowsDpi={6}, WindowsScale={7:0.##}%",
                (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height,
                screenWidth, screenHeight, dpi, scale));
        }

        private bool exportEntitlementReturnAttempted;

        public void ExportVideo(string outputPath, int timeoutSeconds, CancellationToken token)
        {
            exportEntitlementReturnAttempted = false;
            string stagingRoot = String.IsNullOrWhiteSpace(config.OutputDirectory)
                ? Path.GetDirectoryName(outputPath)
                : config.OutputDirectory;
            string stagingOutputPath = Path.Combine(stagingRoot, Path.GetFileName(outputPath));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            Directory.CreateDirectory(stagingRoot);
            bool outputExistedBeforeExport = File.Exists(stagingOutputPath);
            long outputLengthBeforeExport = -1;
            DateTime outputWriteTimeBeforeExportUtc = DateTime.MinValue;
            if (outputExistedBeforeExport) {
                FileInfo existingOutput = new FileInfo(stagingOutputPath);
                outputLengthBeforeExport = existingOutput.Length;
                outputWriteTimeBeforeExportUtc = existingOutput.LastWriteTimeUtc;
                logger.Info(String.Format(
                    "导出目标已存在，将要求本轮导出实际改写文件: Size={0}, LastWriteUtc={1:o}",
                    outputLengthBeforeExport, outputWriteTimeBeforeExportUtc));
            }
            RefreshWindow();
            EnsureDirectDraftEditorBoundForExport();
            string[] exportCandidates = BuildExportCandidates(outputPath, stagingOutputPath);
            string occupiedCandidate = exportCandidates.FirstOrDefault(File.Exists);
            if (!String.IsNullOrWhiteSpace(occupiedCandidate)) {
                throw new IOException("本轮唯一导出文件名已被占用，已阻止打开覆盖弹窗: " + occupiedCandidate);
            }
            FocusForCoordinateAction(token);
            CaptureMainWindow("capcut-direct-style-before-export.png");
            logger.Info("导出阶段: 编辑器已绑定 → 等待导出入口");
            DateTime exportEntryStartedUtc = DateTime.UtcNow;
            NativeMethods.SendControlChord(NativeMethods.VK_E);
            logger.Info("已通过 CapCut 快捷键 Ctrl+E 打开导出窗口");
            SleepChecked(1400, token);
            IntPtr mainHandle = GetLockedWindowHandle();
            AutomationElement exportWindow = WaitForExportWindow(mainHandle, 15, token);
            if (exportWindow == null) {
                if (HasFreshExportActivity(exportCandidates, exportEntryStartedUtc)) {
                    throw new InvalidOperationException("尚未识别导出窗口但已出现本轮文件活动；为避免重复操作已停止");
                }
                logger.Warn("Ctrl+E 在15秒内未打开导出窗口；仅补点一次已验证编辑器右上角导出入口");
                FocusForCoordinateAction(token);
                locator.Click("Export", mainWindow, config.Timeouts.ControlSeconds, token, true);
                SleepChecked(1400, token);
                exportWindow = WaitForExportWindow(mainHandle, 15, token);
            }
            if (exportWindow == null || exportWindow.Current.NativeWindowHandle == mainWindow.Current.NativeWindowHandle) {
                throw new InvalidOperationException("未识别到独立的剪映导出弹窗，已阻止错误坐标操作");
            }
            AutomationElement exportRoot = exportWindow;
            IntPtr exportHandle = new IntPtr(exportRoot.Current.NativeWindowHandle);
            activeExportHandle = exportHandle;
            logger.Info("导出阶段: 导出窗口已打开");
            LogCapCutWindow("export dialog opened", activeExportHandle);
            Rect exportBounds = exportRoot.Current.BoundingRectangle;
            logger.Info(String.Format("已接管剪映导出根窗口: Handle={0}, Class={1}, Bounds={2}x{3}",
                exportHandle, exportRoot.Current.ClassName, (int)exportBounds.Width, (int)exportBounds.Height));
            NativeMethods.FocusWindow(exportHandle);
            NativeMethods.CenterWindowOnCurrentScreen(exportHandle);
            SleepChecked(200, token);
            CaptureMainWindow("capcut-after-export-open.png");
            SetExportFileName(exportRoot, Path.GetFileNameWithoutExtension(outputPath), token);
            ConfigureExportResolution(exportRoot, token);
            // The native folder picker on this CapCut build does not retain a
            // pasted destination reliably.  Export to the already verified
            // local staging directory, wait for a stable file, then move the
            // uniquely named result into the workbench's required directory.
            logger.Info("剪映导出暂存目录: " + stagingRoot);
            CaptureMainWindow("capcut-after-export-configured.png");
            Rect confirmBounds = exportRoot.Current.BoundingRectangle;
            LocatorRule confirmRule = config.GetRule("ExportConfirm");
            if (!confirmRule.HasCoordinate) {
                throw new InvalidOperationException("缺少导出确认按钮坐标，已阻止等待不存在的成片");
            }
            // Build the complete evidence set before clicking export.  On this
            // CapCut build the completion page also has a cyan button at the
            // calibrated export-confirm coordinate (\"发布\"), so the file is
            // the source of truth, not a post-click pixel colour.
            DateTime exportStartedUtc = DateTime.UtcNow;
            NativeMethods.Click((int)(confirmBounds.Left + confirmBounds.Width * confirmRule.RelativeX),
                (int)(confirmBounds.Top + confirmBounds.Height * confirmRule.RelativeY), 1);
            logger.Info("已点击当前剪映导出窗口的实际“导出”按钮");
            logger.Info("导出阶段: 已点击正式导出 → 等待文件");
            ObserveExportStart(exportRoot, exportHandle, exportCandidates, exportStartedUtc, token);
            logger.Info("已开始等待本轮唯一导出文件；蓝色按钮仅作为诊断，不再单独判定导出失败");
            string actualOutputPath = WaitForStableOutput(
                exportCandidates,
                timeoutSeconds,
                exportStartedUtc,
                token);
            if (!String.Equals(Path.GetFullPath(actualOutputPath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase)) {
                if (File.Exists(outputPath)) {
                    throw new IOException("正式输出路径已存在，已阻止覆盖: " + outputPath);
                }
                // CapCut can keep the completed MP4 open briefly even after its
                // size has stabilized.  Close its completion UI and wait for the
                // lock to release; never retry the whole export just because a
                // finished file cannot be moved in the first millisecond.
                bool completionClosed = CloseExportCompletionWindow(
                    Math.Min(8, timeoutSeconds), token, true);
                if (!completionClosed) {
                    logger.Warn("导出文件已稳定，但完成页未完全关闭；将只尝试安全复制现有成片，绝不重复导出");
                }
                MoveOutputWhenUnlocked(actualOutputPath, outputPath,
                    Math.Min(30, Math.Max(5, timeoutSeconds)), token);
                logger.Info("已从剪映暂存目录移动成片到正式任务目录: " + outputPath);
            }
            logger.Info("导出文件已稳定: " + outputPath);
            CaptureMainWindow("capcut-direct-style-export-finished.png");
        }

        private string[] BuildExportCandidates(string outputPath, string stagingOutputPath)
        {
            var candidates = new List<string> { stagingOutputPath };
            if (!String.IsNullOrWhiteSpace(config.CapCutExportStagingDirectory)) {
                candidates.Add(Path.Combine(config.CapCutExportStagingDirectory, Path.GetFileName(outputPath)));
                logger.Info("已加入本机剪映固定导出目录候选: " + config.CapCutExportStagingDirectory);
            }
            string localStagingFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "capcut-export-staging.path");
            try {
                if (File.Exists(localStagingFile)) {
                    foreach (string raw in File.ReadAllLines(localStagingFile, Encoding.UTF8)) {
                        string directory = Environment.ExpandEnvironmentVariables((raw ?? "").Trim());
                        if (directory.Length == 0 || !Directory.Exists(directory)) continue;
                        candidates.Add(Path.Combine(directory, Path.GetFileName(outputPath)));
                        logger.Info("已从本机私有导出目录文件加入候选: " + directory);
                    }
                }
            } catch (Exception ex) {
                logger.Warn("读取本机私有导出目录失败，将只使用正式配置目录: " + ex.Message);
            }
            if (RuntimeModes.IsolatedLabRuntime) {
                const string isolatedCapCutExportBucket =
                    @"E:\Backup\Documents\New project 3\data\home-capcut-test-v020\output";
                if (Directory.Exists(isolatedCapCutExportBucket)) {
                    candidates.Add(Path.Combine(isolatedCapCutExportBucket, Path.GetFileName(outputPath)));
                    logger.Info("隔离内测已加入本机CapCut默认导出桶候选: " + isolatedCapCutExportBucket);
                }
            }
            return candidates.Where(path => !String.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void ObserveExportStart(
            AutomationElement exportRoot,
            IntPtr exportHandle,
            IEnumerable<string> candidates,
            DateTime exportStartedUtc,
            CancellationToken token)
        {
            bool enterFallbackSent = false;
            Stopwatch watch = Stopwatch.StartNew();
            // 10-10..10-16: without Enter the file appeared 0-1.75 s after
            // the click; every Enter sent at 1.8 s was followed by the file
            // within 1.5 s, i.e. the export had already started.  An Enter
            // that reaches a running export can raise "文件已存在，是否覆盖？"
            // (10-16 09:31), which then holds the completion page open.
            while (watch.Elapsed < TimeSpan.FromSeconds(7)) {
                token.ThrowIfCancellationRequested();
                if (HasFreshExportActivity(candidates, exportStartedUtc)) {
                    logger.Info("检测到本轮导出文件已出现或正在增长；禁止再次点击导出/Enter");
                    return;
                }
                if (IsExportCompletionSurface(exportRoot)) {
                    logger.Info("检测到CapCut导出成功页；等待文件稳定，不再操作成功页按钮");
                    return;
                }
                if (!enterFallbackSent && watch.Elapsed >= TimeSpan.FromMilliseconds(5000) &&
                    IsExportWindowCandidate(locator.ElementFromWindow(exportHandle), GetLockedWindowHandle())) {
                    // This is only a focus fallback for the *same settings
                    // dialog*.  It is never based on cyan pixels, which are
                    // shared by the later publish page.
                    logger.Warn("导出设置窗口仍在且尚无文件活动；发送一次Enter回退");
                    NativeMethods.FocusWindow(exportHandle);
                    NativeMethods.PressEnter();
                    enterFallbackSent = true;
                }
                Thread.Sleep(250);
            }
            logger.Info("导出启动观察结束；将继续按文件稳定性等待，不依据页面颜色失败");
        }

        private static bool HasFreshExportActivity(IEnumerable<string> candidates, DateTime exportStartedUtc)
        {
            foreach (string path in candidates ?? new string[0]) {
                try {
                    if (!File.Exists(path)) continue;
                    FileInfo file = new FileInfo(path);
                    if (file.Length > 0 && file.LastWriteTimeUtc >= exportStartedUtc.AddSeconds(-2)) return true;
                } catch { }
            }
            return false;
        }

        private static bool IsExportCompletionSurface(AutomationElement root)
        {
            if (root == null) return false;
            string[] markers = {
                "视频已成功导出到本地", "已成功导出", "去发布", "Publish", "Go to TikTok Studio"
            };
            try {
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition)) {
                    string name = ControlLocator.SafeName(element);
                    if (markers.Any(marker => name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                }
            } catch { }
            return false;
        }

        private void SetExportFileName(AutomationElement exportRoot, string fileName, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(fileName)) {
                throw new ArgumentException("导出文件名不能为空", "fileName");
            }

            // This CapCut QML dialog does not expose the name field through UIA.
            // Its lab layout has a different vertical rhythm from the legacy
            // calibration: the old .807/.168 point lands on the output-folder
            // row, leaving the draft name in place.  The isolated profile uses
            // the visually verified name-field centre only; production keeps
            // its existing calibrated locator until this flow has passed lab E2E.
            if (RuntimeModes.IsolatedLabRuntime) {
                Rect bounds = exportRoot.Current.BoundingRectangle;
                int x = (int)(bounds.Left + bounds.Width * 0.89);
                int y = (int)(bounds.Top + bounds.Height * 0.15);
                NativeMethods.FocusWindow(new IntPtr(exportRoot.Current.NativeWindowHandle));
                NativeMethods.Click(x, y, 1);
                NativeMethods.SendControlChord(NativeMethods.VK_A);
                ControlLocator.PasteTextToForeground(fileName);
                SleepChecked(250, token);
                logger.Info(String.Format(
                    "隔离内测已在实际视频名称框写入本轮唯一名称: {0}，坐标=({1},{2})",
                    fileName, x, y));
                return;
            }

            locator.SetValue("ExportNameField", exportRoot, fileName, 4, token, true);
        }

        private void MoveOutputWhenUnlocked(string sourcePath, string destinationPath, int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            IOException lastLock = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                if (File.Exists(destinationPath)) {
                    throw new IOException("正式输出路径已存在，已阻止覆盖: " + destinationPath);
                }
                try {
                    File.Move(sourcePath, destinationPath);
                    return;
                } catch (IOException ex) {
                    lastLock = ex;
                    // A CapCut completion page can retain a delete/move handle
                    // after the MP4 is fully written while still allowing safe
                    // reads. Copy through a private temporary path and verify
                    // byte length. This promotes the already-finished artifact;
                    // it must never trigger another real export.
                    string copyTemporary = destinationPath + ".copying";
                    try {
                        if (File.Exists(copyTemporary)) File.Delete(copyTemporary);
                        File.Copy(sourcePath, copyTemporary, false);
                        long sourceLength = new FileInfo(sourcePath).Length;
                        long copyLength = new FileInfo(copyTemporary).Length;
                        if (sourceLength <= 0 || copyLength != sourceLength) {
                            throw new IOException("copied export length does not match the stable source");
                        }
                        File.Move(copyTemporary, destinationPath);
                        logger.Warn("CapCut 完成页仍持有源文件；已安全复制并核验本轮唯一成片，禁止重新导出: " +
                            destinationPath);
                        try { File.Delete(sourcePath); }
                        catch (IOException) {
                            logger.Warn("CapCut 默认导出桶中的原文件仍被占用，已保留等待会话退出后清理: " +
                                sourcePath);
                        }
                        return;
                    } catch (IOException copyError) {
                        lastLock = copyError;
                        try {
                            if (File.Exists(copyTemporary)) File.Delete(copyTemporary);
                        } catch { }
                    }
                    Thread.Sleep(500);
                }
            }
            throw new StableExportArtifactException(
                "CapCut 导出文件已经稳定但仍被占用；已禁止重复导出，可从该成片断点继续: " + sourcePath,
                lastLock);
        }

        private AutomationElement WaitForFolderDialog(int timeoutSeconds, CancellationToken token)
        {
            string[] names = { "请选择导出路径", "选择导出路径", "选择文件夹", "Select Folder", "Choose export path" };
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                AutomationElement dialog = locator.FindTopLevelWindow(names);
                if (dialog != null) return dialog;
                AutomationElement foreground = locator.ElementFromWindow(NativeMethods.GetForegroundWindow());
                if (foreground != null) {
                    string title = ControlLocator.SafeName(foreground);
                    string className = "";
                    try { className = foreground.Current.ClassName ?? ""; } catch { }
                    if (className == "#32770" || names.Any(name =>
                        title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) {
                        return foreground;
                    }
                }
                Thread.Sleep(200);
            }
            return null;
        }

        private AutomationElement WaitForExportWindow(IntPtr mainHandle, int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            DateTime lastEntitlementProbe = DateTime.MinValue;
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
                if (foregroundHandle != IntPtr.Zero && foregroundHandle != mainHandle &&
                    NativeMethods.BelongsToSameProcess(mainHandle, foregroundHandle)) {
                    if (DateTime.UtcNow - lastEntitlementProbe >= TimeSpan.FromSeconds(5)) {
                        lastEntitlementProbe = DateTime.UtcNow;
                        string screenshot = CaptureMainWindowForOcr("capcut-export-entitlement-probe.png");
                        string marker, diagnostic;
                        bool matched = VisibleTextOcr.TryContainsAny(screenshot,
                            new[] { "Get discount", "获得免费Pro", "您正在使用", "You're using Pro", "You are using Pro" },
                            out marker, out diagnostic, 2000);
                        token.ThrowIfCancellationRequested();
                        if (matched && ExportEntitlementPolicy.IsBlockingPrompt(diagnostic)) {
                            KeepEntitlementPromptEvidence(screenshot);
                            if (exportEntitlementReturnAttempted) throw ExportEntitlementPolicy.Failure();
                            exportEntitlementReturnAttempted = true; // consume before sending any action
                            logger.Info("已确认权益提示；只尝试一次精确返回编辑，不操作购买按钮");
                            // The prompt exposes no UIA button (10-16 11:18,
                            // 12:44, 14:23: "return-editor action not
                            // verified"), so close the prompt window itself,
                            // as for the environment-check window.
                            if (!ReturnFromEntitlementPrompt(foregroundHandle, token) &&
                                !CloseEntitlementPrompt(foregroundHandle, mainHandle))
                                throw ExportEntitlementPolicy.Failure();
                            SleepChecked(700, token);
                            if (NativeMethods.IsWindow(foregroundHandle) &&
                                NativeMethods.IsWindowVisible(foregroundHandle))
                                throw ExportEntitlementPolicy.Failure();
                            NativeMethods.FocusWindow(mainHandle);
                            SleepChecked(300, token);
                            if (NativeMethods.GetForegroundWindow() != mainHandle)
                                throw ExportEntitlementPolicy.Failure();
                            // All three 10-16 prompts exported normally in
                            // the next session; let the entitlement check
                            // settle before the single reopen.
                            SleepChecked(3000, token);
                            EnsureDirectDraftEditorBoundForExport();
                            logger.Info("返回编辑后已重新核验原编辑器；只重开一次导出");
                            NativeMethods.SendControlChord(NativeMethods.VK_E);
                            SleepChecked(700, token);
                            continue;
                        }
                        logger.Info("导出权益弹窗探测: matched=" + matched + "; " + diagnostic);
                    }
                    AutomationElement foreground = locator.ElementFromWindow(foregroundHandle);
                    AutomationElement ancestor = FindExportWindowAncestor(foreground, mainHandle);
                    if (ancestor != null) return ancestor;
                }

                AutomationElement topLevel = locator.FindTopLevelWindow(
                    new[] { "导出-", "Export-" },
                    Process.GetCurrentProcess().Id,
                    config.ProcessNames);
                if (IsExportWindowCandidate(topLevel, mainHandle)) return topLevel;
                Thread.Sleep(200);
            }
            return null;
        }

        private bool CloseEntitlementPrompt(IntPtr dialog, IntPtr mainHandle)
        {
            if (dialog == IntPtr.Zero || dialog == mainHandle ||
                !NativeMethods.BelongsToSameProcess(mainHandle, dialog) ||
                NativeMethods.GetForegroundWindow() != dialog) return false;
            NativeMethods.PostMessage(dialog, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(2)) {
                if (!NativeMethods.IsWindow(dialog) || !NativeMethods.IsWindowVisible(dialog)) {
                    logger.Info("已关闭 CapCut Pro 权益提示窗口（关闭消息，未点击购买）");
                    return true;
                }
                Thread.Sleep(100);
            }
            logger.Warn("CapCut Pro 权益提示窗口在关闭消息后 2 秒仍可见");
            return false;
        }

        private void KeepEntitlementPromptEvidence(string screenshot)
        {
            try {
                if (String.IsNullOrWhiteSpace(screenshot) || !File.Exists(screenshot)) return;
                string copy = Path.Combine(Path.GetDirectoryName(screenshot),
                    "capcut-export-entitlement-" + DateTime.Now.ToString("yyyyMMdd-HHmmss",
                        CultureInfo.InvariantCulture) + ".png");
                File.Copy(screenshot, copy, true);
                logger.Info("已保留 Pro 权益提示截图: " + copy);
            } catch (Exception ex) {
                logger.Warn("保留 Pro 权益提示截图失败: " + ex.Message);
            }
        }

        private bool ReturnFromEntitlementPrompt(IntPtr dialog, CancellationToken token)
        {
            string helper = Path.Combine(AppPaths.Root,"tools","CapCutFileDialogProbe.exe");
            if (!File.Exists(helper)) return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(dialog,out pid);
            try {
                long ticks = Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks;
                var info = new ProcessStartInfo {
                    FileName=helper, Arguments="--return-editor " + dialog.ToInt64() + " " + pid + " " + ticks,
                    UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true
                };
                using (Process probe=Process.Start(info)) {
                    var output=probe.StandardOutput.ReadToEndAsync();
                    var error=probe.StandardError.ReadToEndAsync();
                    if (!probe.WaitForExit(2000)) {
                        try { probe.Kill(); } catch { }
                        logger.Warn("返回编辑探测超过2秒；结果待核对，禁止再次发送");
                        return false;
                    }
                    token.ThrowIfCancellationRequested();
                    if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[]{output,error},500)) return false;
                    FileDialogProbePayload result=new JavaScriptSerializer().Deserialize<FileDialogProbePayload>(output.Result);
                    logger.Info("返回编辑独立操作回执: " + (result == null ? "missing" : result.diagnostic));
                    return probe.ExitCode==0 && result!=null && result.success && result.fieldVerified && result.pathSubmitted;
                }
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.Warn("返回编辑未确认: " + ex.Message); return false; }
        }

        private AutomationElement FindExportWindowAncestor(AutomationElement element, IntPtr mainHandle)
        {
            AutomationElement current = element;
            for (int level = 0; current != null && level < 8; level++) {
                if (IsExportWindowCandidate(current, mainHandle)) return current;
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch { return null; }
            }
            return null;
        }

        private static bool IsExportWindowCandidate(AutomationElement element, IntPtr mainHandle)
        {
            if (element == null) return false;
            try {
                IntPtr handle = new IntPtr(element.Current.NativeWindowHandle);
                if (handle == IntPtr.Zero || handle == mainHandle) return false;
                Rect bounds = element.Current.BoundingRectangle;
                if (bounds.Width < 500 || bounds.Height < 450) return false;
                string className = element.Current.ClassName ?? "";
                string title = ControlLocator.SafeName(element);
                return className.IndexOf("ExportWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.StartsWith("导出-", StringComparison.OrdinalIgnoreCase) ||
                    title.StartsWith("Export-", StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }

        private bool IsExportWindowForeground(IntPtr exportHandle)
        {
            IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
            AutomationElement foreground = locator.ElementFromWindow(foregroundHandle);
            AutomationElement exportRoot = FindExportWindowAncestor(foreground, GetLockedWindowHandle());
            if (exportRoot == null) return false;
            try { return new IntPtr(exportRoot.Current.NativeWindowHandle) == exportHandle; }
            catch { return false; }
        }

        public void ReturnToEditorAfterExport(int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            if (!CloseExportCompletionWindow(Math.Min(timeoutSeconds, 8), token)) {
                throw new InvalidOperationException(
                    "导出完成页仍然存在，已停止批次，避免下一条视频的导入被弹窗拦截");
            }
            RefreshWindow();
            FocusMainWindow();
            logger.Info("已关闭导出完成页并确认返回编辑器");
        }

        public void DeleteCurrentDraft(int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            if (!CloseExportCompletionWindow(Math.Min(timeoutSeconds, 8), token)) {
                throw new InvalidOperationException("导出完成页仍然存在，未删除草稿和原视频");
            }
            // CapCut 7.2 renders its home-page draft cards in QML without any
            // named UI Automation elements.  A card cannot therefore be safely
            // matched or deleted through the home page.  The draft-directory
            // baseline is exact and was captured before CreateProject.  CapCut can
            // create an additional empty bootstrap draft, so identity is the source
            // video recorded inside the actual draft instead of a directory count.
            bool usingLockedDirectDraft = directDraftStyleRequested &&
                directDraftIdentity != null &&
                !String.IsNullOrWhiteSpace(directDraftDirectory);
            string createdDraft = usingLockedDirectDraft
                ? GetIdentityLockedDirectDraftForCleanup()
                : GetUniqueCreatedDraftDirectory();
            logger.Info("已唯一确认本轮新增草稿目录，准备关闭 CapCut 后直接清理: " + Path.GetFileName(createdDraft));
            CloseCapCutSession();
            token.ThrowIfCancellationRequested();
            string confirmedDraft = usingLockedDirectDraft
                ? GetIdentityLockedDirectDraftForCleanup()
                : GetUniqueCreatedDraftDirectory();
            if (!String.Equals(Path.GetFullPath(createdDraft), Path.GetFullPath(confirmedDraft),
                StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("关闭 CapCut 前后唯一草稿身份发生变化，已停止删除");
            }
            CleanupCreatedDrafts(true, createdDraft, usingLockedDirectDraft);
            if (Directory.Exists(createdDraft)) {
                throw new InvalidOperationException("直接清理后草稿目录仍存在，已停止后续任务");
            }
            logger.Info("已从 CapCut 草稿索引和草稿目录中精确删除本轮唯一新增草稿");
        }

        private string GetIdentityLockedDirectDraftForCleanup()
        {
            if (directDraftIdentity == null ||
                String.IsNullOrWhiteSpace(directDraftDirectory) ||
                !Directory.Exists(directDraftDirectory)) {
                throw new InvalidOperationException("恢复草稿目录不存在，已阻止清理");
            }
            DirectDraftIdentity current = ReadDirectDraftIdentity(directDraftDirectory);
            if (current == null ||
                !String.Equals(current.DraftId, directDraftIdentity.DraftId,
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(current.DisplayName, directDraftIdentity.DisplayName,
                    StringComparison.Ordinal) ||
                !DraftContainsExpectedVideo(directDraftDirectory)) {
                throw new InvalidOperationException(
                    "恢复草稿的 draft_id、名称或源视频身份已变化，已阻止清理");
            }
            return Path.GetFullPath(directDraftDirectory);
        }

        public void CloseSession()
        {
            CloseCapCutSession();
        }

        public void ResetProject(int timeoutSeconds, CancellationToken token)
        {
            try {
                RefreshWindow();
                CloseExportCompletionWindow(3, token);
                RefreshWindow();
                locator.Click("BackToHome", mainWindow, Math.Min(timeoutSeconds, 5), token, false);
                SleepChecked(800, token);
            } finally {
                CloseCapCutSession();
                // A failed task has not satisfied the success gates.  Preserve its
                // newly-created draft for diagnosis; draft cleanup is allowed only
                // through DeleteCurrentDraft after a stable export.
                logger.Warn("任务失败或重置：已保留本轮新增草稿，未执行文件系统删除");
            }
        }

        private bool CloseExportCompletionWindow(int timeoutSeconds, CancellationToken token,
            bool stableOutputKnown = false)
        {
            IntPtr mainHandle = GetLockedWindowHandle();
            AutomationElement completion = null;
            if (activeExportHandle != IntPtr.Zero) {
                AutomationElement known = locator.ElementFromWindow(activeExportHandle);
                if (IsExportWindowCandidate(known, mainHandle)) {
                    completion = known;
                    logger.Info("已通过本轮登记的 exportHandle 重新绑定导出完成页: " +
                        activeExportHandle);
                }
            }
            if (completion == null) {
                completion = WaitForExportWindow(mainHandle, Math.Min(timeoutSeconds, 3), token);
            }
            if (completion == null) return true;

            IntPtr completionHandle = new IntPtr(completion.Current.NativeWindowHandle);
            NativeMethods.FocusWindow(completionHandle);
            SleepChecked(250, token);
            string completionScreenshot = CaptureElementWindow(
                completion, "capcut-export-completion-ocr.png");
            string matchedMarker;
            string ocrDiagnostic;
            bool completionByOcr = VisibleTextOcr.TryContainsAny(
                completionScreenshot,
                new[] {
                    "视频已成功导出到本地",
                    "已成功导出",
                    "去发布",
                    "Video exported successfully",
                    "Exported successfully"
                },
                out matchedMarker,
                out ocrDiagnostic);
            bool completionByUi = IsExportCompletionSurface(completion);
            if (completionByOcr) {
                logger.Info("Windows OCR 已确认 CapCut 导出完成页: " +
                    matchedMarker + "；" + ocrDiagnostic);
            } else if (completionByUi) {
                logger.Info("UI Automation 已确认 CapCut 导出完成页；OCR诊断: " +
                    ocrDiagnostic);
            } else if (stableOutputKnown) {
                logger.Warn("本轮唯一导出文件已经稳定，但完成页文本未暴露；按已登记 exportHandle 关闭，禁止重新导出。OCR诊断: " +
                    ocrDiagnostic);
            }
            var closeRule = new LocatorRule {
                Names = new List<string> {
                    "关闭", "Close", "完成", "Done", "返回首页", "Back to home"
                }
            };
            AutomationElement close = locator.FindByRule(closeRule, completion);
            if (close != null) {
                ControlLocator.ClickElement(close, 1);
                logger.Info("已通过语义控件点击导出完成页的关闭按钮");
            } else {
                Rect bounds = completion.Current.BoundingRectangle;
                int closeX = (int)(bounds.Left + bounds.Width * 0.93);
                int closeY = (int)(bounds.Top + bounds.Height * 0.96);
                NativeMethods.Click(closeX, closeY, 1);
                logger.Info(String.Format(
                    "已通过导出完成页自身坐标点击右下角关闭按钮: ({0}, {1})", closeX, closeY));
            }

            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                AutomationElement current = locator.ElementFromWindow(completionHandle);
                if (!IsExportWindowCandidate(current, mainHandle)) {
                    activeExportHandle = IntPtr.Zero;
                    return true;
                }
                Thread.Sleep(200);
            }

            NativeMethods.FocusWindow(completionHandle);
            NativeMethods.PressEscape();
            SleepChecked(500, token);
            bool closed = !IsExportWindowCandidate(
                locator.ElementFromWindow(completionHandle), mainHandle);
            // The same close coordinate works on most pages but was dropped 13
            // times in 178 (10-16, 10-22). Once the page is proved to be this
            // export's completion surface, ask that window alone to close; the
            // editor and the export button are never touched.
            if (!closed && ExportCompletionClosePolicy.MayPostClose(
                    completionByOcr, completionByUi, stableOutputKnown,
                    NativeMethods.BelongsToSameProcess(mainHandle, completionHandle))) {
                NativeMethods.PostMessage(completionHandle, NativeMethods.WM_CLOSE,
                    IntPtr.Zero, IntPtr.Zero);
                logger.Warn("导出完成页点击与 Esc 后仍在；已向经核实的完成页窗口发送一次关闭消息");
                Stopwatch posted = Stopwatch.StartNew();
                while (!closed && posted.Elapsed < TimeSpan.FromSeconds(3)) {
                    SleepChecked(200, token);
                    closed = !IsExportWindowCandidate(
                        locator.ElementFromWindow(completionHandle), mainHandle);
                }
            }
            if (closed) activeExportHandle = IntPtr.Zero;
            return closed;
        }

        private void ConfigureExportResolution(AutomationElement exportRoot, CancellationToken token)
        {
            string target = String.IsNullOrWhiteSpace(config.ExportResolution) ? "2K" : config.ExportResolution.Trim();
            if (!String.Equals(target, "2K", StringComparison.OrdinalIgnoreCase)) {
                logger.Warn("当前版本只验证了 2K 导出，将继续使用配置值: " + target);
            }

            LocatorRule controlRule = config.GetRule("ExportResolution");
            LocatorRule optionRule = config.GetRule("ExportResolution2K");
            if (!controlRule.HasCoordinate || !optionRule.HasCoordinate) {
                logger.Info("未校准导出分辨率下拉框，保留 CapCut 当前分辨率；本机应预先设置为 2K");
                return;
            }
            locator.ClickCoordinateNow("ExportResolution", exportRoot);
            SleepChecked(350, token);
            locator.ClickCoordinateNow("ExportResolution2K", exportRoot);
            SleepChecked(350, token);
            logger.Info("导出分辨率已选择 2K");
        }

        private void CaptureDraftBaseline()
        {
            if (String.IsNullOrWhiteSpace(draftRoot)) draftRoot = ResolveDraftRoot();
            draftBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(draftRoot) || !Directory.Exists(draftRoot)) return;
            Exception last = null;
            for (int attempt = 1; attempt <= 10; attempt++) {
                try {
                    foreach (string directory in Directory.GetDirectories(draftRoot)) {
                        draftBaseline.Add(Path.GetFullPath(directory));
                    }
                    last = null;
                    break;
                } catch (IOException ex) {
                    last = ex;
                    draftBaseline.Clear();
                    logger.Warn(String.Format("草稿目录暂不可用，等待挂载点就绪 ({0}/10): {1}", attempt, ex.Message));
                    Thread.Sleep(500);
                }
            }
            if (last != null) throw new IOException("无法读取草稿目录基线，已阻止创建项目", last);
            logger.Info(String.Format("已记录草稿基线: {0} 个", draftBaseline.Count));
            try {
                draftBaselineIndexHash = File.Exists(draftIndexPath)
                    ? HashText(File.ReadAllText(draftIndexPath, Encoding.UTF8))
                    : HashText(String.Join("\n", draftBaseline.OrderBy(value => value,
                        StringComparer.OrdinalIgnoreCase).ToArray()));
            } catch {
                draftBaselineIndexHash = HashText(String.Join("\n", draftBaseline.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }

        private DraftCreationCheckpoint CreateDraftCreationCheckpoint()
        {
            DraftRootLeaseRecord lease = draftRootLeaseManager == null
                ? null : draftRootLeaseManager.Current;
            string[] baseline = draftBaseline == null
                ? new string[0]
                : draftBaseline.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).ToArray();
            return new DraftCreationCheckpoint {
                StableTaskArchiveKey = lease == null ? "" : lease.StableTaskArchiveKey ?? "",
                LeaseId = lease == null ? "" : lease.LeaseId ?? "",
                AttemptId = lease == null ? "" : lease.AttemptId ?? "",
                BaselineDraftIds = baseline,
                BaselineIndexHash = String.IsNullOrWhiteSpace(draftBaselineIndexHash)
                    ? HashText(String.Join("\n", baseline)) : draftBaselineIndexHash,
                CreationPhase = draftCreationPhase ?? "baseline_captured",
                CreateCommandId = draftCreationCheckpoint == null
                    ? "" : (draftCreationCheckpoint.CreateCommandId ?? "")
            };
        }

        private static string HashText(string value)
        {
            using (SHA256 sha = SHA256.Create()) {
                return BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(value ?? "")))
                    .Replace("-", "").ToLowerInvariant();
            }
        }

        private string ResolveDraftRoot()
        {
            if (!String.IsNullOrWhiteSpace(config.DraftRoot)) {
                string configuredRoot = Environment.ExpandEnvironmentVariables(config.DraftRoot.Trim());
                if (Directory.Exists(configuredRoot)) {
                    logger.Info("使用配置的草稿目录: " + configuredRoot);
                    SetDefaultDraftIndexPath();
                    return configuredRoot;
                }
                logger.Warn("配置的草稿目录不可用，将自动检测: " + configuredRoot);
            }
            string product = (!String.IsNullOrWhiteSpace(config.CapCutPath) &&
                              config.CapCutPath.IndexOf("JianyingPro", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "JianyingPro" : "CapCut";
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), product, "User Data");
            string defaultRoot = Path.Combine(userData, "Projects", "com.lveditor.draft");
            draftIndexPath = Path.Combine(defaultRoot, "root_meta_info.json");

            string settingsPath = Path.Combine(userData, "Config", "globalSetting");
            try {
                if (File.Exists(settingsPath)) {
                    const string key = "currentCustomDraftPath=";
                    foreach (string line in File.ReadAllLines(settingsPath, Encoding.UTF8)) {
                        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                        string customRoot = line.Substring(key.Length).Trim().Trim('"')
                            .Replace("\\\\", "\\").Replace('/', '\\');
                        if (Directory.Exists(customRoot)) {
                            logger.Info("已检测到自定义草稿目录: " + customRoot);
                            return customRoot;
                        }
                        logger.Warn("配置的自定义草稿目录不可用，将检查默认目录: " + customRoot);
                        break;
                    }
                }
            } catch (Exception ex) {
                logger.Warn("读取自定义草稿目录失败，将检查默认目录: " + ex.Message);
            }
            return defaultRoot;
        }

        private void SetDefaultDraftIndexPath()
        {
            string product = (!String.IsNullOrWhiteSpace(config.CapCutPath) &&
                              config.CapCutPath.IndexOf("JianyingPro", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "JianyingPro" : "CapCut";
            draftIndexPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                product, "User Data", "Projects", "com.lveditor.draft", "root_meta_info.json");
        }

        private void CloseCapCutSession()
        {
            // Export can respawn CapCut's home shell after the editor closes.
            // Re-enumerate for a short bounded period so an owned home shell is
            // not left behind, while never touching an editor that pre-dated
            // this automation session.
            Stopwatch closeWatch = Stopwatch.StartNew();
            TimeSpan quietRequired = sessionLaunchTimedOut
                ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2);
            DateTime quietSince = DateTime.MinValue;
            while (closeWatch.Elapsed < TimeSpan.FromSeconds(22)) {
                RegisterOwnedSessionProcesses();
                List<Process> candidates = GetConfiguredCapCutProcesses()
                    .Where(IsOwnedSessionProcess).ToList();
                if (candidates.Count == 0) {
                    if (quietSince == DateTime.MinValue) quietSince = DateTime.UtcNow;
                    if (DateTime.UtcNow - quietSince >= quietRequired) break;
                    Thread.Sleep(250);
                    continue;
                }
                quietSince = DateTime.MinValue;
                bool closeRequested = false;
                foreach (Process candidate in candidates.Where(item => item.MainWindowHandle != IntPtr.Zero)) {
                    try { closeRequested |= candidate.CloseMainWindow(); } catch { }
                }
                // CapCut saves root_meta_info.json and the draft index while it
                // shuts down.  Killing it 0.7 s after the close request left a
                // 0-byte root index and a stale draft id (2026-10-11 15:02 and
                // 16:05).  CapCut 9.2 never exits on its own after the close
                // request (35/35 forced on 10-11 evening), so waiting for the
                // exit only cost 8 s per close.  Wait instead until the draft
                // files have been quiet for 1.5 s (at most 8 s).
                Stopwatch graceWatch = Stopwatch.StartNew();
                TimeSpan grace = closeRequested ? TimeSpan.FromSeconds(8) : TimeSpan.FromMilliseconds(700);
                TimeSpan quietWrites = TimeSpan.FromMilliseconds(1500);
                bool writesSettled = false;
                do {
                    Thread.Sleep(250);
                    if (closeRequested && graceWatch.Elapsed >= quietWrites &&
                        DraftFilesQuietFor(quietWrites)) {
                        writesSettled = true;
                        break;
                    }
                } while (graceWatch.Elapsed < grace && candidates.Any(item => {
                    try { item.Refresh(); return !item.HasExited; } catch { return false; }
                }));
                int forced = 0;
                foreach (Process candidate in candidates) {
                    try {
                        candidate.Refresh();
                        if (!candidate.HasExited) {
                            candidate.Kill();
                            forced++;
                        }
                    } catch { }
                    try { candidate.Dispose(); } catch { }
                }
                if (forced > 0 && closeRequested) {
                    string closeMessage = String.Format(writesSettled
                        ? "CapCut 草稿文件已静止，请求关闭 {0:0.0}s 后结束 {1} 个进程"
                        : "CapCut 在请求关闭后 {0:0.0}s 内草稿文件仍在变化，已强制结束 {1} 个进程",
                        graceWatch.Elapsed.TotalSeconds, forced);
                    if (writesSettled) logger.Info(closeMessage); else logger.Warn(closeMessage);
                }
                Thread.Sleep(350);
            }
            RegisterOwnedSessionProcesses();
            List<Process> remainingOwned = GetConfiguredCapCutProcesses()
                .Where(IsOwnedSessionProcess).ToList();
            bool fullyClosed = remainingOwned.Count == 0;
            lastCloseLeftOwnedProcesses = !fullyClosed;
            foreach (Process candidate in remainingOwned) {
                try { candidate.Dispose(); } catch { }
            }
            if (fullyClosed) {
                ownedSessionProcessIds.Clear();
                preLaunchSessionProcessIds.Clear();
                sessionStartedUtc = DateTime.MinValue;
                sessionLaunchTimedOut = false;
            } else {
                logger.Warn("CAPCUT_OWNED_PROCESS_EXIT_TIMEOUT: this task's CapCut process did not exit; ownership is retained for safe recovery");
            }
            process = null;
            mainWindow = null;
            directDraftHomeHandle = IntPtr.Zero;
            directDraftEditorHandle = IntPtr.Zero;
            activeExportHandle = IntPtr.Zero;
            logger.Info("已关闭本次剪映会话，准备清理任务草稿");
        }

        // True when no file directly under the draft root, or directly inside
        // one of its draft folders, was written during the last `quiet`.
        // Read failures count as "still writing" so the caller keeps waiting.
        private bool DraftFilesQuietFor(TimeSpan quiet)
        {
            string root = draftRoot;
            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return true;
            try {
                DateTime threshold = DateTime.UtcNow - quiet;
                var folders = new List<string> { root };
                folders.AddRange(Directory.GetDirectories(root));
                foreach (string folder in folders) {
                    foreach (string file in Directory.GetFiles(folder)) {
                        if (File.GetLastWriteTimeUtc(file) > threshold) return false;
                    }
                }
                return true;
            } catch (IOException) { return false; }
              catch (UnauthorizedAccessException) { return false; }
        }

        private List<Process> GetConfiguredCapCutProcesses()
        {
            var candidates = new List<Process>();
            foreach (string configuredName in config.ProcessNames ?? new string[0]) {
                string name = Path.GetFileNameWithoutExtension(configuredName);
                try { candidates.AddRange(Process.GetProcessesByName(name)); } catch { }
            }
            return candidates.GroupBy(candidate => candidate.Id).Select(group => group.First()).ToList();
        }

        private void RegisterOwnedSessionProcesses()
        {
            foreach (Process candidate in GetConfiguredCapCutProcesses()) {
                try {
                    if (candidate.Id == (process == null ? -1 : process.Id) ||
                        CapCutSessionOwnership.IsOwnedCandidate(candidate.Id,
                            candidate.StartTime.ToUniversalTime(),
                            preLaunchSessionProcessIds, sessionStartedUtc)) {
                        ownedSessionProcessIds.Add(candidate.Id);
                    }
                } catch { }
                finally { try { candidate.Dispose(); } catch { } }
            }
        }

        private bool IsOwnedSessionProcess(Process candidate)
        {
            try {
                if (ownedSessionProcessIds.Contains(candidate.Id)) return true;
                return CapCutSessionOwnership.IsOwnedCandidate(candidate.Id,
                    candidate.StartTime.ToUniversalTime(),
                    preLaunchSessionProcessIds, sessionStartedUtc);
            } catch { return false; }
        }

        private void CaptureSessionProcessBaselineIfNeeded()
        {
            if (sessionStartedUtc != DateTime.MinValue ||
                preLaunchSessionProcessIds.Count > 0) return;
            foreach (Process candidate in GetConfiguredCapCutProcesses()) {
                try { preLaunchSessionProcessIds.Add(candidate.Id); }
                catch { }
                finally { try { candidate.Dispose(); } catch { } }
            }
            logger.Info("Captured pre-launch CapCut process baseline: " +
                preLaunchSessionProcessIds.Count);
        }

        private Process FindOwnedSessionProcess()
        {
            foreach (Process candidate in GetConfiguredCapCutProcesses()) {
                if (IsOwnedSessionProcess(candidate)) {
                    ownedSessionProcessIds.Add(candidate.Id);
                    return candidate;
                }
                try { candidate.Dispose(); } catch { }
            }
            return null;
        }

        private void TerminateAllCapCutProcesses(int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (true) {
                token.ThrowIfCancellationRequested();
                var candidates = new List<Process>();
                foreach (string configuredName in config.ProcessNames ?? new string[0]) {
                    string name = Path.GetFileNameWithoutExtension(configuredName);
                    try { candidates.AddRange(Process.GetProcessesByName(name)); } catch { }
                }
                if (candidates.Count == 0) return;
                foreach (Process candidate in candidates) {
                    try {
                        candidate.Refresh();
                        if (candidate.MainWindowHandle != IntPtr.Zero) candidate.CloseMainWindow();
                    } catch { }
                }
                Thread.Sleep(700);
                foreach (Process candidate in candidates) {
                    try {
                        candidate.Refresh();
                        if (!candidate.HasExited) candidate.Kill();
                    } catch { }
                    try { candidate.Dispose(); } catch { }
                }
                if (watch.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds)) {
                    throw new TimeoutException("CapCut background processes did not exit before the next task");
                }
                Thread.Sleep(350);
            }
        }

        private Process FindProcessWithUsableMainWindow()
        {
            foreach (string configuredName in config.ProcessNames ?? new string[0]) {
                string processName = Path.GetFileNameWithoutExtension(configuredName);
                Process[] candidates;
                try { candidates = Process.GetProcessesByName(processName); }
                catch { continue; }
                foreach (Process candidate in candidates) {
                    try {
                        candidate.Refresh();
                        if (candidate.MainWindowHandle != IntPtr.Zero) return candidate;
                    } catch {
                        try { candidate.Dispose(); } catch { }
                    }
                    try { candidate.Dispose(); } catch { }
                }
            }

            // CapCut can host its editor window in a helper process.  Use the
            // top-level UIA window as a second safety signal before touching any
            // headless process.  A real editor has a substantial on-screen area;
            // tiny hidden helper windows do not count as an editable session.
            try {
                AutomationElement candidate = locator.FindTopLevelWindow(
                    config.MainWindowNames, Process.GetCurrentProcess().Id, config.ProcessNames);
                if (candidate != null) {
                    Rect bounds = candidate.Current.BoundingRectangle;
                    if (!candidate.Current.IsOffscreen && bounds.Width >= 640 && bounds.Height >= 400) {
                        int processId = candidate.Current.ProcessId;
                        if (processId > 0) return Process.GetProcessById(processId);
                    }
                }
            } catch { }
            return null;
        }

        private void TerminateHeadlessCapCutProcesses(int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (true) {
                token.ThrowIfCancellationRequested();
                Process visibleEditor = FindProcessWithUsableMainWindow();
                if (visibleEditor != null) {
                    try {
                        throw new InvalidOperationException(
                            "清理后台残留前检测到可见 CapCut 编辑器（PID " + visibleEditor.Id +
                            "），已停止自动清理以保护用户草稿。");
                    } finally {
                        try { visibleEditor.Dispose(); } catch { }
                    }
                }

                var candidates = new List<Process>();
                foreach (string configuredName in config.ProcessNames ?? new string[0]) {
                    string processName = Path.GetFileNameWithoutExtension(configuredName);
                    try { candidates.AddRange(Process.GetProcessesByName(processName)); } catch { }
                }
                if (candidates.Count == 0) return;

                bool accessDenied = false;
                foreach (Process candidate in candidates) {
                    try {
                        candidate.Refresh();
                        if (candidate.MainWindowHandle != IntPtr.Zero) {
                            throw new InvalidOperationException(
                                "后台残留清理期间进程 " + candidate.Id +
                                " 出现主窗口，已停止自动清理以保护用户草稿。");
                        }
                        logger.Info("结束无窗口 CapCut 后台残留进程: PID " + candidate.Id);
                        candidate.Kill();
                    } catch (System.ComponentModel.Win32Exception ex) {
                        // Some CapCut helper processes are protected by their
                        // own elevated launcher.  They have no editor window and
                        // cannot be attached to safely, but failure to terminate
                        // them must not make the isolated draft reopen fail.  A
                        // fresh launcher process can still create the editor.
                        accessDenied = true;
                        logger.Warn("无窗口 CapCut 后台进程无法结束（PID " + candidate.Id +
                            "，" + ex.Message + "）；将保留该辅助进程并继续启动新的隔离编辑器会话");
                    } catch (UnauthorizedAccessException ex) {
                        accessDenied = true;
                        logger.Warn("无窗口 CapCut 后台进程拒绝访问（PID " + candidate.Id +
                            "，" + ex.Message + "）；将保留该辅助进程并继续启动新的隔离编辑器会话");
                    } finally {
                        try { candidate.Dispose(); } catch { }
                    }
                }

                if (accessDenied) return;

                if (watch.Elapsed > TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds))) {
                    throw new TimeoutException("无窗口 CapCut 后台残留在限定时间内未退出，已停止启动新会话");
                }
                Thread.Sleep(300);
            }
        }

        private void CleanupCreatedDrafts(bool requireIndexUpdate, string expectedDraftDirectory,
            bool identityLocked = false)
        {
            // A task resumed from a checkpoint has no pre-creation baseline.
            // Its draft identity was already confirmed twice by the caller
            // (before and after closing CapCut), which is a stronger proof
            // than the baseline, so it must not be skipped silently.
            if (identityLocked && draftBaseline == null) {
                DeleteIdentityLockedDraft(requireIndexUpdate, expectedDraftDirectory);
                return;
            }
            if (draftBaseline == null || String.IsNullOrWhiteSpace(draftRoot)) return;
            string[] created = null;
            Exception enumerationFailure = null;
            for (int attempt = 1; attempt <= 10; attempt++) {
                try {
                    created = Directory.GetDirectories(draftRoot)
                        .Select(Path.GetFullPath)
                        .Where(path => !draftBaseline.Contains(path))
                        .ToArray();
                    enumerationFailure = null;
                    break;
                } catch (IOException ex) {
                    enumerationFailure = ex;
                    logger.Warn(String.Format("清理前草稿目录暂不可用，等待挂载点恢复 ({0}/10): {1}", attempt, ex.Message));
                    Thread.Sleep(500);
                }
            }
            if (enumerationFailure != null) {
                throw new IOException("无法重新读取草稿目录，未执行任何草稿删除", enumerationFailure);
            }
            if (String.IsNullOrWhiteSpace(expectedDraftDirectory) ||
                !created.Any(path => String.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedDraftDirectory), StringComparison.OrdinalIgnoreCase))) {
                throw new InvalidOperationException("当前草稿不再属于本轮新增目录，未执行任何删除");
            }
            RemoveDraftIndexAndDirectory(requireIndexUpdate, Path.GetFullPath(expectedDraftDirectory));
        }

        private void DeleteIdentityLockedDraft(bool requireIndexUpdate, string expectedDraftDirectory)
        {
            if (String.IsNullOrWhiteSpace(expectedDraftDirectory) ||
                String.IsNullOrWhiteSpace(directDraftDirectory) ||
                !String.Equals(Path.GetFullPath(expectedDraftDirectory), Path.GetFullPath(directDraftDirectory),
                    StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("待删除草稿不是已锁定身份的恢复草稿，未执行任何删除");
            }
            string full = Path.GetFullPath(expectedDraftDirectory);
            if (String.IsNullOrWhiteSpace(draftRoot) ||
                !full.StartsWith(Path.GetFullPath(draftRoot).TrimEnd('\\') + "\\",
                    StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("恢复草稿不在当前草稿根目录下，未执行任何删除");
            }
            logger.Info("续跑任务没有草稿基线，按已两次确认的草稿身份精确清理: " + Path.GetFileName(full));
            RemoveDraftIndexAndDirectory(requireIndexUpdate, full);
        }

        private void RemoveDraftIndexAndDirectory(bool requireIndexUpdate, string expectedDraftDirectory)
        {
            string[] currentDraftOnly = new[] { expectedDraftDirectory };

            try {
                RemoveDraftIndexEntries(currentDraftOnly);
            } catch (Exception ex) {
                if (requireIndexUpdate) {
                    throw new IOException("草稿索引更新失败，未删除草稿目录和原视频", ex);
                }
                logger.Warn("草稿索引不可用，继续删除本轮新增草稿目录: " + ex.Message);
            }
            foreach (string directory in currentDraftOnly) {
                Exception last = null;
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        if (Directory.Exists(directory)) Directory.Delete(directory, true);
                        last = null;
                        break;
                    } catch (Exception ex) {
                        last = ex;
                        Thread.Sleep(400);
                    }
                }
                if (last != null) throw new IOException("删除任务草稿失败: " + directory, last);
                logger.Info("已删除任务草稿: " + Path.GetFileName(directory));
            }
            draftBaseline = null;
            draftExpectedVideoPath = null;
        }

        private string GetUniqueCreatedDraftDirectory()
        {
            if (draftBaseline == null || String.IsNullOrWhiteSpace(draftRoot) || !Directory.Exists(draftRoot))
                throw new InvalidOperationException("缺少本轮草稿基线，无法唯一确认当前草稿，已阻止原视频删除");
            string[] created = Directory.GetDirectories(draftRoot)
                .Select(Path.GetFullPath)
                .Where(path => !draftBaseline.Contains(path))
                .ToArray();
            if (created.Length == 0)
                throw new InvalidOperationException("没有检测到本条任务新增的草稿，已阻止原视频删除");
            if (String.IsNullOrWhiteSpace(draftExpectedVideoPath))
                throw new InvalidOperationException("缺少本轮源视频身份，无法安全确认当前草稿，已阻止原视频删除");
            string[] matches = created.Where(DraftContainsExpectedVideo).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(String.Format(
                    "检测到 {0} 个新增草稿，其中匹配本轮源视频的草稿为 {1} 个，无法唯一确认当前任务，已阻止原视频删除", created.Length, matches.Length));
            return matches[0];
        }

        private bool recoveringCreatedDraft;

        private bool HasLockedReopenSourceEvidence(string directory)
        {
            if (!recoveringCreatedDraft) return DraftContainsExpectedVideo(directory);
            if (draftCreationCheckpoint == null || !String.Equals(Path.GetFullPath(directory),
                Path.GetFullPath(draftCreationCheckpoint.DiscoveredDraftPath), StringComparison.OrdinalIgnoreCase)) return false;
            // Creation-only recovery may contain a media-bin record but no
            // timeline material yet. Never relax media-stage/cleanup checks.
            foreach (string file in Directory.GetFiles(directory, "draft_content.json", SearchOption.AllDirectories)) {
                var data = new JavaScriptSerializer { MaxJsonLength=Int32.MaxValue }.DeserializeObject(
                    File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                object tracks;
                if (data == null || !data.TryGetValue("tracks", out tracks)) return false;
                foreach (object raw in tracks as System.Collections.IEnumerable ?? new object[0]) {
                    var track = raw as Dictionary<string, object>;
                    object segments;
                    if (track == null || !track.TryGetValue("segments", out segments)) return false;
                    if ((segments as System.Collections.IEnumerable ?? new object[0]).Cast<object>().Any()) return false;
                }
            }
            string metadata=Path.Combine(directory,"draft_meta_info.json");
            return File.Exists(metadata) && !String.IsNullOrWhiteSpace(MediaImportFacts.FindMaterialId(
                File.ReadAllText(metadata,Encoding.UTF8),draftExpectedVideoPath));
        }

        private bool DraftContainsExpectedVideo(string draftDirectory)
        {
            try {
                if (String.IsNullOrWhiteSpace(draftExpectedVideoPath) ||
                    !Directory.Exists(draftDirectory)) return false;
                string expected = NormalizeDraftMediaPath(draftExpectedVideoPath);
                string[] files = Directory.GetFiles(draftDirectory,
                    "draft_content.json", SearchOption.AllDirectories);
                foreach (string contentPath in files) {
                    string content = File.ReadAllText(contentPath, Encoding.UTF8)
                        .Replace("\\/", "/")
                        .Replace("\\\\", "/");
                    if (content.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            } catch (Exception ex) {
                logger.Warn("读取新增草稿媒体身份失败，已保留草稿: " + Path.GetFileName(draftDirectory) + " / " + ex.Message);
                return false;
            }
        }

        private static string NormalizeDraftMediaPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            return Path.GetFullPath(path.Trim()).Replace('\\', '/');
        }

        private void DeleteCreatedDraftThroughCapCutUi(string draftDirectory, int timeoutSeconds, CancellationToken token)
        {
            RefreshWindow();
            FocusForCoordinateAction(token);
            string displayName = ResolveDraftDisplayName(draftDirectory);
            if (String.IsNullOrWhiteSpace(displayName)) {
                throw new InvalidOperationException("无法从唯一草稿读取显示名称，未通过 CapCut 界面删除，已阻止原视频删除");
            }
            AutomationElement tile = FindVisibleNamedElement(mainWindow, displayName);
            if (tile == null) {
                throw new InvalidOperationException("CapCut 首页未找到唯一草稿卡片“" + displayName + "”，已阻止原视频删除");
            }
            Rect tileBounds = tile.Current.BoundingRectangle;
            NativeMethods.RightClick((int)(tileBounds.Left + tileBounds.Width / 2), (int)(tileBounds.Top + tileBounds.Height / 2));
            logger.Info("已在 CapCut 首页右键唯一草稿卡片: " + displayName);
            SleepChecked(500, token);
            AutomationElement delete = FindCapCutAction(new[] { "删除", "Delete" }, ControlType.MenuItem, ControlType.Button);
            if (delete == null) {
                throw new InvalidOperationException("唯一草稿的 CapCut 右键菜单没有“删除”，未执行文件系统兜底删除");
            }
            ControlLocator.ClickElement(delete, 1);
            SleepChecked(500, token);
            if (Directory.Exists(draftDirectory)) {
                AutomationElement confirm = FindCapCutAction(
                    new[] { "确定", "确认", "删除", "Confirm", "Delete" }, ControlType.Button);
                if (confirm == null) {
                    throw new InvalidOperationException("CapCut 未出现可确认的草稿删除按钮，已保留草稿和原视频");
                }
                ControlLocator.ClickElement(confirm, 1);
            }
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(draftDirectory)) {
                    draftBaseline = null;
                    return;
                }
                Thread.Sleep(300);
            }
            throw new InvalidOperationException("CapCut 界面确认删除后草稿目录仍存在，已保留原视频并停止后续任务");
        }

        private AutomationElement FindCapCutAction(string[] names, params ControlType[] allowedTypes)
        {
            int processId = mainWindow == null ? 0 : mainWindow.Current.ProcessId;
            AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement window in windows) {
                try {
                    if (processId != 0 && window.Current.ProcessId != processId) continue;
                    AutomationElementCollection descendants = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    foreach (AutomationElement element in descendants) {
                        string name = ControlLocator.SafeName(element);
                        if (!names.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                            name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        ControlType type = element.Current.ControlType;
                        if (allowedTypes.Length == 0 || allowedTypes.Contains(type)) return element;
                    }
                } catch { }
            }
            return null;
        }

        private static AutomationElement FindVisibleNamedElement(AutomationElement root, string name)
        {
            if (root == null || String.IsNullOrWhiteSpace(name)) return null;
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in elements) {
                try {
                    string candidate = ControlLocator.SafeName(element);
                    if (!candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    Rect bounds = element.Current.BoundingRectangle;
                    if (!element.Current.IsOffscreen && bounds.Width > 5 && bounds.Height > 5) return element;
                } catch { }
            }
            return null;
        }

        private static string ResolveDraftDisplayName(string draftDirectory)
        {
            string[] candidates = { "draft_meta_info.json", "draft_content.json" };
            foreach (string fileName in candidates) {
                string path = Path.Combine(draftDirectory, fileName);
                if (!File.Exists(path)) continue;
                try {
                    object root = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 }
                        .DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                    string value = FindJsonString(root, new[] { "draft_name", "name" });
                    if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
                } catch { }
            }
            return "";
        }

        private DirectDraftIdentity ReadDirectDraftIdentity(string draftDirectory, bool logFailure = true)
        {
            if (String.IsNullOrWhiteSpace(draftDirectory)) return null;
            string metaPath = Path.Combine(draftDirectory, "draft_meta_info.json");
            if (!File.Exists(metaPath)) return null;
            try {
                string json = File.ReadAllText(metaPath, Encoding.UTF8);
                object root = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 }
                    .DeserializeObject(json);
                string draftId = FindJsonString(root, new[] { "draft_id" });
                string draftName = FindJsonString(root, new[] { "draft_name" });
                Match created = Regex.Match(json, "\\\"tm_draft_create\\\"\\s*:\\s*(\\d+)");
                long createdOrdinal;
                if (!Int64.TryParse(created.Success ? created.Groups[1].Value : "", NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out createdOrdinal)) createdOrdinal = 0;
                return new DirectDraftIdentity {
                    Directory = Path.GetFullPath(draftDirectory),
                    DraftId = draftId ?? "",
                    DisplayName = draftName ?? "",
                    CreatedOrdinal = createdOrdinal
                };
            } catch (Exception ex) {
                logger.Warn("读取实验草稿身份失败: " + Path.GetFileName(draftDirectory) + " / " + ex.Message);
                return null;
            }
        }

        private DirectDraftIdentity ReadDirectDraftIdentityWithRetry(string draftDirectory, int timeoutSeconds,
            CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Exception lastFailure = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                try {
                    DirectDraftIdentity identity = ReadDirectDraftIdentity(draftDirectory, false);
                    if (identity != null && !String.IsNullOrWhiteSpace(identity.DraftId) &&
                        !String.IsNullOrWhiteSpace(identity.DisplayName)) return identity;
                    identity = ReadDirectDraftIdentityFromTaskIndex(draftDirectory);
                    if (identity != null && !String.IsNullOrWhiteSpace(identity.DraftId) &&
                        !String.IsNullOrWhiteSpace(identity.DisplayName)) {
                        logger.Info("CapCut draft metadata was empty; restored the exact identity from the task-scoped root index: draft=" +
                            identity.DisplayName + ", draftId=" + identity.DraftId);
                        return identity;
                    }
                } catch (Exception ex) {
                    lastFailure = ex;
                }
                Thread.Sleep(350);
            }
            if (lastFailure != null) {
                logger.Warn("实验草稿元数据在等待后仍不可读: " + lastFailure.Message);
            } else {
                logger.Warn("实验草稿元数据在等待 " + timeoutSeconds + " 秒后仍未形成有效身份");
            }
            return null;
        }

        private DirectDraftIdentity ReadDirectDraftIdentityFromTaskIndex(
            string draftDirectory)
        {
            string draftId, draftName;
            long createdOrdinal;
            if (!DraftIndexIdentityPolicy.TryResolveSoleMatchingDraft(
                    draftDirectory, out draftId, out draftName,
                    out createdOrdinal)) return null;
            return new DirectDraftIdentity {
                Directory = Path.GetFullPath(draftDirectory),
                DraftId = draftId,
                DisplayName = draftName,
                CreatedOrdinal = createdOrdinal
            };
        }

        private DirectDraftIdentity ReadDirectDraftIdentityOrTaskIndex(
            string draftDirectory)
        {
            DirectDraftIdentity identity = ReadDirectDraftIdentity(
                draftDirectory, false);
            if (identity != null &&
                !String.IsNullOrWhiteSpace(identity.DraftId) &&
                !String.IsNullOrWhiteSpace(identity.DisplayName))
                return identity;
            identity = ReadDirectDraftIdentityFromTaskIndex(draftDirectory);
            if (identity != null) {
                logger.Info("CapCut 9.2 draft metadata is empty; restored the exact checkpoint identity from the isolated task index: draft=" +
                    identity.DisplayName + ", draftId=" + identity.DraftId);
            }
            return identity;
        }

        private sealed class DirectDraftIdentity
        {
            public string Directory;
            public string DraftId;
            public string DisplayName;
            public long CreatedOrdinal;
        }

        private sealed class DraftActivationStamp
        {
            public DateTime DirectoryWriteUtc;
            public DateTime MetaWriteUtc;
            public DateTime ContentWriteUtc;
            public DateTime LockWriteUtc;
            public DateTime BizConfigWriteUtc;
            public DateTime SettingsWriteUtc;
            public DateTime TimelineLayoutWriteUtc;
            public DateTime TimelineProjectWriteUtc;

            public IEnumerable<DateTime> Values
            {
                get {
                    return new[] {
                        DirectoryWriteUtc,
                        MetaWriteUtc,
                        ContentWriteUtc,
                        LockWriteUtc,
                        BizConfigWriteUtc,
                        SettingsWriteUtc,
                        TimelineLayoutWriteUtc,
                        TimelineProjectWriteUtc
                    };
                }
            }
        }

        private static string FindJsonString(object value, string[] keys)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null) {
                foreach (string key in keys) {
                    object raw;
                    if (dictionary.TryGetValue(key, out raw) && raw is string && !String.IsNullOrWhiteSpace((string)raw))
                        return (string)raw;
                }
                foreach (object child in dictionary.Values) {
                    string found = FindJsonString(child, keys);
                    if (!String.IsNullOrWhiteSpace(found)) return found;
                }
            }
            var array = value as object[];
            if (array != null) {
                foreach (object child in array) {
                    string found = FindJsonString(child, keys);
                    if (!String.IsNullOrWhiteSpace(found)) return found;
                }
            }
            return "";
        }

        private void RemoveDraftIndexEntries(IEnumerable<string> directories)
        {
            string indexPath = draftIndexPath;
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("CapCut 草稿索引不存在", indexPath);
            var targets = new HashSet<string>(directories.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 };
            var root = serializer.DeserializeObject(File.ReadAllText(indexPath, Encoding.UTF8)) as Dictionary<string, object>;
            object rawStores;
            if (root == null || !root.TryGetValue("all_draft_store", out rawStores))
                throw new InvalidDataException("CapCut 草稿索引缺少 all_draft_store");
            object[] stores = rawStores as object[];
            if (stores == null) throw new InvalidDataException("CapCut 草稿索引格式异常");
            int matched = stores.Count(item => {
                var record = item as Dictionary<string, object>;
                object rawPath;
                return record != null && record.TryGetValue("draft_fold_path", out rawPath) &&
                       targets.Contains(NormalizePath(Convert.ToString(rawPath)));
            });
            if (matched != targets.Count) {
                throw new InvalidOperationException(String.Format(
                    "草稿索引只精确匹配到 {0}/{1} 条目标记录，已停止删除", matched, targets.Count));
            }
            root["all_draft_store"] = stores.Where(item => {
                var record = item as Dictionary<string, object>;
                object rawPath;
                return record == null || !record.TryGetValue("draft_fold_path", out rawPath) ||
                       !targets.Contains(NormalizePath(Convert.ToString(rawPath)));
            }).ToArray();
            string tempPath = indexPath + ".capcut-batch.tmp";
            // Flush before replacing: a truncated root index makes the whole
            // task draft root unusable (2026-10-11, root_meta_info.json = 0 B).
            DurableFile.WriteAllText(tempPath, serializer.Serialize(root), new UTF8Encoding(false));
            File.Copy(tempPath, indexPath, true);
            File.Delete(tempPath);
            logger.Info(String.Format("已从 CapCut 草稿索引精确移除 {0} 条本轮记录", matched));
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\'); }
            catch { return path.Replace('/', '\\').TrimEnd('\\'); }
        }

        private Process FindRunningProcess()
        {
            foreach (string name in config.ProcessNames) {
                string processName = Path.GetFileNameWithoutExtension(name);
                Process candidate = Process.GetProcessesByName(processName).OrderByDescending(p => p.MainWindowHandle != IntPtr.Zero).FirstOrDefault();
                if (candidate != null) return candidate;
            }
            return null;
        }

        private string ResolveExecutable()
        {
            if (!String.IsNullOrWhiteSpace(config.CapCutPath)) {
                string configured = Environment.ExpandEnvironmentVariables(config.CapCutPath);
                // Respect the exact configured executable. The parent Apps
                // launcher can redirect to an unvalidated auto-updated build.
                return Path.GetFullPath(configured);
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = {
                Path.Combine(local, "JianyingPro", "Apps", "JianyingPro.exe"),
                Path.Combine(local, "CapCut", "Apps", "CapCut.exe"),
                Path.Combine(local, "Programs", "CapCut", "CapCut.exe")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        private sealed class FileDialogProbeResult
        {
            public bool Success;
            public bool FieldVerified;
            public bool PathSubmitted;
            public string ErrorCode;
            public string Diagnostic;
        }

        private sealed class FileDialogProbePayload
        {
            public bool success { get; set; }
            public bool fieldVerified { get; set; }
            public bool pathSubmitted { get; set; }
            public string errorCode { get; set; }
            public string diagnostic { get; set; }
        }

        private sealed class MediaDialogWaitResult
        {
            public MediaDialogObservation Accepted;
            public List<MediaDialogObservation> Observations =
                new List<MediaDialogObservation>();

            public string Diagnostic
            {
                get
                {
                    if (Observations.Count == 0) return "no native dialog candidates";
                    return String.Join(" | ", Observations.Select(DescribeMediaDialog));
                }
            }
        }

        // Native-only bounded discovery.  This deliberately never constructs
        // an AutomationElement, so a busy Chromium/QML window cannot freeze the
        // import worker before Ctrl+I is sent.
        private HashSet<long> CaptureNativeWindowBaseline()
        {
            return new HashSet<long>(NativeMethods.GetTopLevelWindowHandles()
                .Select(handle => handle.ToInt64()));
        }

        private bool WaitForNativeDialogClosed(IntPtr dialog,
            int timeoutSeconds, CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(
                Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                if (!NativeMethods.IsWindow(dialog) ||
                    !NativeMethods.IsWindowVisible(dialog)) return true;
                SleepChecked(100, token);
            }
            return !NativeMethods.IsWindow(dialog) ||
                !NativeMethods.IsWindowVisible(dialog);
        }

        private MediaDialogWaitResult WaitForNativeMediaDialog(int timeoutSeconds,
            CancellationToken token, HashSet<long> baseline, Stopwatch commandWatch)
        {
            Stopwatch watch = Stopwatch.StartNew();
            var latest = new Dictionary<long, MediaDialogObservation>();
            while (watch.Elapsed < TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))) {
                token.ThrowIfCancellationRequested();
                foreach (IntPtr handle in NativeMethods.GetTopLevelWindowHandles()) {
                    MediaDialogObservation observation = ObserveNativeMediaDialog(
                        handle, baseline, commandWatch);
                    if (observation == null) continue;
                    latest[observation.Handle] = observation;
                    if (observation.Accepted) {
                        var accepted = new MediaDialogWaitResult {
                            Accepted = observation,
                            Observations = latest.Values.ToList()
                        };
                        logger.Info("文件选择器候选已接受: " +
                            DescribeMediaDialog(observation));
                        return accepted;
                    }
                }
                SleepChecked(100, token);
            }
            var result = new MediaDialogWaitResult {
                Observations = latest.Values.ToList()
            };
            logger.Warn("文件选择器候选扫描结束: " + result.Diagnostic);
            return result;
        }

        private MediaDialogObservation ObserveNativeMediaDialog(IntPtr handle,
            HashSet<long> baseline, Stopwatch commandWatch)
        {
            if (handle == IntPtr.Zero) return null;
            string title = NativeMethods.GetWindowTitle(handle);
            string className = NativeMethods.GetWindowClassName(handle);
            bool nativeClass = CapCutImportDialogPolicy.IsNativeDialogClass(className);
            bool foreground = handle == NativeMethods.GetForegroundWindow();
            if (!nativeClass && !foreground) return null;
            bool visible = NativeMethods.IsWindowVisible(handle);
            bool responsive = !NativeMethods.IsHungAppWindow(handle);
            uint pid;
            NativeMethods.GetWindowThreadProcessId(handle, out pid);
            bool ownedSession = false;
            bool directProcess = false;
            try {
                Process candidate = Process.GetProcessById((int)pid);
                ownedSession = IsOwnedSessionProcess(candidate);
                directProcess = process != null && candidate.Id == process.Id;
                candidate.Dispose();
            } catch { }
            IntPtr editor = GetLockedWindowHandle();
            IntPtr owner = NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER);
            IntPtr rootOwner = NativeMethods.GetAncestor(handle,
                NativeMethods.GA_ROOTOWNER);
            bool ownerMatch = editor != IntPtr.Zero &&
                (owner == editor || rootOwner == editor);
            bool isNew = baseline != null && !baseline.Contains(handle.ToInt64());
            bool newForeground = isNew && foreground;
            bool accepted = CapCutImportDialogPolicy.IsCandidate(title,
                className, visible, responsive, ownedSession,
                directProcess, ownerMatch, newForeground);
            string reason = accepted ? "accepted" : MediaDialogRejectionReason(
                title, className, visible, responsive, ownedSession,
                directProcess, ownerMatch, isNew, foreground);
            return new MediaDialogObservation {
                Handle = handle.ToInt64(),
                ProcessId = (int)pid,
                Title = title ?? "",
                ClassName = className ?? "",
                OwnerHandle = owner.ToInt64(),
                RootOwnerHandle = rootOwner.ToInt64(),
                Visible = visible,
                Responsive = responsive,
                Foreground = foreground,
                NewSinceCommand = isNew,
                DirectProcessMatch = directProcess,
                OwnedSessionProcess = ownedSession,
                OwnerChainMatchesEditor = ownerMatch,
                NativeDialogClass = nativeClass,
                BlockedTitle = CapCutImportDialogPolicy.IsBlockedTitle(title),
                Accepted = accepted,
                RejectionReason = reason,
                ObservedAfterMilliseconds = commandWatch == null
                    ? 0 : commandWatch.ElapsedMilliseconds
            };
        }

        private static string MediaDialogRejectionReason(string title,
            string className, bool visible, bool responsive,
            bool ownedSession, bool directProcess, bool ownerMatch,
            bool isNew, bool foreground)
        {
            if (!visible) return "hidden";
            if (!responsive) return "unresponsive";
            if (CapCutImportDialogPolicy.IsBlockedTitle(title)) return "blocked_title";
            if (!CapCutImportDialogPolicy.IsNativeDialogClass(className)) return "class_not_file_dialog";
            if (!ownedSession && !directProcess && !ownerMatch) return "no_verified_editor_relationship";
            if (!CapCutImportDialogPolicy.IsMediaPickerTitle(title) &&
                !(ownerMatch && isNew && foreground && String.IsNullOrWhiteSpace(title)))
                return "title_or_command_relationship_unconfirmed";
            return "policy_rejected";
        }

        private static string DescribeMediaDialog(MediaDialogObservation item)
        {
            if (item == null) return "<none>";
            return String.Format(CultureInfo.InvariantCulture,
                "hwnd={0},pid={1},class={2},title={3},owner={4},rootOwner={5},foreground={6},new={7},session={8},direct={9},ownerMatch={10},result={11}",
                item.Handle, item.ProcessId, item.ClassName ?? "",
                String.IsNullOrWhiteSpace(item.Title) ? "<empty>" : item.Title,
                item.OwnerHandle, item.RootOwnerHandle, item.Foreground,
                item.NewSinceCommand, item.OwnedSessionProcess,
                item.DirectProcessMatch, item.OwnerChainMatchesEditor,
                item.Accepted ? "accepted" : item.RejectionReason);
        }

        private FileDialogProbeResult RunBoundedFileDialogProbe(IntPtr dialog,
            string mediaPath, CancellationToken token, bool inspectOnly)
        {
            // Inspection remains deliberately short: it is only an optional
            // diagnostic gate.  Submission gets a larger bounded budget
            // because shell/UIA providers can take several seconds to expose
            // the filename field even after Win32 has identified the dialog.
            return RunBoundedFileDialogProbe(dialog, mediaPath, token, inspectOnly,
                inspectOnly ? 2000 : 8000);
        }

        private FileDialogProbeResult RunBoundedFileDialogProbe(IntPtr dialog,
            string mediaPath, CancellationToken token, bool inspectOnly, int timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            string helper = Path.Combine(AppPaths.Root, "tools", "CapCutFileDialogProbe.exe");
            if (!File.Exists(helper))
                return new FileDialogProbeResult { ErrorCode = "CAPCUT_FILE_DIALOG_FILENAME_FIELD_MISSING",
                    Diagnostic = "缺少独立文件选择器探测器: " + helper };
            try {
                uint dialogPid;
                NativeMethods.GetWindowThreadProcessId(dialog, out dialogPid);
                var info = new ProcessStartInfo {
                    FileName = helper,
                    Arguments = (inspectOnly ? "--inspect " : "--import ") + dialog.ToInt64().ToString(CultureInfo.InvariantCulture) +
                        " " + dialogPid.ToString(CultureInfo.InvariantCulture) +
                        (inspectOnly ? "" : " " + QuoteProcessArgument(mediaPath)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process probe = Process.Start(info)) {
                    if (probe == null)
                        return new FileDialogProbeResult { ErrorCode = "CAPCUT_FILE_DIALOG_PROBE_TIMEOUT", Diagnostic = "无法启动独立探测器" };
                    if (!probe.WaitForExit(Math.Max(1000, timeoutMs))) {
                        try { probe.Kill(); } catch { }
                        string operation = inspectOnly ? "检查" : "提交";
                        string budget = inspectOnly ? "2秒" : "8秒";
                        return new FileDialogProbeResult {
                            ErrorCode = inspectOnly ? "CAPCUT_FILE_DIALOG_PROBE_TIMEOUT" : "CAPCUT_IMPORT_COMMAND_UNCONFIRMED",
                            Diagnostic = "文件选择器 UIA " + operation + "超过" + budget +
                                "，已终止探测进程；提交结果待草稿核对" };
                    }
                    string output = probe.StandardOutput.ReadToEnd();
                    string error = probe.StandardError.ReadToEnd();
                    FileDialogProbePayload payload = null;
                    try { payload = new JavaScriptSerializer().Deserialize<FileDialogProbePayload>(output); }
                    catch { }
                    if (probe.ExitCode == 0 && payload != null && payload.success &&
                        payload.fieldVerified && (inspectOnly || payload.pathSubmitted))
                        return new FileDialogProbeResult { Success = true,
                            FieldVerified = true, PathSubmitted = payload.pathSubmitted,
                            ErrorCode = "", Diagnostic = payload.diagnostic ?? "path submitted" };
                    return new FileDialogProbeResult {
                        FieldVerified = payload != null && payload.fieldVerified,
                        PathSubmitted = payload != null && payload.pathSubmitted,
                        ErrorCode = payload != null &&
                            String.Equals(payload.errorCode, "FIELD_MISSING", StringComparison.OrdinalIgnoreCase)
                            ? "CAPCUT_FILE_DIALOG_FILENAME_FIELD_MISSING" : "CAPCUT_FILE_DIALOG_IDENTITY_MISMATCH",
                        Diagnostic = payload == null ? (output + " " + error).Trim()
                            : (payload.diagnostic ?? payload.errorCode ?? "probe failed")
                    };
                }
            } catch (Exception ex) {
                return new FileDialogProbeResult { ErrorCode = "CAPCUT_FILE_DIALOG_PROBE_TIMEOUT", Diagnostic = ex.Message };
            }
        }

        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\\\"", "\\\\\"") + "\"";
        }

        private bool IsOwnedRenameDialogForeground()
        {
            AutomationElement foreground = locator.ElementFromWindow(NativeMethods.GetForegroundWindow());
            if (foreground == null) return false;
            string title = ControlLocator.SafeName(foreground);
            if (title.IndexOf("Rename", StringComparison.OrdinalIgnoreCase) < 0 &&
                title.IndexOf("重命名", StringComparison.OrdinalIgnoreCase) < 0) return false;

            try {
                return mainWindow != null &&
                    foreground.Current.ProcessId == mainWindow.Current.ProcessId;
            } catch { return false; }
        }

        private bool IsNativeRenameDialogForeground()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            string title = NativeMethods.GetWindowTitle(foreground);
            if (title.IndexOf("Rename", StringComparison.OrdinalIgnoreCase) < 0 &&
                title.IndexOf("重命名", StringComparison.OrdinalIgnoreCase) < 0) return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(foreground, out pid);
            try {
                Process candidate = Process.GetProcessById((int)pid);
                bool owned = IsOwnedSessionProcess(candidate) ||
                    (process != null && candidate.Id == process.Id);
                candidate.Dispose();
                return owned;
            } catch { return false; }
        }

        private bool TryDismissOwnedRenameDialog()
        {
            if (!IsOwnedRenameDialogForeground()) return false;
            NativeMethods.PressEscape();
            return true;
        }

        private string WaitForStableOutput(
            IEnumerable<string> candidates,
            int timeoutSeconds,
            DateTime exportStartedUtc,
            CancellationToken token)
        {
            string[] paths = candidates.Where(path => !String.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var previousSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var stableSamples = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                foreach (string path in paths) {
                    if (!File.Exists(path)) continue;
                    FileInfo file;
                    try { file = new FileInfo(path); } catch { continue; }
                    // A stale file with the same name must never be accepted as this task's export.
                    if (file.LastWriteTimeUtc < exportStartedUtc.AddSeconds(-2) || file.Length <= 0) continue;
                    long previous;
                    previousSizes.TryGetValue(path, out previous);
                    int samples;
                    stableSamples.TryGetValue(path, out samples);
                    samples = file.Length == previous ? samples + 1 : 0;
                    previousSizes[path] = file.Length;
                    stableSamples[path] = samples;
                    if (samples >= 3) {
                        logger.Info("检测到稳定的 CapCut 导出文件: " + path);
                        return path;
                    }
                }
                Thread.Sleep(1000);
            }
            throw new TimeoutException("等待 CapCut 导出文件完成超时: " + String.Join(" | ", paths));
        }

        private void WaitForStableFile(
            string path,
            int timeoutSeconds,
            bool existedBeforeExport,
            long lengthBeforeExport,
            DateTime writeTimeBeforeExportUtc,
            CancellationToken token)
        {
            Stopwatch watch = Stopwatch.StartNew();
            long previousSize = -1;
            int stableSamples = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path)) {
                    long size = 0;
                    DateTime writeTimeUtc = DateTime.MinValue;
                    try {
                        FileInfo current = new FileInfo(path);
                        size = current.Length;
                        writeTimeUtc = current.LastWriteTimeUtc;
                    } catch { }
                    bool changedByThisExport = !existedBeforeExport ||
                        size != lengthBeforeExport ||
                        writeTimeUtc > writeTimeBeforeExportUtc;
                    if (!changedByThisExport) {
                        stableSamples = 0;
                        previousSize = size;
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (size > 0 && size == previousSize) stableSamples++; else stableSamples = 0;
                    previousSize = size;
                    if (stableSamples >= 3) return;
                }
                Thread.Sleep(1000);
            }
            throw new TimeoutException("等待导出文件完成超时: " + path);
        }

        private void RefreshWindow()
        {
            IntPtr lockedHandle = GetLockedWindowHandle();
            if (lockedHandle != IntPtr.Zero) {
                AutomationElement lockedWindow = TryGetWindowElementBounded(lockedHandle, 1500);
                if (lockedWindow != null) {
                    mainWindow = lockedWindow;
                    return;
                }
            }
            AutomationElement largest = FindOwnedCapCutWindowNative();
            if (largest != null) {
                mainWindow = largest;
                return;
            }
            try {
                if (process != null) {
                    process.Refresh();
                    AutomationElement refreshed = locator.ElementFromWindow(process.MainWindowHandle);
                    if (refreshed != null) mainWindow = refreshed;
                }
            } catch { }
            if (mainWindow == null) mainWindow = FindOwnedCapCutWindowNative();
            if (mainWindow == null) throw new InvalidOperationException("剪映主窗口不可用");
        }

        private bool IsConfiguredCapCutProcess(AutomationElement element)
        {
            try {
                using (Process candidate = Process.GetProcessById(element.Current.ProcessId))
                    return IsConfiguredCapCutProcessName(candidate.ProcessName);
            } catch { return false; }
        }

        private bool IsConfiguredCapCutProcess(int processId)
        {
            if (processId <= 0) return false;
            try {
                using (Process candidate = Process.GetProcessById(processId))
                    return IsConfiguredCapCutProcessName(candidate.ProcessName);
            } catch { return false; }
        }

        private bool IsConfiguredCapCutProcessName(string processName)
        {
            return (config.ProcessNames ?? new string[0]).Any(name => String.Equals(
                Path.GetFileNameWithoutExtension(name), processName,
                StringComparison.OrdinalIgnoreCase));
        }

        private bool IsOwnedSessionProcess(int processId)
        {
            if (processId <= 0) return false;
            if (ownedSessionProcessIds.Contains(processId)) return true;
            try {
                using (Process candidate = Process.GetProcessById(processId))
                    return IsOwnedSessionProcess(candidate);
            } catch { return false; }
        }

        // CapCut 9.2 starts a small launcher tree.  On some launches the
        // visible QML window is created by a child whose reported process
        // start time is a few seconds before the Process.Start() handle that
        // the client received.  Treating that child as foreign made a valid
        // launch look like "main window timeout" even though the window was
        // already on screen.  This fallback is limited to a fresh launch,
        // excludes the pre-launch process baseline, and requires a recent
        // configured CapCut process; it cannot adopt an older user window.
        private bool IsFreshLaunchProcessCandidate(int processId)
        {
            if (!sessionWasFreshlyLaunched || processId <= 0 ||
                sessionStartedUtc == DateTime.MinValue ||
                preLaunchSessionProcessIds.Contains(processId)) return false;
            try {
                using (Process candidate = Process.GetProcessById(processId)) {
                    if (!IsConfiguredCapCutProcessName(candidate.ProcessName))
                        return false;
                    DateTime started = candidate.StartTime.ToUniversalTime();
                    return started >= sessionStartedUtc.AddSeconds(-30);
                }
            } catch { return false; }
        }

        private AutomationElement FindOwnedCapCutWindowNative()
        {
            var candidates = new List<Tuple<IntPtr, string, string, int>>();
            foreach (IntPtr handle in NativeMethods.GetTopLevelWindowHandles()) {
                try {
                    uint nativePid;
                    NativeMethods.GetWindowThreadProcessId(handle, out nativePid);
                    if (nativePid == 0 || !IsConfiguredCapCutProcess((int)nativePid))
                        continue;
                    if (!IsOwnedSessionProcess((int)nativePid) &&
                        (process == null || process.Id != (int)nativePid) &&
                        !IsFreshLaunchProcessCandidate((int)nativePid)) continue;
                    if (NativeMethods.IsHungAppWindow(handle)) continue;
                    NativeMethods.RECT rect;
                    if (!NativeMethods.GetWindowRect(handle, out rect) ||
                        rect.Right - rect.Left < 640 || rect.Bottom - rect.Top < 400)
                        continue;
                    string className = NativeMethods.GetWindowClassName(handle);
                    string title = NativeMethods.GetWindowTitle(handle);
                    bool titleMatch = (config.MainWindowNames ?? new string[0]).Any(name =>
                        !String.IsNullOrWhiteSpace(name) &&
                        title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                    CapCutWindowRole role = CapCutHomeShellPolicy.Classify(className);
                    if (!titleMatch && role == CapCutWindowRole.Unknown &&
                        String.IsNullOrWhiteSpace(title)) continue;
                    int priority = role == CapCutWindowRole.Editor ? 3 :
                        role == CapCutWindowRole.Transition ? 1 : 2;
                    candidates.Add(Tuple.Create(handle, className, title, priority));
                } catch { }
            }
            foreach (Tuple<IntPtr, string, string, int> item in candidates
                .OrderByDescending(value => value.Item4)) {
                AutomationElement element = TryGetWindowElementBounded(item.Item1, 1500);
                if (element != null) return element;
            }
            return null;
        }

        private void DismissVerifiedHomePromotion(CancellationToken token)
        {
            IntPtr home = directDraftHomeHandle;
            IntPtr popup = NativeMethods.GetForegroundWindow();
            if (home == IntPtr.Zero || popup == IntPtr.Zero || popup == home ||
                !NativeMethods.BelongsToSameProcess(home, popup) || !IsCapCutHomeShell(home)) return;
            string kind = NativeMethods.GetWindowClassName(popup);
            if (!kind.StartsWith("SplashDialogV2_QMLTYPE_", StringComparison.Ordinal)) return;
            NativeMethods.RECT a, b;
            if (!NativeMethods.GetWindowRect(popup, out a) || !NativeMethods.GetWindowRect(home, out b)) return;
            bool inside = a.Left >= b.Left && a.Top >= b.Top && a.Right <= b.Right && a.Bottom <= b.Bottom;
            double area = Math.Max(0, a.Right-a.Left) * Math.Max(0, a.Bottom-a.Top);
            double homeArea = Math.Max(0, b.Right-b.Left) * Math.Max(0, b.Bottom-b.Top);
            string filename = "capcut-home-promotion-" + Guid.NewGuid().ToString("N") + ".png";
            CaptureWindowHandle(popup, filename);
            string marker, diagnostic;
            bool read = VisibleTextOcr.TryContainsAny(Path.Combine(AppPaths.LogDirectory, filename),
                new[] { "开通会员畅享" }, out marker, out diagnostic, 2000);
            token.ThrowIfCancellationRequested();
            if (!read || !CapCutImportDialogPolicy.CanDismissHomePromotion(true,
                !NativeMethods.IsHungAppWindow(popup), NativeMethods.GetWindowTitle(popup),
                kind, inside, area, homeArea, diagnostic)) return;
            if (NativeMethods.GetForegroundWindow() != popup ||
                !NativeMethods.BelongsToSameProcess(home, popup) ||
                NativeMethods.GetWindowClassName(popup) != kind) return;
            // Close the positively identified offer window, never its trial/purchase button.
            NativeMethods.PostMessage(popup, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!WaitForNativeDialogClosed(popup, 2, token))
                throw new RecoverableDraftReopenException("home_promotion_close_unconfirmed",
                    "已识别首页会员推广但未确认关闭；未点击草稿或购买按钮");
            NativeMethods.ActivateWindow(home);
            logger.Info("已关闭身份与文本均确认的首页会员推广，继续单次草稿打开");
        }

        private bool CloseForegroundCapCutPopup()
        {
            try {
                IntPtr mainHandle = GetLockedWindowHandle();
                IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
                if (foregroundHandle == IntPtr.Zero || foregroundHandle == mainHandle) return false;
                uint processId;
                NativeMethods.GetWindowThreadProcessId(foregroundHandle, out processId);
                Process foregroundProcess = Process.GetProcessById((int)processId);
                bool owned = IsOwnedSessionProcess(foregroundProcess) ||
                    (process != null && foregroundProcess.Id == process.Id);
                if (!owned) return false;
                string title = NativeMethods.GetWindowTitle(foregroundHandle);
                string className = NativeMethods.GetWindowClassName(foregroundHandle);
                // Never close a native file picker, rename dialog, or a hard
                // error here.  This function is only a bounded native popup
                // cleanup and must not invoke UIA on the busy editor.
                if (className == "#32770" || title.IndexOf("重命名", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("rename", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("错误", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("打开", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                NativeMethods.RECT popupRect;
                NativeMethods.RECT mainRect;
                if (!NativeMethods.GetWindowRect(foregroundHandle, out popupRect) ||
                    !NativeMethods.GetWindowRect(mainHandle, out mainRect)) return false;
                double popupArea = Math.Max(0, popupRect.Right - popupRect.Left) *
                    Math.Max(0, popupRect.Bottom - popupRect.Top);
                double mainArea = Math.Max(0, mainRect.Right - mainRect.Left) *
                    Math.Max(0, mainRect.Bottom - mainRect.Top);
                if (popupArea < 20000 || popupArea >= mainArea * 0.9) return false;
                // A Qt tutorial has no title-bar close button. The previous
                // top-right coordinate hit its content and left it foreground.
                // Never dismiss an unidentified same-process popup.
                IntPtr editor = GetBoundEditorHandleRaw();
                bool inside = popupRect.Left >= mainRect.Left && popupRect.Top >= mainRect.Top &&
                    popupRect.Right <= mainRect.Right && popupRect.Bottom <= mainRect.Bottom;
                if (editor != mainHandle || !CapCutImportDialogPolicy.CanProbeEditorHint(
                        editor.ToInt64(), foregroundHandle.ToInt64(), owned,
                        !NativeMethods.IsHungAppWindow(foregroundHandle), title, className,
                        popupArea, mainArea, inside)) return false;
                string filename = "capcut-editor-hint-" + Guid.NewGuid().ToString("N") + ".png";
                CaptureWindowHandle(foregroundHandle, filename);
                string marker, diagnostic;
                if (!VisibleTextOcr.TryContainsAny(Path.Combine(AppPaths.LogDirectory, filename),
                        new[] { "Edit Pilot", "开启", "关闭" }, out marker, out diagnostic, 2000) ||
                    !CapCutImportDialogPolicy.CanDismissEditorHint(
                        editor.ToInt64(), foregroundHandle.ToInt64(), owned,
                        !NativeMethods.IsHungAppWindow(foregroundHandle), title, className,
                        popupArea, mainArea, inside, diagnostic)) return false;
                // Recheck ownership/foreground after the asynchronous OCR probe.
                if (NativeMethods.GetForegroundWindow() != foregroundHandle ||
                    GetBoundEditorHandleRaw() != editor ||
                    !NativeMethods.BelongsToSameProcess(editor, foregroundHandle)) return false;
                if (!NativeMethods.PostMessage(foregroundHandle, NativeMethods.WM_CLOSE,
                        IntPtr.Zero, IntPtr.Zero)) return false;
                logger.Info("已向经OCR核实的 Edit Pilot 引导窗发送关闭；导入前仍须重新核对编辑器前台");
                return true;
            } catch (Exception ex) {
                logger.Warn("关闭 CapCut 前台弹窗失败: " + ex.Message);
                return false;
            }
        }

        internal static bool IsEnvironmentCheckTitle(string title)
        {
            string value = (title ?? "").Trim();
            return String.Equals(value, "环境检测", StringComparison.Ordinal) ||
                String.Equals(value, "Environment check", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "Environment detection", StringComparison.OrdinalIgnoreCase);
        }

        // CapCut can open a separate "环境检测 / 您的电脑可以流畅使用CapCut"
        // window centred on screen.  On 2026-10-11 19:47 it covered the
        // caption progress dialog, so the OCR check missed a recognition that
        // was already at 45% and stopped the batch.  It is an informational
        // window of this session's own CapCut process; closing it is the same
        // as its own close button and changes nothing in the draft.
        private bool DismissEnvironmentCheckWindow()
        {
            bool closed = false;
            try {
                foreach (IntPtr handle in NativeMethods.GetTopLevelWindowHandles()) {
                    if (!IsEnvironmentCheckTitle(NativeMethods.GetWindowTitle(handle))) continue;
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(handle, out processId);
                    if (processId == 0 || !(IsOwnedSessionProcess((int)processId) ||
                        IsCapCutInstallProcess((int)processId))) continue;
                    NativeMethods.PostMessage(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Stopwatch watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(2) &&
                        NativeMethods.GetTopLevelWindowHandles().Contains(handle)) {
                        Thread.Sleep(100);
                    }
                    logger.Info("已关闭 CapCut 的环境检测提示窗口；未处理其他弹窗");
                    closed = true;
                }
            } catch (Exception ex) {
                logger.Warn("关闭 CapCut 环境检测提示失败，继续由验证逻辑安全停止: " + ex.Message);
            }
            return closed;
        }

        // The detector may run in a CapCut helper with another process name.
        // Accept it only when its executable lives in the configured CapCut
        // installation folder.
        private bool IsCapCutInstallProcess(int processId)
        {
            try {
                string capcut = String.IsNullOrWhiteSpace(config.CapCutPath) ? "" : Path.GetFullPath(config.CapCutPath);
                string appsRoot = String.IsNullOrWhiteSpace(capcut) ? "" :
                    Path.GetDirectoryName(Path.GetDirectoryName(capcut));
                if (String.IsNullOrWhiteSpace(appsRoot)) return false;
                using (Process candidate = Process.GetProcessById(processId)) {
                    string image = Path.GetFullPath(candidate.MainModule.FileName);
                    return image.StartsWith(appsRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                }
            } catch { return false; }
        }

        private void DismissKnownInlineNotice(CancellationToken token, bool allowVisualFallback)
        {
            DismissEnvironmentCheckWindow();
            if (mainWindow == null) return;
            try {
                string draftPathError;
                if (TryFindCapCutDraftPathError(out draftPathError)) {
                    logger.Warn("检测到 CapCut 草稿路径错误弹窗；不执行通用确认按钮点击：" + draftPathError);
                    return;
                }
                IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
                IntPtr lockedHandle = GetLockedWindowHandle();
                LocatorRule acknowledgement = new LocatorRule {
                    Names = new List<string> { "我知道了", "I understand" }
                };
                // Never enumerate the child tree of the busy editor itself.
                // CapCut's Chromium/QML root can block UIA for a minute while
                // media is loading.  A semantic acknowledgement lookup is
                // limited to a separate foreground popup; in-editor overlays
                // are handled by the bounded screenshot probe below.
                AutomationElement button = null;
                if (foregroundHandle != IntPtr.Zero && foregroundHandle != lockedHandle) {
                    AutomationElement foregroundPopup = locator.ElementFromWindow(foregroundHandle);
                    button = FindShallowInlineAcknowledgement(foregroundPopup,
                        acknowledgement.Names);
                }
                if (button != null && IsHdrAcknowledgement(button)) {
                    token.ThrowIfCancellationRequested();
                    ControlLocator.ClickElement(button, 1);
                    logger.Info("已关闭 CapCut 的 HDR 设备提示；未处理其他弹窗");
                    return;
                }
                if (allowVisualFallback && TryDismissCenteredHdrAcknowledgement(token)) {
                    logger.Info("已通过中心确认按钮外观关闭 CapCut 的 HDR 设备提示；未处理其他弹窗");
                }
            } catch (Exception ex) {
                logger.Warn("关闭已知 HDR 提示失败，继续由验证逻辑安全停止: " + ex.Message);
            }
        }

        private AutomationElement FindShallowInlineAcknowledgement(
            AutomationElement root, IEnumerable<string> names)
        {
            if (root == null) return null;
            string[] targets = (names ?? new string[0]).Where(value =>
                !String.IsNullOrWhiteSpace(value)).ToArray();
            if (targets.Length == 0) return null;
            try {
                AutomationElementCollection children = root.FindAll(
                    TreeScope.Children, Condition.TrueCondition);
                int count = 0;
                foreach (AutomationElement child in children) {
                    if (++count > 128) break;
                    AutomationElement match = FindNamedControl(child, targets);
                    if (match != null) return match;
                    bool dialogLike = false;
                    try {
                        string className = child.Current.ClassName ?? "";
                        string type = child.Current.ControlType == null
                            ? "" : child.Current.ControlType.ProgrammaticName ?? "";
                        dialogLike = className.IndexOf("#32770", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            type.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0;
                    } catch { }
                    if (!dialogLike) continue;
                    AutomationElementCollection grandchildren;
                    try { grandchildren = child.FindAll(TreeScope.Children, Condition.TrueCondition); }
                    catch { grandchildren = null; }
                    if (grandchildren == null) continue;
                    int nestedCount = 0;
                    foreach (AutomationElement grandchild in grandchildren) {
                        if (++nestedCount > 64) break;
                        match = FindNamedControl(grandchild, targets);
                        if (match != null) return match;
                    }
                }
            } catch { }
            return null;
        }

        private AutomationElement FindNamedControl(AutomationElement element,
            IEnumerable<string> names)
        {
            if (element == null) return null;
            try {
                string value = ControlLocator.SafeName(element);
                if (!String.IsNullOrWhiteSpace(value) && names.Any(name =>
                    String.Equals(value, name, StringComparison.OrdinalIgnoreCase) ||
                    value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                    return element;
            } catch { }
            return null;
        }

        // Recent CapCut builds expose the HDR prompt as an in-editor layer with
        // no usable UI-Automation name.  Its acknowledgement is the only wide
        // cyan button in the centre of the editor, so look exclusively there;
        // this deliberately cannot click the top-right export/upload buttons.
        private bool TryDismissCenteredHdrAcknowledgement(CancellationToken token)
        {
            if (mainWindow == null) return false;
            string draftPathError;
            if (TryFindCapCutDraftPathError(out draftPathError)) return false;
            Rect raw = mainWindow.Current.BoundingRectangle;
            if (raw.Width < 800 || raw.Height < 600) return false;
            int width = (int)raw.Width;
            int height = (int)raw.Height;
            using (var bitmap = new Bitmap(width, height))
            using (Graphics graphics = Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen((int)raw.Left, (int)raw.Top, 0, 0, bitmap.Size);
                System.Drawing.Point acknowledgement;
                if (HdrNoticeVision.TryFindAcknowledgement(bitmap, out acknowledgement)) {
                    token.ThrowIfCancellationRequested();
                    NativeMethods.Click((int)raw.Left + acknowledgement.X,
                        (int)raw.Top + acknowledgement.Y, 1);
                    return true;
                }
            }
            return false;
        }

        private bool IsHdrAcknowledgement(AutomationElement button)
        {
            if (button == null) return false;
            try {
                AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(button);
                List<string> names = new List<string>();
                for (AutomationElement item = parent; item != null && names.Count < 12;
                    item = TreeWalker.ControlViewWalker.GetParent(item)) {
                    string name = ControlLocator.SafeName(item);
                    if (!String.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                string context = String.Join(" ", names.ToArray()).ToLowerInvariant();
                if (ContainsDraftPathFailureText(context)) return false;
                return context.Contains("hdr") || context.Contains("高动态") ||
                    context.Contains("设备") || context.Contains("色彩") ||
                    context.Contains("color space") || context.Contains("colour space") ||
                    context.Contains("high dynamic");
            } catch { return false; }
        }

        private void FocusMainWindow()
        {
            try {
                IntPtr handle = GetLockedWindowHandle();
                if (handle == IntPtr.Zero && process != null) {
                    process.Refresh();
                    handle = process.MainWindowHandle;
                }
                if (handle != IntPtr.Zero) {
                        bool maximized = NativeMethods.EnsureMaximized(handle);
                        if (!maximized)
                            logger.Warn("CapCut编辑器窗口未能确认最大化：Handle=" + handle);
                        bool activated = NativeMethods.ActivateWindow(handle);
                        logger.Info(String.Format("激活 CapCut 主窗口: Handle={0}, Result={1}", handle, activated));
                        Thread.Sleep(250);
                        // Remote-viewer chrome can briefly win foreground
                        // focus just after activation.  Retry the activation
                        // here so coordinate steps do not fail on a transient
                        // focus steal; FocusForCoordinateAction still blocks
                        // safely if another application keeps the foreground.
                        for (int retry = 1; retry <= 2; retry++) {
                            IntPtr foreground = NativeMethods.GetForegroundWindow();
                            if (foreground == handle || NativeMethods.BelongsToSameProcess(handle, foreground)) break;
                            Thread.Sleep(350);
                            bool retried = NativeMethods.ActivateWindow(handle);
                            logger.Info(String.Format("重新激活 CapCut 主窗口（{0}/2）: Handle={1}, Result={2}",
                                retry, handle, retried));
                            Thread.Sleep(350);
                        }
                }
            } catch { }
        }

        // CapCut's UI thread is often busy for several seconds right after a
        // cold start or a heavy timeline edit. Windows then reports the editor
        // as hung, GetLockedWindowHandle() skips it (falling back to a slow UIA
        // read), and Ctrl+I is only handled later. Imports failed on exactly
        // that window 8 times in Oct 2026 (CAPCUT_IMPORT_FOREGROUND_CONFLICT).
        private const int EditorBusyWaitSeconds = 45;
        private const int LatePickerWaitSeconds = 10;

        private IntPtr GetBoundEditorHandleRaw()
        {
            if (directDraftEditorHandle != IntPtr.Zero &&
                NativeMethods.IsWindowVisible(directDraftEditorHandle))
                return directDraftEditorHandle;
            if (boundEditorHandle != IntPtr.Zero &&
                NativeMethods.IsWindowVisible(boundEditorHandle))
                return boundEditorHandle;
            return IntPtr.Zero;
        }

        // Returns true once the bound editor answers again (immediately when it
        // is not busy). seconds = 0 only checks. Never sends input.
        private bool WaitForEditorResponsive(CancellationToken token, int seconds, string stage)
        {
            IntPtr editor = GetBoundEditorHandleRaw();
            if (editor == IntPtr.Zero || !NativeMethods.IsHungAppWindow(editor)) return true;
            if (seconds <= 0) return false;
            logger.Warn("CapCut 编辑器暂时无响应，等待恢复后再继续：" + stage);
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds)) {
                SleepChecked(500, token);
                if (!NativeMethods.IsHungAppWindow(editor)) {
                    logger.Info(String.Format("CapCut 编辑器已恢复响应（等待 {0:0.0}s）：{1}",
                        watch.Elapsed.TotalSeconds, stage));
                    SleepChecked(500, token);
                    return true;
                }
            }
            logger.Warn(String.Format("CapCut 编辑器 {0}s 内仍无响应：{1}", seconds, stage));
            return false;
        }

        // Before Ctrl+I the editor itself must be foreground. A same-process
        // popup or a busy editor gets up to three bounded settle attempts
        // (wait for the editor, close the popup, refocus). Nothing is typed.
        private bool SettleEditorForegroundBeforeShortcut(CancellationToken token, string visualAction,
            string purpose = "Ctrl+I")
        {
            for (int attempt = 1; attempt <= 3; attempt++) {
                WaitForEditorResponsive(token, EditorBusyWaitSeconds, purpose + " 前 " + visualAction);
                IntPtr editor = GetLockedWindowHandle();
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                if (editor != IntPtr.Zero && foreground == editor) {
                    if (attempt > 1)
                        logger.Info(String.Format("第 {0} 次整理后编辑器已在前台：{1}", attempt, visualAction));
                    return true;
                }
                logger.Warn(String.Format("{0} 前前台不是编辑器（第 {1}/3 次）：编辑器={2}；{3}",
                    purpose, attempt, editor, DescribeForegroundWindow()));
                if (attempt == 3) break;
                if (CloseForegroundCapCutPopup()) SleepChecked(700, token);
                RefreshWindow();
                try {
                    FocusForCoordinateAction(token);
                } catch (InvalidOperationException ex) {
                    logger.Warn("重新聚焦 CapCut 失败：" + ex.Message);
                }
                SleepChecked(500, token);
            }
            return false;
        }

        private static string DescribeForegroundWindow()
        {
            try {
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                uint pid;
                NativeMethods.GetWindowThreadProcessId(foreground, out pid);
                return String.Format("前台窗口={0}，类={1}，标题={2}，进程={3}，无响应={4}",
                    foreground, NativeMethods.GetWindowClassName(foreground),
                    NativeMethods.GetWindowTitle(foreground), pid,
                    NativeMethods.IsHungAppWindow(foreground));
            } catch (Exception ex) {
                return "前台窗口信息读取失败：" + ex.Message;
            }
        }

        private void FocusForCoordinateAction(CancellationToken token)
        {
            FocusMainWindow();
            SleepChecked(250, token);
            IntPtr expected = GetLockedWindowHandle();
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (expected == IntPtr.Zero ||
                (foreground != expected && !NativeMethods.BelongsToSameProcess(expected, foreground))) {
                throw new InvalidOperationException(String.Format("无法安全激活剪映，已阻止坐标操作。目标={0}，前台={1}", expected, foreground));
            }
            if (foreground != expected) {
                logger.Info(String.Format("已接受同一 CapCut 进程的前台子窗口: 主窗口={0}，前台={1}", expected, foreground));
            }
        }

        private IntPtr GetLockedWindowHandle()
        {
            try {
                // The native binding is the durable source of truth.  Reading
                // mainWindow.Current here can block on CapCut's QML provider
                // exactly while the editor is loading, which previously made
                // the client appear frozen and could reactivate the landing
                // shell instead of the editor.
                if (directDraftEditorHandle != IntPtr.Zero &&
                    NativeMethods.IsWindowVisible(directDraftEditorHandle) &&
                    !NativeMethods.IsHungAppWindow(directDraftEditorHandle))
                    return directDraftEditorHandle;
                if (boundEditorHandle != IntPtr.Zero &&
                    NativeMethods.IsWindowVisible(boundEditorHandle) &&
                    !NativeMethods.IsHungAppWindow(boundEditorHandle))
                    return boundEditorHandle;
                if (mainWindow != null && mainWindow.Current.NativeWindowHandle != 0) return new IntPtr(mainWindow.Current.NativeWindowHandle);
            } catch { }
            return IntPtr.Zero;
        }

        private void CaptureMainWindow(string fileName)
        {
            IntPtr handle = GetLockedWindowHandle();
            if (handle != IntPtr.Zero) {
                CaptureWindowHandle(handle, fileName);
                return;
            }
            try {
                if (mainWindow == null) {
                    logger.Warn("保存界面诊断截图跳过：当前没有有效CapCut窗口句柄");
                    return;
                }
                Rect rect = mainWindow.Current.BoundingRectangle;
                if (rect.Width < 10 || rect.Height < 10) return;
                using (var bitmap = new System.Drawing.Bitmap((int)rect.Width, (int)rect.Height))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen((int)rect.Left, (int)rect.Top, 0, 0, bitmap.Size);
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    string path = Path.Combine(AppPaths.LogDirectory, fileName);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    logger.Info("已保存界面诊断截图: " + path);
                }
            } catch (Exception ex) {
                logger.Warn("保存界面诊断截图失败: " + ex.Message);
            }
        }

        private void CaptureWindowHandle(IntPtr handle, string fileName)
        {
            try {
                NativeMethods.RECT nativeRect;
                if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out nativeRect)) {
                    logger.Warn("保存界面诊断截图跳过：无法读取窗口矩形");
                    return;
                }
                int width = nativeRect.Right - nativeRect.Left;
                int height = nativeRect.Bottom - nativeRect.Top;
                if (width < 10 || height < 10) return;
                using (var bitmap = new System.Drawing.Bitmap(width, height))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen(nativeRect.Left, nativeRect.Top, 0, 0, bitmap.Size);
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    string path = Path.Combine(AppPaths.LogDirectory, fileName);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    logger.Info("已保存界面诊断截图: " + path + " (handle=" + handle + ")");
                }
            } catch (Exception ex) {
                logger.Warn("保存窗口诊断截图失败: " + ex.Message);
            }
        }

        private string CaptureMainWindowForOcr(string fileName)
        {
            try {
                // Prefer the locked editor handle's Win32 rectangle, like the
                // diagnostic screenshots.  After a UIA probe timeout the UIA
                // bounding rectangle was stale (2026-10-12 01:27: a 1270x617
                // capture of the player that cut off the progress dialog).
                int left, top, width, height;
                NativeMethods.RECT nativeRect;
                // A busy editor can report "hung" while recognising captions;
                // it is still on screen, so capture it anyway.
                IntPtr handle =
                    directDraftEditorHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(directDraftEditorHandle)
                        ? directDraftEditorHandle
                    : boundEditorHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(boundEditorHandle)
                        ? boundEditorHandle
                    : GetLockedWindowHandle();
                if (handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out nativeRect)) {
                    left = nativeRect.Left; top = nativeRect.Top;
                    width = nativeRect.Right - nativeRect.Left;
                    height = nativeRect.Bottom - nativeRect.Top;
                } else {
                    Rect rect = mainWindow.Current.BoundingRectangle;
                    left = (int)rect.Left; top = (int)rect.Top;
                    width = (int)rect.Width; height = (int)rect.Height;
                }
                if (width < 10 || height < 10) return "";
                using (var bitmap = new System.Drawing.Bitmap(width, height))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    string path = Path.Combine(AppPaths.LogDirectory, fileName);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return path;
                }
            } catch (Exception ex) {
                logger.Warn("保存字幕识别OCR截图失败: " + ex.Message);
                return "";
            }
        }

        private string CaptureElementWindow(AutomationElement element, string fileName)
        {
            if (element == null) return "";
            try {
                Rect rect = element.Current.BoundingRectangle;
                if (rect.Width < 10 || rect.Height < 10) return "";
                using (var bitmap = new System.Drawing.Bitmap((int)rect.Width, (int)rect.Height))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen((int)rect.Left, (int)rect.Top, 0, 0, bitmap.Size);
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    string path = Path.Combine(AppPaths.LogDirectory, fileName);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    logger.Info("已保存导出窗口OCR截图: " + path);
                    return path;
                }
            } catch (Exception ex) {
                logger.Warn("保存导出窗口OCR截图失败: " + ex.Message);
                return "";
            }
        }

        private static void SleepChecked(int milliseconds, CancellationToken token)
        {
            int remaining = milliseconds;
            while (remaining > 0) {
                token.ThrowIfCancellationRequested();
                int slice = Math.Min(remaining, 200);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }
    }
}
