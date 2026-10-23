import type { Draft } from "./draft.js";
export interface ChromaOptions {
    color: string;
    intensity?: number;
    shadow?: number;
}
export declare function setChroma(draft: Draft, segId: string, opts: ChromaOptions): {
    ok: true;
    segmentId: string;
    materialId: string;
    color: string;
    intensity: number;
    shadow: number;
};
export declare function removeChroma(draft: Draft, segId: string): {
    ok: true;
    segmentId: string;
    removed: string[];
};
