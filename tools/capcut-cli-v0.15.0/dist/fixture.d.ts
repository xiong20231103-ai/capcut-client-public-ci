export interface SanitizeFileResult {
    file: string;
    bytes_in: number;
    bytes_out: number;
    redactions: number;
}
export interface SanitizeReport {
    ok: boolean;
    source_dir: string;
    out_dir: string;
    version: string | null;
    modern_storage: boolean;
    files: SanitizeFileResult[];
    redaction_kinds: Record<string, number>;
    media_excluded: boolean;
    notes: string[];
}
/**
 * Build a shareable, redacted fixture bundle from a real draft folder.
 * Reuses the same storage discovery the `diagnose` command uses, so the
 * reported version/canonical-file analysis matches.
 */
export declare function sanitizeDraftBundle(input: string, outDir: string): SanitizeReport;
