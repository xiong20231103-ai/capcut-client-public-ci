import type { Draft } from "./draft.js";
declare const STANDARD_FILES: readonly ["draft_content.json", "draft_info.json", "draft_meta_info.json", "template-2.tmp"];
export type DraftCandidateName = (typeof STANDARD_FILES)[number] | string;
export interface DraftCandidate {
    name: DraftCandidateName;
    path: string;
    exists: boolean;
    size: number;
    mtime: string | null;
    sha256: string | null;
    raw: string | null;
    parseable: boolean;
    envelopePath: string[];
    draft: Draft | null;
    timelineHash: string | null;
    error?: string;
}
export interface DraftStore {
    projectDir: string;
    canonical: DraftCandidate;
    targets: DraftCandidate[];
    candidates: DraftCandidate[];
    version: string | null;
    modernStorage: boolean;
    diverged: boolean;
}
export interface DraftStoreReport {
    ok: boolean;
    project_dir: string;
    canonical: string;
    version: string | null;
    modern_storage: boolean;
    diverged: boolean;
    write_guard: "ok" | "warn" | "refuse";
    editor_running: string[];
    candidates: Array<{
        file: string;
        exists: boolean;
        size: number;
        mtime: string | null;
        sha256: string | null;
        parseable_timeline: boolean;
        envelope: string;
        timeline_hash: string | null;
        tracks?: number;
        segments?: number;
        app_version?: string | null;
        error?: string;
    }>;
    next_actions: string[];
}
export declare function discoverDraftStore(input: string): DraftStore;
export declare function serializeDraftCandidate(candidate: DraftCandidate, draft: Draft): string;
export declare function editorProcesses(): string[];
export declare function isManagedDraftPath(path: string): boolean;
export interface TimelineSyncTarget {
    file: string;
    state: "canonical" | "in_sync" | "drifted";
    envelope: string;
    mtime: string | null;
    timeline_hash: string | null;
    guid: string | null;
    guid_drifted: boolean;
    tracks?: number;
    segments?: number;
}
export interface TimelineSyncUnreconcilable {
    file: string;
    reason: string;
    workaround: string;
}
export interface TimelineSyncPlan {
    project_dir: string;
    canonical: string;
    canonical_path: string;
    version: string | null;
    modern_storage: boolean;
    in_sync: boolean;
    /** Drifted mirrors whose mtime is newer than draft_content.json's. CapCut
     * >= 8.7 writes the mirrors on save, so a newer drifted mirror may hold app
     * edits that a canonical -> mirror repair would roll back. */
    newer_mirrors: string[];
    canonical_stale: boolean;
    targets: TimelineSyncTarget[];
    drifted: string[];
    unreconcilable: TimelineSyncUnreconcilable[];
}
export interface TimelineSyncResult {
    plan: TimelineSyncPlan;
    canonicalDraft: Draft;
    canonicalCandidate: DraftCandidate;
    driftedCandidates: DraftCandidate[];
}
/**
 * Plan for `sync-timelines` (issue #39, symptom #35): draft_content.json is
 * canonical; every other readable timeline target is compared against it by
 * timeline hash. Direction is always draft_content.json -> mirror, but each
 * target's mtime is surfaced: CapCut >= 8.7 writes the mirrors on save, so a
 * drifted mirror NEWER than draft_content.json may hold app edits that the
 * repair would roll back (canonical_stale / newer_mirrors flag this; --apply
 * refuses without --force-write). A mirror file that exists but holds no
 * readable timeline (binary/encrypted template-2.tmp) cannot be reconciled
 * and is reported as such instead of being silently skipped. Accepts a
 * project directory or its draft_content.json path; any other explicitly
 * named file is rejected so the plan and the write always cover the same
 * target set.
 */
export declare function planTimelineSync(input: string): TimelineSyncResult;
export declare function diagnoseDraftStore(input: string): DraftStoreReport;
export {};
