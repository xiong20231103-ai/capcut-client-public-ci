import type { Draft } from "./draft.js";
export type AppSource = "cc" | "lv" | "unknown";
/** Evidence labels, aligned with the taxonomy in docs/version-support.md. */
export type SupportEvidence = "fixture-tested" | "synthetic-tested" | "reported" | "expected-compatible" | "none";
export interface VersionInfo {
    app: "CapCut" | "JianYing" | "unknown";
    app_source: AppSource;
    app_version: string | null;
    os: string | null;
    schema: {
        mask_field: "mask" | "common_masks" | "both" | "none";
        has_text_ranges: boolean;
        has_audio_fades: boolean;
        new_version_field: unknown;
        last_modified_platform: unknown;
        /** Top-level `version` schema integer, when present (the field real
         * app-created drafts carry; CLI-created drafts legitimately lack it). */
        schema_int: number | null;
    };
    support: {
        status: "supported" | "untested" | "known-broken";
        notes: string[];
        /** How the status claim is backed — see docs/version-support.md. */
        evidence: SupportEvidence;
        /** True when a detected version/schema marker is newer than anything this
         * CLI has evidence for. */
        beyond_known_range: boolean;
        /** What saveDraft would do on a write: ok, warn (write + stderr WARNING),
         * or refuse (needs --force-write). */
        write_guard: "ok" | "warn" | "refuse";
    };
}
export declare const KNOWN_SCHEMA_INT_MAX = 360000;
export declare function versionTuple(version: string | null): number[];
export declare function atLeast(version: string | null, wanted: string): boolean;
export interface WriteSafety {
    action: "ok" | "warn" | "refuse";
    /** Human sentences, one per trigger; ends with the fixture-collection CTA
     * whenever action is not "ok". */
    reasons: string[];
    detected: {
        app_source: AppSource;
        app_version: string | null;
        schema_int: number | null;
        beyond_known_range: boolean;
    };
}
/**
 * Write-time version boundary (mined pain: NEW app builds reject tool-written
 * drafts as "content corrupted" / 内容已损坏, and no version marker was checked
 * at write time). Refuses when the draft's detected version or schema
 * generation is beyond the registry's evidence or known-broken; warns on
 * unrecognized-but-marked sources and old schema generations. Markerless
 * drafts (`capcut create` output has no platform and no top-level `version`)
 * never trigger — every marker is optional.
 *
 * `storeVersion` is the max app_version across the project's readable sibling
 * files, so a mirror written by a newer app build trips the guard too. Pass
 * null when no store has been discovered (dry-run, single-draft assessment).
 */
export declare function assessWriteSafety(draft: Draft, storeVersion: string | null): WriteSafety;
export declare function detectVersion(draft: Draft): VersionInfo;
