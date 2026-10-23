import type { Draft } from "./draft.js";
import { type TextStylePreset } from "./preset.js";
export interface CaptionOptions {
    audio?: string;
    fromSegment?: string;
    whisperCmd?: string;
    whisperModel?: string;
    language?: string;
    trackName?: string;
    styleRef?: string;
    preset?: TextStylePreset;
    whisperEngine?: "auto" | "openai" | "whisper-cpp" | "faster-whisper";
    karaoke?: boolean;
    maxWords?: number;
    maxChars?: number;
    maxGapMs?: number;
    highlightWords?: string[];
    keywordColor?: string;
    keywordSize?: number;
    colorCycle?: string[];
}
export interface CaptionWord {
    word: string;
    startUs: number;
    endUs: number;
}
export interface CaptionCue {
    startUs: number;
    endUs: number;
    text: string;
    words?: CaptionWord[];
}
export interface CaptionResult {
    ok: boolean;
    cues: number;
    language?: string;
    track_name: string;
    first_cue?: {
        start_us: number;
        text: string;
    };
    last_cue?: {
        start_us: number;
        text: string;
    };
    source_audio: string;
    engine: "whisper-cli" | "shell" | "openai" | "stdin-srt";
    engine_name?: string;
    words?: number;
    karaoke?: boolean;
    keyword_matches?: number;
    color_cycle?: number;
}
/**
 * Generate captions by running whisper on the project's audio and emitting
 * real CapCut caption-track segments (not text-segment mimics — fixes the
 * import-srt pain documented in pyJianYingDraft #148).
 *
 * Whisper integration is shell-out; user supplies the binary path. We support
 * the canonical whisper.cpp output naming convention (`<base>.srt`).
 *
 * Architecture:
 *   1. Resolve audio file (either --audio or extracted from --from-segment)
 *   2. Spawn whisper CLI; capture SRT output
 *   3. Parse SRT into caption cues
 *   4. Create captions track + real subtitle material per cue
 */
export declare function captionDraft(draft: Draft, opts: CaptionOptions): CaptionResult;
interface TranscriptionResult {
    cues: CaptionCue[];
    words: CaptionWord[];
    engine: "openai" | "whisper-cpp" | "faster-whisper";
}
export declare function buildWhisperInvocation(engine: TranscriptionResult["engine"], audio: string, model: string, language: string, outputDir: string, json: boolean): {
    args: string[];
    prefix: string;
    extension: ".json" | ".srt";
};
export declare function parseWhisperJson(raw: string): {
    cues: CaptionCue[];
    words: CaptionWord[];
};
export declare function wordsFromCues(cues: CaptionCue[]): CaptionWord[];
export declare function groupWords(words: CaptionWord[], maxWords?: number, maxChars?: number, maxGapUs?: number): CaptionCue[];
export {};
