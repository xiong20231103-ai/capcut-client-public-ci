import type { Draft } from "./draft.js";
import { type Namespace } from "./enums.js";
import { type WikimediaAsset } from "./wikimedia.js";
/**
 * If `path` is an http(s) URL, resolve it through the Wikimedia fetcher, saving
 * into a `wikimedia/` subdir of the draft's assets folder. Non-Wikimedia URLs
 * error — keeping network scope tight. Returns the local path + the fetched
 * asset (null for plain filesystem paths).
 */
export declare function resolveAssetPath(path: string, draftFilePath: string, kind: "video" | "audio", forceLicense?: boolean): Promise<{
    localPath: string;
    asset: WikimediaAsset | null;
    warning?: string;
}>;
export declare function uuid(): string;
export interface InitOptions {
    name: string;
    templateDir: string;
    draftsDir: string;
    now?: number;
}
export declare function initDraft(opts: InitOptions): {
    draftPath: string;
    filePath: string;
    registered: boolean;
};
interface RegisterOptions {
    draftsDir: string;
    draftPath: string;
    filePath: string;
    draftId: string;
    name: string;
    nowMs: number;
    durationUs?: number;
}
/**
 * Add the new draft to root_meta_info.json (creating the file if absent) and write
 * a per-folder draft_meta_info.json sidecar. Merges into the existing index — never
 * clobbers other drafts — and backs the index up to .bak before writing.
 * Returns true if the draft was registered in the root index.
 */
export declare function registerDraftInIndex(opts: RegisterOptions): boolean;
export interface RegistrationTarget {
    file: "draft_meta_info.json" | "root_meta_info.json";
    path: string | null;
    state: "ok" | "missing" | "unreadable" | "stale" | "unregistered" | "unknown-store-root";
    action: "none" | "create" | "update" | "blocked";
    detail: string;
    stale_fields: string[];
}
export interface RegistrationPlan {
    project_dir: string;
    content_path: string;
    draft_id: string;
    draft_name: string;
    duration_us: number;
    store_root: string | null;
    store_root_source: string;
    needs_repair: boolean;
    targets: RegistrationTarget[];
    /** Files --apply would write (target action create/update). */
    repairs: string[];
    /** Targets needing attention that the CLI refuses to write (target action blocked). */
    blocked: string[];
}
interface RegistrationWrite {
    file: RegistrationTarget["file"];
    path: string;
    content: string;
    /** Raw pre-plan content, null when the file did not exist (no .bak then). */
    previous: string | null;
}
export interface RegistrationResult {
    plan: RegistrationPlan;
    writes: RegistrationWrite[];
}
/**
 * Plan for `capcut register`: report whether an existing draft's
 * draft_meta_info.json sidecar and root_meta_info.json entry are present and
 * agree with draft_content.json (the read-only id/name/duration source), and
 * prepare the exact writes --apply would make. Accepts a project directory or
 * its draft_content.json path; any other explicitly named file is rejected so
 * the plan and the write always cover the same target set. An unreadable
 * root_meta_info.json is never rewritten (it lists EVERY draft); it is
 * reported blocked instead — as is everything when the draft does not live
 * inside a known store root.
 */
export declare function planDraftRegistration(input: string, options?: {
    draftsDir?: string;
    now?: number;
}): RegistrationResult;
/**
 * Perform the writes a registration plan prepared: temp+fsync+rename per file,
 * with a `.bak` of the pre-plan content for every file that already existed.
 * Unless forceWrite, refuses when a target changed on disk between the plan
 * read and now (the sync-timelines optimistic-concurrency rule).
 */
