export declare const QUICKSTART_FALLBACK_DURATION_US = 5000000;
export interface QuickstartOptions {
    name: string;
    templateDir: string;
    draftsDir: string;
    video?: string;
    audio?: string;
    srt?: string;
    ffprobeCmd?: string;
    now?: number;
}
export interface QuickstartStep {
    step: string;
    ok: boolean;
    detail: string;
}
export interface QuickstartResult {
    ok: boolean;
    name: string;
    draft_path: string;
    file_path: string;
    registered: boolean;
    added: {
        video: boolean;
        audio: boolean;
        captions: number;
    };
    steps: QuickstartStep[];
    lint: {
        errors: number;
        warnings: number;
        info: number;
        total: number;
    };
    store: {
        canonical: string;
        version: string | null;
        modern_storage: boolean;
        next_actions: string[];
    };
    open_hint: string[];
}
/**
 * Create a minimal editable draft from a single video, audio, and/or SRT input,
 * verify it with the same lint the `lint` command runs, inspect its storage
 * layout the way `diagnose` does, and return an explicit "open in CapCut" hint.
 *
 * Pure orchestration of exported primitives — no new draft-mutation logic — so
 * its output stays identical to running the commands by hand.
 */
export declare function runQuickstart(opts: QuickstartOptions): QuickstartResult;
