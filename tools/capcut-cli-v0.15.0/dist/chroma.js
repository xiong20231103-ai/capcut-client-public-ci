import { randomUUID as uuid } from "node:crypto";
import { findSegment } from "./draft.js";
export function setChroma(draft, segId, opts) {
    const found = findSegment(draft, segId);
    if (!found)
        throw new Error(`Segment not found: ${segId}`);
    if (found.track.type !== "video") {
        throw new Error(`Chroma key can only be applied to video segments (segment ${segId} is on a ${found.track.type} track)`);
    }
    const rgb = parseHex(opts.color);
    if (!rgb)
        throw new Error(`Invalid color: ${opts.color}. Expected #RRGGBB.`);
    const intensity = clamp01(opts.intensity ?? 0.5);
    const shadow = clamp01(opts.shadow ?? 0);
    const matId = uuid();
    const chromaMaterial = {
        id: matId,
        type: "chromas",
        color: opts.color,
        intensity,
        shadow,
        path: "",
    };
    if (!Array.isArray(draft.materials.chromas))
        draft.materials.chromas = [];
    draft.materials.chromas.push(chromaMaterial);
    const seg = found.segment;
    if (!Array.isArray(seg.extra_material_refs))
        seg.extra_material_refs = [];
    seg.extra_material_refs.push(matId);
    return { ok: true, segmentId: seg.id, materialId: matId, color: opts.color, intensity, shadow };
}
export function removeChroma(draft, segId) {
    const found = findSegment(draft, segId);
    if (!found)
        throw new Error(`Segment not found: ${segId}`);
    const seg = found.segment;
    const removed = [];
    const chromas = draft.materials.chromas ?? [];
    const chromaIds = new Set(chromas.map((c) => c.id));
    if (Array.isArray(seg.extra_material_refs)) {
        seg.extra_material_refs = seg.extra_material_refs.filter((ref) => {
            if (chromaIds.has(ref)) {
                removed.push(ref);
                return false;
            }
            return true;
        });
    }
    draft.materials.chromas = chromas.filter((c) => !removed.includes(c.id));
    return { ok: true, segmentId: seg.id, removed };
}
function parseHex(s) {
    const m = /^#?([0-9a-fA-F]{6})$/.exec(s);
    if (!m)
        return null;
    const n = parseInt(m[1], 16);
    return { r: (n >> 16) & 0xff, g: (n >> 8) & 0xff, b: n & 0xff };
}
function clamp01(x) {
    return Math.max(0, Math.min(1, x));
}
