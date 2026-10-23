/**
 * Best-effort media dimension probe.
 *
 * `add-video` previously hardcoded 1920x1080 for every source, so a portrait
 * (1080x1920) clip landed in the draft as landscape and CapCut rendered it
 * letterboxed/cropped. This module shells out to `ffprobe` to read the real
 * stored dimensions AND the display rotation, then returns the dimensions as
 * they should appear on screen.
 *
 * Architecture mirrors `render`/`caption`: the parsing + rotation math is a
 * pure, deterministic function (`parseProbeStreams` / `displayDimensions`)
 * that tests can assert without invoking ffprobe; the live shell-out
 * (`probeVideoDimensions`) is best-effort and returns null whenever ffprobe is
 * missing, errors, or emits something we cannot parse. Callers fall back to
 * their previous defaults on null, so a host without ffprobe is never worse
 * off than before — it just does not get auto-detection.
 */
export interface ProbedDimensions {
    width: number;
    height: number;
    rotation: number;
}
export interface MediaProbe {
    width: number | null;
    height: number | null;
    rotation: number;
    durationUs: number | null;
    videoDurationUs: number | null;
    fps: number | null;
    hasVideo: boolean;
    hasAudio: boolean;
    audioChannels: number | null;
    videoCodec: string | null;
    audioCodec: string | null;
}
export declare function parseMediaProbe(json: string): MediaProbe | null;
export declare function probeMedia(path: string, ffprobeCmd?: string, useCache?: boolean): MediaProbe | null;
/**
 * Parse `ffprobe -show_streams -print_format json` output and pull the stored
 * width/height plus display rotation off the first video stream. Rotation is
 * read from `tags.rotate` (older ffmpeg) or a Display Matrix `side_data_list`
 * entry (newer ffmpeg), normalized to 0/90/180/270 clockwise. Returns null if
 * the JSON has no usable video stream.
 */
export declare function parseProbeStreams(json: string): ProbedDimensions | null;
/** Normalize any degree value (negative or >360) to one of 0/90/180/270. */
export declare function normalizeRotation(rotation: number): number;
/**
 * Apply rotation to stored dimensions to get on-screen (display) dimensions.
 * A 90/270 degree rotation swaps width and height.
 */
export declare function displayDimensions(d: ProbedDimensions): {
    width: number;
    height: number;
};
/**
 * Live, best-effort probe. Runs ffprobe against `path` and returns the
 * on-screen dimensions, or null if ffprobe is unavailable/fails/unparseable.
 * `ffprobeCmd` overrides the binary (mirrors render's `--ffmpeg-cmd`).
 */
export declare function probeVideoDimensions(path: string, ffprobeCmd?: string): {
    width: number;
    height: number;
    rotation: number;
} | null;
