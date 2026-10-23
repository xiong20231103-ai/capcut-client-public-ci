using System;

namespace CapCutBatchClient
{
    internal enum CapCutWindowRole
    {
        Unknown,
        Home,
        Transition,
        Editor
    }

    internal enum CapCutUiState
    {
        Unavailable,
        Home,
        Editor,
        Transitioning,
        Dialog,
        Ambiguous
    }

    internal sealed class CapCutWindowEvidence
    {
        internal bool Available { get; set; }
        internal bool Dialog { get; set; }
        internal string ClassName { get; set; }
        internal bool ExactCreateProjectControl { get; set; }
        internal bool ExactExportControl { get; set; }
        internal bool TimelineStructure { get; set; }
        internal bool MediaPanelStructure { get; set; }
        // Diagnostic only. Pixel colour is deliberately excluded from state
        // classification because preview content is untrusted visual data.
        internal bool CyanAtCreateProjectCoordinate { get; set; }
    }

    internal static class CapCutWindowStateClassifier
    {
        internal static CapCutUiState Classify(CapCutWindowEvidence evidence)
        {
            if (evidence == null || !evidence.Available)
                return CapCutUiState.Unavailable;
            if (evidence.Dialog) return CapCutUiState.Dialog;

            CapCutWindowRole role = CapCutHomeShellPolicy.Classify(
                evidence.ClassName);
            bool editorEvidence = role == CapCutWindowRole.Editor ||
                evidence.ExactExportControl ||
                (evidence.TimelineStructure && evidence.MediaPanelStructure);
            // Editor evidence always wins when a landing page and editor coexist.
            if (editorEvidence) return CapCutUiState.Editor;
            if (role == CapCutWindowRole.Transition)
                return CapCutUiState.Transitioning;
            if (role == CapCutWindowRole.Home ||
                evidence.ExactCreateProjectControl)
                return CapCutUiState.Home;
            return CapCutUiState.Ambiguous;
        }
    }

    internal static class CapCutHomeShellPolicy
    {
        internal static CapCutWindowRole Classify(string className)
        {
            string value = className ?? "";
            if (value.StartsWith("HomePageX_QMLTYPE_", StringComparison.OrdinalIgnoreCase))
                return CapCutWindowRole.Home;
            if (value.StartsWith("AIShortsWebViewWindow_QMLTYPE_", StringComparison.OrdinalIgnoreCase))
                return CapCutWindowRole.Transition;
            if (value.StartsWith("MainWindow_QMLTYPE_", StringComparison.OrdinalIgnoreCase))
                return CapCutWindowRole.Editor;
            return CapCutWindowRole.Unknown;
        }

        internal static bool IsHome(string className, bool unknownClassHasHomeEvidence)
        {
            CapCutWindowRole role = Classify(className);
            if (role == CapCutWindowRole.Home) return true;
            if (role == CapCutWindowRole.Editor || role == CapCutWindowRole.Transition) return false;
            return unknownClassHasHomeEvidence;
        }

        internal static int EditorInitializationTimeoutSeconds(int requestedTimeoutSeconds)
        {
            // A cold CapCut editor launch can spend several seconds probing the
            // GPU, renderer and encoder before its QML controls become usable.
            // Keep the wait bounded, but never reuse the old 15-second ceiling.
            return Math.Max(45, Math.Min(90, Math.Max(1, requestedTimeoutSeconds)));
        }

        internal static int DraftCreationTransitionTimeoutSeconds(int requestedTimeoutSeconds,
            bool coldStart)
        {
            // Creating a draft can take longer than merely discovering an
            // already-open editor.  The old 30-second cap rejected valid
            // sessions while CapCut was still materializing the editor.
            int minimum = coldStart ? 90 : 60;
            return Math.Max(minimum, Math.Min(90, Math.Max(1, requestedTimeoutSeconds)));
        }

        internal static bool CanBindEditor(IntPtr homeHandle, IntPtr candidateHandle,
            bool candidateVisible, bool candidateIsHomeShell, bool taskIdentityMatches,
            bool editorRoleConfirmed)
        {
            return homeHandle != IntPtr.Zero &&
                candidateHandle != IntPtr.Zero &&
                candidateVisible &&
                !candidateIsHomeShell &&
                taskIdentityMatches &&
                // CapCut may morph the landing-page HWND into the editor in
                // place.  A same-handle bind is safe only after the candidate
                // has independently been classified as an editor.  A
                // replacement HWND remains valid as before.
                (homeHandle != candidateHandle || editorRoleConfirmed);
        }

        internal static bool CanBindEditor(IntPtr homeHandle, IntPtr candidateHandle,
            bool candidateVisible, bool candidateIsHomeShell, bool taskIdentityMatches)
        {
            // Compatibility overload for callers that do not have a role
            // result.  Such callers must still use a replacement window.
            return CanBindEditor(homeHandle, candidateHandle, candidateVisible,
                candidateIsHomeShell, taskIdentityMatches, false);
        }

        internal static bool CanProceedToExport(bool editorBound, bool editorResponsive,
            bool homePageVisible, bool exportEntryVisible)
        {
            return editorBound && editorResponsive && !homePageVisible && exportEntryVisible;
        }

        internal static bool CanClose(IntPtr homeHandle, IntPtr editorHandle, uint homeProcessId,
            uint editorProcessId, bool isVerifiedHomeShell, bool isVerifiedEditorBinding)
        {
            // Closing a homepage HWND can terminate the whole QML host when the
            // homepage and editor share one CapCut process. Keep that shell alive;
            // only an independently hosted, verified homepage may be closed.
            return isVerifiedHomeShell &&
                isVerifiedEditorBinding &&
                homeHandle != IntPtr.Zero &&
                editorHandle != IntPtr.Zero &&
                homeHandle != editorHandle &&
                homeProcessId != 0 &&
                editorProcessId != 0 &&
                homeProcessId != editorProcessId;
        }
    }
}
