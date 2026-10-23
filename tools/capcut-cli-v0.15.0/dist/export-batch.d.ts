export interface ExportBatchOptions {
    draftsDir: string;
    outDir?: string;
    dryRun?: boolean;
    app?: "capcut" | "jianying";
}
export interface ExportBatchResult {
    ok: boolean;
    platform: NodeJS.Platform;
    drafts: string[];
    results: Array<{
        draft: string;
        status: "queued" | "skipped" | "error";
        message?: string;
    }>;
    warning: string;
}
/**
 * Best-effort UI-automated batch export. EXPERIMENTAL.
 *
 * CapCut/JianYing have no headless render CLI. This wraps OS-level automation:
 *   - macOS: AppleScript (`osascript`) opens each draft and triggers Export
 *   - Windows: PowerShell + SendKeys (Ctrl+E export; needs CapCut window focus)
 *   - Linux: not supported — CapCut/JianYing don't run natively
 *
 * Reliability is bounded by the host UI not changing. We surface this clearly
 * with `warning` in the result and `--dry-run` for safe exploration.
 */
export declare function exportBatch(opts: ExportBatchOptions): ExportBatchResult;
export declare function windowsExportScript(draftDir: string, app: "capcut" | "jianying"): string;
