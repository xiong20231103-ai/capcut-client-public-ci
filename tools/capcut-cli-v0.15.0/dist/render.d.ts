import type { Draft } from "./draft.js";
export interface RenderOptions {
    out?: string;
    scale?: number;
    fps?: number;
    ffmpegCmd?: string;
    burnCaptions?: boolean;
    allVideoTracks?: boolean;
    dryRun?: boolean;
}
export interface RenderInput {
    index: number;
    path: string;
    kind: "video" | "photo" | "audio";
}
export interface RenderPlan {
    output: string;
    width: number;
    height: number;
    fps: number;
    inputs: RenderInput[];
    filterComplex: string;
    args: string[];
    videoSegments: number;
    audioSegments: number;
    textOverlays: number;
    skipped: Array<{
        segmentId: string;
        reason: string;
    }>;
    overlaySegments: number;
    capabilities?: FfmpegCapabilities;
}
export interface RenderResult extends RenderPlan {
    ok: boolean;
    executed: boolean;
}
export interface FfmpegCapabilities {
    available: boolean;
    drawtext: boolean;
    overlay: boolean;
    x264: boolean;
}
export declare function probeFfmpegCapabilities(command?: string): FfmpegCapabilities;
/**
 * Build the ffmpeg invocation for a draft. Pure and deterministic given the
 * draft + options (no uuids, no clock) so it can be asserted in tests.
 */
export declare function buildRenderPlan(draft: Draft, opts: RenderOptions): RenderPlan;
export declare function renderDraft(draft: Draft, filePath: string, opts: RenderOptions): RenderResult;
