import { spawnSync } from "node:child_process";
import { statSync } from "node:fs";
const probeCache = new Map();
function fraction(value) {
    if (typeof value === "number" && Number.isFinite(value))
        return value;
    if (typeof value !== "string")
        return null;
    const [a, b] = value.split("/").map(Number);
    const result = b ? a / b : a;
    return Number.isFinite(result) && result > 0 ? result : null;
}
export function parseMediaProbe(json) {
    let parsed;
    try {
        parsed = JSON.parse(json);
    }
    catch {
        return null;
    }
    const streams = Array.isArray(parsed.streams) ? parsed.streams : [];
    const video = streams.find((stream) => stream.codec_type === "video");
    const audio = streams.find((stream) => stream.codec_type === "audio");
    if (!video && !audio)
        return null;
    let rotation = 0;
    const tags = video?.tags;
    if (tags?.rotate !== undefined && Number.isFinite(Number(tags.rotate)))
        rotation = Number(tags.rotate);
    else if (Array.isArray(video?.side_data_list)) {
        const item = video.side_data_list.find((value) => Number.isFinite(Number(value.rotation)));
        if (item)
            rotation = Number(item.rotation);
    }
    rotation = normalizeRotation(rotation);
    let width = video && Number(video.width) > 0 ? Number(video.width) : null;
    let height = video && Number(video.height) > 0 ? Number(video.height) : null;
    if ((rotation === 90 || rotation === 270) && width && height)
        [width, height] = [height, width];
    const format = (parsed.format ?? {});
    const durationSeconds = fraction(format.duration) ?? fraction(video?.duration) ?? fraction(audio?.duration);
    const videoDurationSeconds = fraction(video?.duration);
    return {
        width,
        height,
        rotation,
        durationUs: durationSeconds === null ? null : Math.round(durationSeconds * 1_000_000),
        videoDurationUs: videoDurationSeconds === null ? null : Math.round(videoDurationSeconds * 1_000_000),
        fps: fraction(video?.avg_frame_rate) ?? fraction(video?.r_frame_rate),
        hasVideo: Boolean(video),
        hasAudio: Boolean(audio),
        audioChannels: audio && Number(audio.channels) > 0 ? Number(audio.channels) : null,
        videoCodec: typeof video?.codec_name === "string" ? video.codec_name : null,
        audioCodec: typeof audio?.codec_name === "string" ? audio.codec_name : null,
    };
}
export function probeMedia(path, ffprobeCmd = "ffprobe", useCache = true) {
    let cacheKey = `${ffprobeCmd}:${path}`;
    try {
        const stat = statSync(path);
        cacheKey += `:${stat.size}:${stat.mtimeMs}`;
    }
    catch {
        return null;
    }
    if (useCache && probeCache.has(cacheKey))
        return probeCache.get(cacheKey) ?? null;
    let result;
    try {
        result = spawnSync(ffprobeCmd, ["-v", "quiet", "-print_format", "json", "-show_streams", "-show_format", path], {
            encoding: "utf-8",
            timeout: 30_000,
            maxBuffer: 16 * 1024 * 1024,
        });
    }
    catch {
        return null;
    }
    const parsed = result.status === 0 && typeof result.stdout === "string" ? parseMediaProbe(result.stdout) : null;
    if (useCache)
        probeCache.set(cacheKey, parsed);
    return parsed;
}
/**
 * Parse `ffprobe -show_streams -print_format json` output and pull the stored
 * width/height plus display rotation off the first video stream. Rotation is
 * read from `tags.rotate` (older ffmpeg) or a Display Matrix `side_data_list`
 * entry (newer ffmpeg), normalized to 0/90/180/270 clockwise. Returns null if
 * the JSON has no usable video stream.
 */
export function parseProbeStreams(json) {
    let parsed;
    try {
        parsed = JSON.parse(json);
    }
    catch {
        return null;
    }
    const streams = parsed.streams;
    if (!Array.isArray(streams))
        return null;
    const video = streams.find((s) => s &&
        typeof s === "object" &&
        s.codec_type === "video" &&
        Number.isFinite(Number(s.width)) &&
        Number.isFinite(Number(s.height)));
    if (!video)
        return null;
    const width = Number(video.width);
    const height = Number(video.height);
    if (!(width > 0) || !(height > 0))
        return null;
    let rotation = 0;
    const tags = video.tags;
    if (tags && tags.rotate !== undefined && Number.isFinite(Number(tags.rotate))) {
        rotation = Number(tags.rotate);
    }
    else if (Array.isArray(video.side_data_list)) {
        for (const sd of video.side_data_list) {
            if (sd && Number.isFinite(Number(sd.rotation))) {
                rotation = Number(sd.rotation);
                break;
            }
        }
    }
    return { width, height, rotation: normalizeRotation(rotation) };
}
/** Normalize any degree value (negative or >360) to one of 0/90/180/270. */
export function normalizeRotation(rotation) {
    const r = ((Math.round(rotation) % 360) + 360) % 360;
    // Snap to the nearest quarter turn; ffprobe occasionally reports e.g. -90.
    return (Math.round(r / 90) * 90) % 360;
}
/**
 * Apply rotation to stored dimensions to get on-screen (display) dimensions.
 * A 90/270 degree rotation swaps width and height.
 */
export function displayDimensions(d) {
    const rot = normalizeRotation(d.rotation);
    if (rot === 90 || rot === 270)
        return { width: d.height, height: d.width };
    return { width: d.width, height: d.height };
}
/**
 * Live, best-effort probe. Runs ffprobe against `path` and returns the
 * on-screen dimensions, or null if ffprobe is unavailable/fails/unparseable.
 * `ffprobeCmd` overrides the binary (mirrors render's `--ffmpeg-cmd`).
 */
export function probeVideoDimensions(path, ffprobeCmd = "ffprobe") {
    const parsed = probeMedia(path, ffprobeCmd);
    if (!parsed?.width || !parsed.height)
        return null;
    return { width: parsed.width, height: parsed.height, rotation: parsed.rotation };
}
