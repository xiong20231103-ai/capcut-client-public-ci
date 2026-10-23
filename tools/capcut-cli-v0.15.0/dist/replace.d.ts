import { type Draft } from "./draft.js";
export interface ReplaceMediaOptions {
    segmentId: string;
    newPath: string;
    ffprobeCmd?: string;
    retime?: boolean;
    dryRun?: boolean;
}
export interface ReplaceMediaResult {
    ok: boolean;
    segment_id: string;
    material_id: string;
    material_type: string;
    old_path: string;
    new_path: string;
    shared_with_segments: number;
    old_duration_us: number | null;
    new_duration_us: number | null;
    source_used_us: number;
    retimed: boolean;
    warning?: string;
}
/**
 * Point a segment's material at a new source file, copy that file into the
 * draft's assets directory, and refresh the material's intrinsic metadata.
 * Preserves the segment's target/source timeranges by default.
 */
export declare function replaceMedia(draft: Draft, filePath: string, opts: ReplaceMediaOptions): ReplaceMediaResult;
