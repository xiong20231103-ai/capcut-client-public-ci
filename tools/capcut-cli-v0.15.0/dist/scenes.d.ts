export interface SceneCut {
    time: number;
    score: number;
}
export interface SceneSegment {
    start: number;
    end: number | null;
    duration: number | null;
    start_us: number;
    end_us: number | null;
    duration_us: number | null;
}
export interface SceneDetectOptions {
    threshold?: number;
    minGap?: number;
    limit?: number;
    ffmpegCmd?: string;
    ffprobeCmd?: string;
    timeoutMs?: number;
    maxBufferBytes?: number;
}
export interface SceneReport {
    video: string;
    threshold: number;
    min_gap: number;
    limit: number | null;
    duration: number | null;
    duration_us: number | null;
    duration_source: "video-stream" | "container" | null;
    cuts: Array<{
        time: number;
        time_us: number;
        timecode: string;
        score: number;
    }>;
    segments: SceneSegment[];
}
/**
 * Parse the stderr of `-vf select='gt(scene,T)',metadata=print -f null -`.
 * Each selected frame emits two lines:
 *   [Parsed_metadata_1 @ 0x...] frame:0  pts:15360  pts_time:1.024
 *   [Parsed_metadata_1 @ 0x...] lavfi.scene_score=0.400000
 */
export declare function parseSceneCuts(stderr: string): SceneCut[];
/** Pull the input duration off ffmpeg's stderr header ("Duration: 00:00:03.00, ..."). */
export declare function parseFfmpegDuration(stderr: string): number | null;
/**
 * Merge cuts closer than `minGap` seconds. Chains of close cuts collapse into
 * one cluster; the strongest cut (earliest on ties) represents each cluster.
 */
export declare function mergeCloseCuts(cuts: SceneCut[], minGap: number): SceneCut[];
/** Keep the N strongest cuts (earliest wins ties), returned back in time order. */
export declare function limitCuts(cuts: SceneCut[], limit: number | undefined): SceneCut[];
/**
 * Turn cut points into contiguous segments [0..cut1][cut1..cut2][..duration].
 * With an unknown duration the trailing segment is open-ended (end null).
 */
export declare function buildSceneSegments(cuts: SceneCut[], duration: number | null): SceneSegment[];
/**
 * Format seconds as hh:mm:ss.mmm. Rounds to milliseconds FIRST so the carry
 * propagates through seconds/minutes/hours — 59.9996 must become
 * "00:01:00.000", never the out-of-range "00:00:60.000".
 */
export declare function timecode(seconds: number): string;
/**
 * Live detection. Runs ffmpeg's scene filter over `videoPath` and assembles
 * the report. Throws with an actionable message when the file or ffmpeg is
 * missing, or when ffmpeg fails (mirrors render's error style).
 */
export declare function detectScenes(videoPath: string, opts?: SceneDetectOptions): SceneReport;