export declare function applyDraftRegistration(result: RegistrationResult, options?: {
    forceWrite?: boolean;
}): {
    applied: string[];
    backups: string[];
};
interface CompanionRefs {
    ids: string[];
    materials: Array<{
        type: string;
        data: Record<string, unknown>;
    }>;
}
export declare function createCompanionMaterials(trackType: "text" | "video" | "audio" | "sticker" | "effect"): CompanionRefs;
export declare function registerCompanions(draft: Draft, companions: CompanionRefs): void;
export interface AddTextOptions {
    text: string;
    start: number;
    duration: number;
    fontSize?: number;
    color?: string;
    alignment?: number;
    x?: number;
    y?: number;
    trackName?: string;
}
export declare const STYLE_FIELDS: readonly ["alignment", "font_size", "text_color", "typesetting", "letter_spacing", "line_spacing", "line_feed", "line_max_width", "force_apply_line_max_width", "fixed_width", "fixed_height", "text_alpha", "has_shadow", "shadow_alpha", "shadow_angle", "shadow_color", "shadow_distance", "shadow_smoothing", "has_border", "border_width", "border_color", "border_alpha", "has_text_shadow_config", "background_color", "background_alpha", "background_style", "background_round_radius", "background_width", "background_height", "background_horizontal_offset", "background_vertical_offset", "font_id", "font_name", "font_path", "font_resource_id", "bold", "italic", "underline"];
export declare function copyTextStyle(draft: Draft, refSegmentId: string, targetMaterialId: string, opts?: {
    keepFillColor?: boolean;
}): void;
export declare function addText(draft: Draft, _filePath: string, opts: AddTextOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
};
export interface AddAudioOptions {
    path: string;
    start: number;
    duration: number;
    volume?: number;
    trackName?: string;
}
export declare function addAudio(draft: Draft, filePath: string, opts: AddAudioOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
};
export interface AddVideoOptions {
    path: string;
    start: number;
    duration: number;
    type?: "video" | "photo";
    width?: number;
    height?: number;
    trackName?: string;
}
export declare function addVideo(draft: Draft, filePath: string, opts: AddVideoOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
};
export interface CutOptions {
    start: number;
    end: number;
}
export declare function cutProject(draft: Draft, opts: CutOptions): {
    kept: number;
    removed: number;
};
/**
 * Orphan-material GC shared by `prune` and `remove`: drop every material entry
 * that no surviving segment references via material_id or extra_material_refs
 * (the latter is what keeps masks/effects/animations/fades from being wrongly
 * deleted). Entries without a string id are kept (can't prove they're
 * orphaned). Behavior-identical extraction of the original cmdPrune loop; the
 * caller decides whether a nonzero `removed` warrants a save.
 */
export declare function pruneOrphanMaterials(draft: Draft): {
    removed: number;
    byType: Record<string, {
        removed: number;
        kept: number;
    }>;
};
export interface RemoveSegmentOptions {
    keepTrack?: boolean;
    keepMaterials?: boolean;
}
export interface RemoveSegmentResult {
    segmentId: string;
    trackId: string;
    trackName: string;
    trackType: string;
    trackRemoved: boolean;
    materialsRemoved: number;
    materialsByType: Record<string, {
        removed: number;
        kept: number;
    }>;
    durationBefore: number;
    durationAfter: number;
}
/**
 * Remove one segment in place: splice it out of its track, drop the track when
 * it becomes empty (unless keepTrack — cutProject already removes empty tracks
 * wholesale, so CapCut tolerates it), GC newly-orphaned materials with the same
 * conservative sweep prune uses (unless keepMaterials; a material any surviving
 * segment still references is never deleted), and recompute draft.duration as
 * the max segment end across ALL tracks (0 when no segments remain, same as a
 * fresh init draft).
 */
