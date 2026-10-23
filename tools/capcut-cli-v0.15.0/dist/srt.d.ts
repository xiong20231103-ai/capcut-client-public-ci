export interface SrtCue {
    index: number;
    startUs: number;
    endUs: number;
    text: string;
}
export declare function parseSrt(content: string): SrtCue[];
export interface SubtitleWord {
    word: string;
    startUs: number;
    endUs: number;
}
export interface SubtitleCue {
    startUs: number;
    endUs: number;
    text: string;
    words?: SubtitleWord[];
}
export interface SegmentCue extends SubtitleCue {
    styleRanges?: Array<[number, number]>;
}
export declare function interpolateWords(text: string, startUs: number, endUs: number): SubtitleWord[];
export declare function cueWords(cue: SubtitleCue): SubtitleWord[];
export declare function collapseKaraokeRuns(entries: SegmentCue[]): SubtitleCue[];
export declare function renderSrt(cues: Array<{
    startUs: number;
    endUs: number;
    text: string;
}>): string;
export declare function renderVtt(cues: SubtitleCue[], wordTimestamps: boolean): string;
