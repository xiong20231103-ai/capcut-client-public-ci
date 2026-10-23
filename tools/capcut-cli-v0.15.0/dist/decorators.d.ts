import type { Draft } from "./draft.js";
import { type Namespace } from "./enums.js";
export declare function keyframeProperties(): string[];
export type EasingName = "linear" | "ease-in" | "ease-out" | "ease-in-out";
export declare function keyframeEasings(): string[];
export declare function resolveEasing(easing: string | undefined): EasingName;
export declare function parseKeyframeValue(property: string, value: string): number;
export interface KeyframeInput {
    property: string;
    timeUs: number;
    value: number;
    easing?: string;
}
export declare function uuidHex(): string;
export declare function addKeyframes(draft: Draft, segmentId: string, keyframes: KeyframeInput[], easing?: string): {
    segmentId: string;
    added: number;
    lists: Array<{
        property: string;
        count: number;
    }>;
    warnings: string[];
};
export declare function transitionSlugs(namespace?: Namespace): string[];
export declare function addTransition(draft: Draft, segmentId: string, slug: string, durationUs?: number, namespace?: Namespace): {
    segmentId: string;
    transition_id: string;
    name: string;
    duration_us: number;
};
export declare function maskSlugs(namespace?: Namespace): string[];
export interface MaskOptions {
    centerX?: number;
    centerY?: number;
    size?: number;
    rotation?: number;
    feather?: number;
    invert?: boolean;
    rectWidth?: number;
    roundCorner?: number;
}
export declare function addMask(draft: Draft, segmentId: string, slug: string, opts: MaskOptions, namespace?: Namespace): {
    segmentId: string;
    mask_id: string;
    name: string;
};
export declare function setBgBlur(draft: Draft, segmentId: string, level: 1 | 2 | 3 | 4 | "off"): {
    segmentId: string;
    canvas_id: string | null;
    blur: number | null;
};
export interface TextStyleOptions {
    alpha?: number;
    vertical?: boolean;
    fixedWidth?: number;
    fixedHeight?: number;
    shadow?: boolean;
    shadowAlpha?: number;
    shadowAngle?: number;
    shadowColor?: string;
    shadowDistance?: number;
    shadowSmoothing?: number;
    borderWidth?: number;
    borderColor?: string;
    borderAlpha?: number;
    bgColor?: string;
    bgAlpha?: number;
    bgStyle?: number;
    bgRoundRadius?: number;
    bgWidth?: number;
    bgHeight?: number;
    bgHOffset?: number;
    bgVOffset?: number;
}
export declare function setTextStyle(draft: Draft, segmentId: string, opts: TextStyleOptions): {
    materialId: string;
    applied: string[];
};
export declare function textAnimSlugs(): string[];
export interface TextAnimOptions {
    intro?: string;
    outro?: string;
    introDurationUs?: number;
    outroDurationUs?: number;
}
export declare function imageAnimSlugs(): string[];
export declare function imageAnimCatalogue(): Array<{
    slug: string;
    member: string;
    name: string;
    effect_id: string;
    resource_id: string;
}>;
export interface ImageAnimOptions {
    intro?: string;
    outro?: string;
    combo?: string;
    introDurationUs?: number;
    outroDurationUs?: number;
    comboDurationUs?: number;
}
export declare function addImageAnim(draft: Draft, segmentId: string, opts: ImageAnimOptions, namespace?: Namespace): {
    segmentId: string;
    added: Array<{
        type: string;
        name: string;
        duration_us: number;
        start_us: number;
    }>;
    material_id: string;
};
export declare function addTextAnim(draft: Draft, segmentId: string, opts: TextAnimOptions, namespace?: Namespace): {
    segmentId: string;
    added: Array<{
        type: string;
        name: string;
        duration_us: number;
        start_us: number;
    }>;
    material_id: string;
};
export interface TextRangeInput {
    start: number;
    end: number;
    font_color?: string;
    font_size?: number;
    font_alpha?: number;
    bold?: boolean;
    italic?: boolean;
    underline?: boolean;
}
export declare function setTextRanges(draft: Draft, segmentId: string, ranges: TextRangeInput[]): {
    segmentId: string;
    material_id: string;
    styles: number;
    text_length: number;
};
/** The gold `caption --karaoke` paints the active word with; --keyword-color defaults to it. */
export declare const KARAOKE_HIGHLIGHT_COLOR = "#FFD700";
/** Default --keyword-size multiplier when --highlight-words is present. */
export declare const DEFAULT_KEYWORD_SIZE = 1.2;
/**
 * Case-insensitive whole-word matches of `words` (words or phrases) in `text`.
 * Offsets are JS code-unit indices — exactly what TextRangeInput.start/end
 * take. "Whole word" is Unicode-aware: a match may not butt against a letter
 * or digit, so `für` matches inside `Grüße für alle` but `cap` never matches
 * inside `capcut`. Overlapping matches (e.g. "New York" + "York") keep the
 * earlier one, matching setTextRanges' no-overlap contract.
 */
export declare function findKeywordRanges(text: string, words: string[]): Array<{
    start: number;
    end: number;
}>;
export interface KeywordEmphasisOptions {
    words: string[];
    color: string;
    sizeMultiplier: number;
    baseSize: number;
    /**
     * When set, gaps between emphasis ranges are written as explicit base-styled
     * ranges instead of relying on setTextRanges' styles[0] inheritance. Caption
     * materials store their base style as {font_color, font_size} (no fill/size
     * keys), which that inheritance cannot see — explicit coverage keeps the
     * cue's real base colour/size on the unmatched text.
     */
    baseColor?: string;
    baseBold?: boolean;
    /** Ranges built first (e.g. the karaoke active word); keyword matches override them. */
    presetRanges?: TextRangeInput[];
}
/**
 * Build the non-overlapping range list for one cue: keyword emphasis ranges,
 * any preset (karaoke) ranges that no keyword overlaps, and — when baseColor
 * is given — explicit base-styled gap ranges covering the rest of the text.
 * Follows the v0.13 "explicit flags beat preset ranges" contract: a keyword
 * match overrides the overlapped preset word's colour/size but inherits its
 * bold/italic/underline. Returns zero ranges when there is nothing to style.
 */
export declare function buildEmphasisRanges(text: string, opts: KeywordEmphasisOptions): {
    ranges: TextRangeInput[];
    matches: number;
};
export declare function bubbleSlugs(): string[];
export declare function bubbleCatalogue(): Array<{
    slug: string;
    member: string;
    name: string;
    effect_id: string;
    resource_id: string;
}>;
export interface BubbleOptions {
    slug?: string;
    effectId?: string;
    resourceId?: string;
}
export declare function setBubble(draft: Draft, segmentId: string, opts: BubbleOptions): {
    segmentId: string;
    bubble_id: string;
    effect_id: string;
    resource_id: string;
};