export declare function removeSegment(draft: Draft, segId: string, opts?: RemoveSegmentOptions): RemoveSegmentResult;
export interface Template {
    name: string;
    type: string;
    segment: Record<string, unknown>;
    material: {
        type: string;
        data: Record<string, unknown>;
    };
    extra_materials: Array<{
        type: string;
        data: Record<string, unknown>;
    }>;
}
export declare function saveTemplate(draft: Draft, segId: string, name: string, outPath: string): Template;
export declare function applyTemplate(draft: Draft, templatePath: string, start: number, duration: number, overrides?: {
    x?: number;
    y?: number;
    scaleX?: number;
    scaleY?: number;
    text?: string;
}): {
    segmentId: string;
    materialId: string;
    trackId: string;
};
export interface DuplicateSegmentOptions {
    /** Place the copy onto this existing same-type track instead of creating one. */
    trackName?: string;
}
export interface DuplicateSegmentResult {
    segmentId: string;
    sourceSegmentId: string;
    materialId: string;
    trackId: string;
    trackName: string;
    createdTrack: boolean;
    clonedMaterials: Array<{
        type: string;
        id: string;
        source_id: string;
    }>;
}
/**
 * Duplicate a segment at the SAME timeline position/duration onto a track that
 * renders above the source. Default: a fresh same-type track inserted directly
 * after the source track — sortTracks is stable within a type and a later
 * same-type track renders ABOVE, so the copy sits exactly on top of its
 * source. With `trackName`, the copy goes onto that existing same-type track
 * instead; occupied target range / missing track / type mismatch all throw.
 */
export declare function duplicateSegment(draft: Draft, segId: string, opts?: DuplicateSegmentOptions): DuplicateSegmentResult;
export interface AddStickerOptions {
    resourceId: string;
    start: number;
    duration: number;
    x?: number;
    y?: number;
    scale?: number;
    rotation?: number;
    trackName?: string;
}
export declare function addSticker(draft: Draft, opts: AddStickerOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
};
export declare function effectSlugs(): string[];
export declare function effectCatalogue(): Array<{
    slug: string;
    member: string;
    name: string;
    effect_id: string;
    resource_id: string;
}>;
export interface AddEffectOptions {
    slug: string;
    start: number;
    duration: number;
    params?: number[];
    trackName?: string;
    namespace?: Namespace;
    /** Raw store/catalogue resource ID — skips slug lookup; `slug` becomes the display name. */
    resourceId?: string;
    /** Raw effect ID; defaults to `resourceId`. Only meaningful with `resourceId`. */
    effectId?: string;
    /** Effect strength 0..1 (material `value`); default 1. */
    intensity?: number;
    /** Experimental: bind the effect to one segment (`apply_target_type: 0`) instead of the whole frame. */
    bindSegmentId?: string;
}
export declare function addEffect(draft: Draft, opts: AddEffectOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
    name: string;
};
export declare const MIX_MODES: Record<string, string>;
export declare function mixModeSlugs(): string[];
export declare function setMixMode(draft: Draft, segmentId: string, mode: string): {
    segmentId: string;
    material_id: string;
    mix_mode: string;
};
export interface CropRect {
    x: number;
    y: number;
    w: number;
    h: number;
}
export declare function cropPresets(): string[];
export declare function cropRectForRatio(width: number, height: number, preset: string): CropRect;
export declare function getCrop(draft: Draft, segmentId: string): {
    segmentId: string;
    material_id: string;
    width: number | null;
    height: number | null;
    crop: Record<string, number> | null;
};
export declare function setCrop(draft: Draft, segmentId: string, rect: CropRect): {
    segmentId: string;
    material_id: string;
    rect: CropRect;
    crop: Record<string, number>;
    crop_ratio?: string;
};
export declare function setAudioFade(draft: Draft, segmentId: string, opts: {
    fadeInUs?: number;
    fadeOutUs?: number;
}): {
    segmentId: string;
    fade_id: string;
    fade_in_us: number;
    fade_out_us: number;
};
export declare function setCover(draft: Draft, imagePath: string, timeMs?: number): {
    cover_path: string;
    time_ms: number;
};
export declare function filterSlugs(namespace?: Namespace): string[];
export declare function filterCatalogue(): Array<{
    slug: string;
    member: string;
    name: string;
    effect_id: string;
    resource_id: string;
}>;
export interface AddFilterOptions {
    slug: string;
    start: number;
    duration: number;
    intensity?: number;
    trackName?: string;
    namespace?: Namespace;
    /** Raw store/catalogue resource ID — skips slug lookup; `slug` becomes the display name. */
    resourceId?: string;
    /** Raw effect ID; defaults to `resourceId`. Only meaningful with `resourceId`. */
    effectId?: string;
}
export declare function addFilter(draft: Draft, opts: AddFilterOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
    name: string;
};
export {};
