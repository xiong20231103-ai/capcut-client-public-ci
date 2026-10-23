import { type TextRangeInput } from "./decorators.js";
import type { Draft } from "./draft.js";
export declare const PRESET_VERSION = 1;
export interface TextStylePreset {
    capcutCliPreset: number;
    style: Record<string, unknown>;
    transform?: {
        x: number;
        y: number;
    };
    bubble?: {
        effect_id: string;
        resource_id: string;
    };
    text_ranges?: TextRangeInput[];
}
export declare function extractTextPreset(draft: Draft, segmentId: string): {
    preset: TextStylePreset;
    segmentId: string;
    materialId: string;
    captured: string[];
};
export declare function parsePreset(raw: string, source: string): TextStylePreset;
export declare function loadPresetFile(path: string): TextStylePreset;
export declare function applyTextPreset(draft: Draft, segmentId: string, preset: TextStylePreset): {
    materialId: string;
    applied: string[];
};
export declare function captionStyleFromPreset(preset: TextStylePreset): Record<string, unknown>;
