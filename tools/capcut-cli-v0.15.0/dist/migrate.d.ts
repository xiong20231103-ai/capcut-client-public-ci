import type { Draft } from "./draft.js";
export type SchemaVersion = "5.9" | "6.0" | "9.6";
export interface MigrationResult {
    ok: boolean;
    from: string;
    to: string;
    applied: string[];
    skipped: string[];
    warnings: string[];
}
/**
 * Migrate a draft's schema across known version jumps.
 *
 * Currently implements:
 *   - mask -> common_masks (JianYing 5.9 -> 9.6+, CapCut older -> newer)
 *
 * Migrations are best-effort: if a field doesn't apply (e.g. no masks present),
 * we record it as skipped rather than failing. The draft is mutated in place.
 */
export declare function migrateDraft(draft: Draft, from: string, to: string): MigrationResult;
