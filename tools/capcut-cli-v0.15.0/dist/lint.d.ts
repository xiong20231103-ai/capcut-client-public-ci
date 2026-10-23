import type { Draft } from "./draft.js";
export type Severity = "error" | "warning" | "info";
export interface LintIssue {
    severity: Severity;
    code: string;
    message: string;
    fixable: boolean;
    location?: {
        track?: string;
        segment_id?: string;
        material_id?: string;
        path?: string;
    };
}
export declare const MIN_CAPTION_DURATION_US = 100000;
export interface LintOptions {
    maxCharsPerLine: number;
    maxCueDurationUs: number;
    minGapBetweenCaptionsUs: number;
    checkLocalPaths: boolean;
}
export declare const DEFAULT_LINT_OPTIONS: LintOptions;
export declare function lintDraft(draft: Draft, opts?: LintOptions): LintIssue[];
export declare function summarize(issues: LintIssue[]): {
    errors: number;
    warnings: number;
    info: number;
    total: number;
};
export declare function lintExitCode(summary: {
    errors: number;
    warnings: number;
}): number;
export interface FixResult {
    fixed: LintIssue[];
    remaining: LintIssue[];
}
export declare function fixDraft(draft: Draft, opts?: LintOptions): FixResult;
