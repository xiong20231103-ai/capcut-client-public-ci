// Zero-dep ASS / SSA subtitle parser. Returns cues with microsecond timings,
// shaped identically to SrtCue so the import pipeline can be shared.
// We parse only the [Events] section and only Dialogue: lines. The Format:
// header is read to find the Start / End / Text column indices (these vary
// between ASS files). ASS time format is H:MM:SS.cc (centiseconds).
// Inline override codes ({\\b1}, {\\an8}, …) and \\N line breaks are stripped
// so the imported text shows what the viewer sees, not the raw markup.
const TIME = /^(\d+):(\d{2}):(\d{2})[.,](\d{1,3})$/;
function timeToUs(s) {
    const m = TIME.exec(s.trim());
    if (!m)
        throw new Error(`Invalid ASS timestamp: ${s}`);
    const cs = m[4].padEnd(2, "0").slice(0, 2);
    const ms = parseInt(cs, 10) * 10;
    return (parseInt(m[1], 10) * 3600 + parseInt(m[2], 10) * 60 + parseInt(m[3], 10)) * 1_000_000 + ms * 1000;
}
function stripOverrides(raw) {
    // Drop ASS override blocks like {\\b1\\an8}; convert \\N and \\n to real newlines;
    // drop \\h (hard-space marker) → keep as space.
    return raw
        .replace(/\{[^}]*\}/g, "")
        .replace(/\\N/g, "\n")
        .replace(/\\n/g, "\n")
        .replace(/\\h/g, " ")
        .trim();
}
export function parseAss(content) {
    const lines = content.replace(/\r\n?/g, "\n").split("\n");
    let inEvents = false;
    let format = null;
    const cues = [];
    let idx = 0;
    for (const raw of lines) {
        const line = raw.trim();
        if (!line)
            continue;
        if (line.startsWith("[")) {
            inEvents = /^\[events\]/i.test(line);
            continue;
        }
        if (!inEvents)
            continue;
        if (/^Format\s*:/i.test(line)) {
            format = line
                .slice(line.indexOf(":") + 1)
                .split(",")
                .map((s) => s.trim().toLowerCase());
            continue;
        }
        if (!/^Dialogue\s*:/i.test(line))
            continue;
        if (!format) {
            // ASS spec says Format must precede Dialogue, but accept the common
            // default if missing: Layer, Start, End, Style, Name, MarginL, MarginR,
            // MarginV, Effect, Text.
            format = ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];
        }
        const rest = line.slice(line.indexOf(":") + 1).trim();
        const startCol = format.indexOf("start");
        const endCol = format.indexOf("end");
        const textCol = format.indexOf("text");
        const styleCol = format.indexOf("style");
        if (startCol < 0 || endCol < 0 || textCol < 0) {
            throw new Error(`ASS Format line missing required columns (start/end/text): ${format.join(",")}`);
        }
        // Split into format.length-1 columns, the LAST column (text) absorbs the
        // remaining commas — Dialogue text fields routinely contain commas.
        const parts = [];
        let i = 0;
        let cur = "";
        let col = 0;
        while (i < rest.length) {
            const ch = rest[i];
            if (ch === "," && col < format.length - 1) {
                parts.push(cur);
                cur = "";
                col++;
            }
            else {
                cur += ch;
            }
            i++;
        }
        parts.push(cur);
        if (parts.length < format.length) {
            // Malformed line — skip rather than throwing.
            continue;
        }
        const startUs = timeToUs(parts[startCol]);
        const endUs = timeToUs(parts[endCol]);
        if (endUs <= startUs)
            continue; // CapCut/JianYing won't render zero/neg cues
        const text = stripOverrides(parts[textCol]);
        if (!text)
            continue;
        idx++;
        cues.push({
            index: idx,
            startUs,
            endUs,
            text,
            style: styleCol >= 0 ? parts[styleCol].trim() : undefined,
        });
    }
    return cues;
}
