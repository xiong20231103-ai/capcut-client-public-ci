import type { Draft } from "./draft.js";
import { type Namespace } from "./enums.js";
export interface AddSfxOptions {
    slug: string;
    start: number;
    duration: number;
    trackName?: string;
    namespace?: Namespace;
    volume?: number;
}
export declare function addSfx(draft: Draft, opts: AddSfxOptions): {
    segmentId: string;
    materialId: string;
    trackId: string;
    name: string;
    slug: string;
};
