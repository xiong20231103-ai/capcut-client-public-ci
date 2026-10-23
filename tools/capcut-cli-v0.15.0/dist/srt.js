// Zero-dep SRT parser + SRT/WebVTT renderers. Cues carry microsecond timings.
// Parser format:
//   1
//   00:00:01,000 --> 00:00:04,500
//   line 1
//   line 2
//
//   2
//   ...
// Accepts '.' or ',' as the ms separator. Index lines are optional.
import { srtTime, vttTime } from "./time.js";
const TS = /^(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})/;
function tsToUs(h, m, s, ms) {
    const msPadded = `${ms}000`.slice(0, 3);
    return ((parseInt(h, 10) * 3600 + parseInt(m, 10) * 60 + parseInt(s, 10)) * 1000 + parseInt(msPadded, 10)) * 1000;
}
export function parseSrt(content) {
    const cues = [];
    const lines = content.replace(/\r\n?/g, "\n").split("\n");
    let i = 0;
    let autoIdx = 0;
    while (i < lines.length) {
        while (i < lines.length && lines[i].trim() === "")
            i++;
        if (i >= lines.length)
            break;
        let idx = autoIdx + 1;
        if (/^\d+$/.test(lines[i].trim())) {
            idx = parseInt(lines[i].trim(), 10);
            i++;
        }
        if (i >= lines.length)
            break;
        const m = TS.exec(lines[i]);
        if (!m)
            throw new Error(`Invalid SRT timestamp near line ${i + 1}: ${lines[i]}`);
        const startUs = tsToUs(m[1], m[2], m[3], m[4]);
        const endUs = tsToUs(m[5], m[6], m[7], m[8]);
        i++;
        const textLines = [];
        while (i < lines.length && lines[i].trim() !== "") {
            textLines.push(lines[i]);
            i++;
        }
        if (endUs <= startUs)
            throw new Error(`SRT cue ${idx} has end <= start`);
        cues.push({ index: idx, startUs, endUs, text: textLines.join("\n") });
        autoIdx = idx;
    }
    return cues;
}
// No stored word timing: spread the cue's duration across its words,
// weighted by character length. Approximate by construction.
export function interpolateWords(text, startUs, endUs) {
    const tokens = Array.from(text.matchAll(/\S+/g), (m) => m[0]);
    const total = tokens.reduce((n, w) => n + w.length, 0);
    if (total === 0)
        return [];
    const duration = endUs - startUs;
    let seen = 0;
    return tokens.map((word) => {
        const s = startUs + Math.round((duration * seen) / total);
        seen += word.length;
        const e = startUs + Math.round((duration * seen) / total);
        return { word, startUs: s, endUs: e };
    });
}
export function cueWords(cue) {
    return cue.words ?? interpolateWords(cue.text, cue.startUs, cue.endUs);
}
// Karaoke captions (`caption --karaoke`) store one segment PER WORD: full
// phrase text, word-timed segment, one style range highlighting that word.
// A run of same-text segments whose highlighted ranges tile the phrase's
// words IN ORDER exactly once collapses back into a single cue with real
// word timings. A phrase repeated back-to-back (chorus lines) yields the
// same text 2N times; the highlight ranges cycle, and each cycle restart
// begins a NEW run — one collapsed cue per repetition.
export function collapseKaraokeRuns(entries) {
    const cues = [];
    let i = 0;
    while (i < entries.length) {
        let j = i + 1;
        while (j < entries.length && entries[j].text === entries[i].text)
            j++;
        collapseStretch(entries.slice(i, j), cues);
        i = j;
    }
    return cues;
}
function tokenRanges(text) {
    return [...text.matchAll(/\S+/g)].map((tok) => {
        const at = tok.index ?? 0;
        return {
            word: tok[0],
            byteStart: toUtf16Bytes(text, at),
            byteEnd: toUtf16Bytes(text, at + tok[0].length),
        };
    });
}
function matchesToken(entry, tok) {
    return (entry.styleRanges ?? []).some((r) => r[0] === tok.byteStart && r[1] === tok.byteEnd);
}
// Collapse one maximal stretch of consecutive same-text segments. Greedy:
// at each position, try to consume one full karaoke repetition (highlight
// ranges matching token 0..n-1 in order, non-decreasing start times).
// Segments that cannot join a valid run but still highlight exactly one
// word keep that word's own timing — word mode must never re-interpolate
// the full phrase into a single word's timeslot.
function collapseStretch(stretch, cues) {
    const text = stretch[0].text;
    const tokens = tokenRanges(text);
    let i = 0;
    while (i < stretch.length) {
        if (tokens.length >= 2 && i + tokens.length <= stretch.length) {
            let k = 0;
            while (k < tokens.length &&
                matchesToken(stretch[i + k], tokens[k]) &&
                (k === 0 || stretch[i + k].startUs >= stretch[i + k - 1].startUs)) {
                k++;
            }
            if (k === tokens.length) {
                const run = stretch.slice(i, i + k);
                cues.push({
                    startUs: run[0].startUs,
                    endUs: Math.max(...run.map((e) => e.endUs)),
                    text,
                    words: run.map((e, w) => ({ word: tokens[w].word, startUs: e.startUs, endUs: e.endUs })),
                });
                i += k;
                continue;
            }
        }
        cues.push(orphanCue(stretch, stretch[i], tokens));
        i++;
    }
}
// A same-text segment that did not fit a valid run. When the stretch is
// karaoke-shaped (>= 2 same-text segments) and the segment highlights
// exactly one word, keep that word as the segment's only timed word so
// word-granularity export emits it once at its real time instead of
// exploding the full phrase into the segment's timerange. A lone segment
// (or one without a single-word highlight) stays an ordinary cue.
function orphanCue(stretch, entry, tokens) {
    const cue = { startUs: entry.startUs, endUs: entry.endUs, text: entry.text };
    if (stretch.length < 2)
        return cue;
    const matched = tokens.filter((tok) => matchesToken(entry, tok));
    if (matched.length === 1) {
        cue.words = [{ word: matched[0].word, startUs: entry.startUs, endUs: entry.endUs }];
    }
    return cue;
}
function toUtf16Bytes(text, codeUnitIdx) {
    return Buffer.from(text.slice(0, codeUnitIdx), "utf16le").length;
}
export function renderSrt(cues) {
    return cues.map((c, i) => `${i + 1}\n${srtTime(c.startUs)} --> ${srtTime(c.endUs)}\n${c.text}\n`).join("\n");
}
// WebVTT cue payload: '<' opens a tag and '&' a character reference, so raw
// text must be escaped or everything after a bare '<' is swallowed. Applied
// to cue text only — never to timing lines or inline timestamp tags.
function vttEscape(text) {
    return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
// Word granularity emits ONE cue per phrase with inline word timestamps
// (`one <00:00:01.400>two`) — the karaoke form players understand. A leading
// timestamp is omitted: the first word is active from the cue start.
export function renderVtt(cues, wordTimestamps) {
    const blocks = cues.map((c) => {
        const text = wordTimestamps ? vttKaraokeText(c) : vttEscape(c.text);
        return `${vttTime(c.startUs)} --> ${vttTime(c.endUs)}\n${text}\n`;
    });
    return `WEBVTT\n\n${blocks.join("\n")}`;
}
// Inline timestamps must be strictly increasing and strictly greater than
// the cue start (WebVTT spec), or players ignore the tag. Coincident word
// starts (zero-duration Whisper words) share one timestamp group: the
// duplicate tag is skipped and the word joins the previous group.
function vttKaraokeText(cue) {
    const words = cueWords(cue);
    if (words.length === 0)
        return vttEscape(cue.text);
    let lastMs = Math.round(cue.startUs / 1000);
    let text = vttEscape(words[0].word);
    for (const w of words.slice(1)) {
        const ms = Math.round(w.startUs / 1000);
        if (ms > lastMs) {
            text += ` <${vttTime(w.startUs)}>${vttEscape(w.word)}`;
            lastMs = ms;
        }
        else {
            text += ` ${vttEscape(w.word)}`;
        }
    }
    return text;
}
