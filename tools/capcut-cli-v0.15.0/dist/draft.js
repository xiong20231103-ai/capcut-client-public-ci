import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readdirSync, readFileSync, renameSync, rmSync, unlinkSync, writeFileSync, writeSync, } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { stripBom } from "./bom.js";
import { discoverDraftStore, editorProcesses, isManagedDraftPath, serializeDraftCandidate, } from "./store.js";
import { assessWriteSafety } from "./version.js";
export function findDraft(input) {
    return discoverDraftStore(input).canonical.path;
}
const loadContexts = new Map();
export function loadDraft(path) {
    const store = discoverDraftStore(path);
    const filePath = store.canonical.path;
    const draft = structuredClone(store.canonical.draft);
    loadContexts.set(resolve(filePath), { store });
    return { draft, filePath };
}
// Canonical bottom->top layer order CapCut expects in the tracks array.
// Derived from a real CapCut-authored draft: [video, audio, text].
// Tracks are pushed in command-call order as content is added, so without
// this normalization the array order (which drives CapCut's timeline layout)
// ends up scrambled. Unknown types are kept after the known ones.
const TRACK_RANK = {
    video: 0,
    audio: 1,
    sticker: 2,
    effect: 3,
    filter: 4,
    text: 5,
};
// Sort tracks into the canonical layer order. Stable: tracks of the same type
// keep their authored order (tiebreak on original index).
export function sortTracks(draft) {
    draft.tracks = draft.tracks
        .map((track, index) => ({ track, index }))
        .sort((a, b) => (TRACK_RANK[a.track.type] ?? 99) - (TRACK_RANK[b.track.type] ?? 99) || a.index - b.index)
        .map(({ track }) => track);
}
// --dry-run state. When set, saveDraft computes the in-memory change but skips
// both the on-disk write and the .bak snapshot, so the draft is left untouched.
// All ~35 mutating commands funnel through saveDraft, so gating here covers them.
let dryRun = false;
let forceWrite = false;
export function setDryRun(value) {
    dryRun = value;
}
export function isDryRun() {
    return dryRun;
}
export function setForceWrite(value) {
    forceWrite = value;
}
// Multi-step undo history. Alongside the single `.bak`, every write also keeps
// a rolling stack of the pre-write content under `<draftdir>/.capcut-cli-history/`,
// capped at HISTORY_MAX. `restore --step N` rolls back N writes; CapCut ignores
// the hidden dir. snapshots are named `<draftbase>.NNNNNN.snap` (zero-padded,
// monotonically increasing) so the newest is the lexicographically last.
const HISTORY_DIR = ".capcut-cli-history";
const HISTORY_MAX = 20;
function historyDir(filePath) {
    return join(dirname(filePath), HISTORY_DIR);
}
function snapshotFiles(filePath) {
    const dir = historyDir(filePath);
    if (!existsSync(dir))
        return [];
    const prefix = `${basename(filePath)}.`;
    return readdirSync(dir)
        .filter((f) => f.startsWith(prefix) && f.endsWith(".snap"))
        .sort(); // zero-padded indices => lexicographic === numeric order, oldest first
}
function writeHistorySnapshot(filePath, content) {
    const dir = historyDir(filePath);
    if (!existsSync(dir))
        mkdirSync(dir, { recursive: true });
    const existing = snapshotFiles(filePath);
    const last = existing[existing.length - 1];
    const lastIndex = last ? Number.parseInt(last.match(/\.(\d+)\.snap$/)?.[1] ?? "0", 10) : 0;
    const name = `${basename(filePath)}.${String(lastIndex + 1).padStart(6, "0")}.snap`;
    writeFileSync(join(dir, name), content, "utf-8");
    // Trim oldest beyond the cap.
    const all = snapshotFiles(filePath);
    while (all.length > HISTORY_MAX) {
        const oldest = all.shift();
        if (oldest)
            rmSync(join(dir, oldest));
    }
}
// Snapshots newest-first, step 1 = most recent write (equivalent to `.bak`).
export function listSnapshots(filePath) {
    const dir = historyDir(filePath);
    return snapshotFiles(filePath)
        .map((f) => ({ index: Number.parseInt(f.match(/\.(\d+)\.snap$/)?.[1] ?? "0", 10), path: join(dir, f) }))
        .sort((a, b) => b.index - a.index)
        .map((s, i) => ({ step: i + 1, index: s.index, path: s.path }));
}
// Optimistic concurrency check: never silently overwrite a file changed by
// CapCut, another CLI process, or a sync client since the candidates were read.
export function assertTargetsUnchangedOnDisk(targets) {
    for (const target of targets) {
        if (!target.exists || target.raw === null)
            continue;
        // `target.raw` was BOM-stripped at load; strip here too so a BOM'd file
        // that is otherwise untouched does not read as a concurrent change.
        const current = stripBom(readFileSync(target.path, "utf-8"));
        if (current !== target.raw) {
            throw new Error(`Draft changed on disk after it was loaded: ${target.name}. ` +
                "Reload and retry, or pass --force-write to overwrite intentionally.");
        }
    }
}
// Transactional multi-target write: serialize `draft` into each target's own
// envelope, then temp+fsync+rename with a `.bak` (and history snapshot) per
// file actually written, rolling back on a partial commit. Writes EXACTLY the
// given targets — callers decide the write set (saveDraft: every readable
// sibling; sync-timelines: only the drifted mirrors). No-ops under --dry-run.
export function commitDraftTargets(targets, draft, options = {}) {
    if (dryRun)
        return;
    // Prepare every replacement before renaming any target. This keeps the
    // multi-file write as close to a transaction as the filesystem allows.
    const prepared = targets.map((target, index) => {
        const content = serializeDraftCandidate(target, draft);
        const temp = `${target.path}.capcut-cli-${process.pid}-${Date.now()}-${index}.tmp`;
        writeAndSync(temp, content);
        return { target, temp, content };
    });
    const committed = [];
    try {
        for (const item of prepared) {
            if (options.backup !== false && item.target.raw !== null) {
                writeAtomic(`${item.target.path}.bak`, item.target.raw);
                writeHistorySnapshot(item.target.path, item.target.raw);
            }
        }
        for (const item of prepared) {
            renameSync(item.temp, item.target.path);
            committed.push(item);
        }
    }
    catch (error) {
        // Roll back targets already renamed during a partial commit.
        for (const item of committed.reverse()) {
            if (item.target.raw !== null)
                writeAtomic(item.target.path, item.target.raw);
        }
        for (const item of prepared) {
            if (existsSync(item.temp))
                unlinkSync(item.temp);
        }
        throw error;
    }
}
export function saveDraft(filePath, draft, options = {}) {
    if (dryRun) {
        // Version guard, warning only: dry-run writes nothing, so it never blocks,
        // but the WARNING still previews what a real write would do. Draft-only
        // assessment (no store discovery) keeps dry-run free of extra I/O.
        if (options.skipVersionGuard !== true) {
            const safety = assessWriteSafety(draft, null);
            if (safety.action !== "ok")
                process.stderr.write(`WARNING: ${safety.reasons.join(" ")}\n`);
        }
        // Normalize in memory (so any read-back is consistent) but write nothing.
        sortTracks(draft);
        return;
    }
    const resolved = resolve(filePath);
    const context = loadContexts.get(resolved) ?? { store: discoverDraftStore(filePath) };
    const { store } = context;
    if (!forceWrite && isManagedDraftPath(filePath)) {
        const running = editorProcesses();
        if (running.length > 0) {
            throw new Error(`${running.join(" / ")} is running. Close the editor before writing this managed draft, ` +
                "or pass --force-write if you accept that the app may overwrite the change.");
        }
    }
    // Write-time version boundary: refuse when the draft's detected version or
    // schema generation is beyond the registry's evidence or known-broken (new
    // app builds are reported to reject tool-written drafts as corrupted).
    // store.version covers every readable sibling, so a mirror written by a
    // newer app build trips the guard too. Markerless drafts (capcut create
    // output) never trigger. --force-write overrides, but the WARNING still
    // lands on stderr so a forced write is never silent.
    // skipVersionGuard is for `restore`'s mirror re-sync ONLY: restoring a
    // backup is the escape hatch, not the hazard (docs/version-support.md), and
    // a mid-restore refusal would leave the canonical rolled back but the
    // mirrors diverged.
    if (options.skipVersionGuard !== true) {
        const safety = assessWriteSafety(draft, store.version);
        if (safety.action === "refuse" && !forceWrite)
            throw new Error(safety.reasons.join("\n"));
        if (safety.action === "warn" || (safety.action === "refuse" && forceWrite)) {
            process.stderr.write(`WARNING: ${safety.reasons.join(" ")}\n`);
        }
    }
    if (!forceWrite)
        assertTargetsUnchangedOnDisk(store.targets);
    sortTracks(draft);
    commitDraftTargets(store.targets, draft, options);
    // Refresh hashes/raw snapshots so a library caller can save the same loaded
    // draft more than once without tripping its own conflict guard.
    // Rediscover from the canonical file, not only the parent directory. The
    // latter would lose explicitly addressed custom filenames such as A.json.
    loadContexts.set(resolved, { store: discoverDraftStore(store.canonical.path) });
}
function writeAndSync(path, content) {
    const fd = openSync(path, "w", 0o600);
    try {
        writeSync(fd, content, undefined, "utf-8");
        fsyncSync(fd);
    }
    finally {
        closeSync(fd);
    }
}
// Exported for factory.ts (register): the same temp+fsync+rename write the
// draft save path uses, for metadata files outside commitDraftTargets.
export function writeAtomic(path, content) {
    const temp = `${path}.capcut-cli-${process.pid}-${Date.now()}.tmp`;
    writeAndSync(temp, content);
    renameSync(temp, path);
}
export function extractText(content) {
    try {
        const parsed = JSON.parse(content);
        if (parsed.text)
            return parsed.text;
    }
    catch {
        return content
            .replace(/<[^>]*>/g, "")
            .replace(/\[|\]/g, "")
            .trim();
    }
    return content;
}
// Style `range` entries are UTF-16LE byte offsets (see setTextRanges).
export function extractStyleRanges(content) {
    try {
        const parsed = JSON.parse(content);
        if (!Array.isArray(parsed.styles))
            return [];
        const ranges = [];
        for (const style of parsed.styles) {
            const r = style.range;
            if (Array.isArray(r) && r.length === 2 && typeof r[0] === "number" && typeof r[1] === "number") {
                ranges.push([r[0], r[1]]);
            }
        }
        return ranges;
    }
    catch {
        return [];
    }
}
export function updateTextContent(content, newText) {
    try {
        const parsed = JSON.parse(content);
        if (parsed.text !== undefined) {
            parsed.text = newText;
            if (parsed.styles && parsed.styles.length > 0) {
                const encoded = Buffer.from(newText, "utf16le");
                parsed.styles[0].range = [0, encoded.length];
            }
            return JSON.stringify(parsed);
        }
    }
    catch {
        const match = content.match(/^(.*\])?(.*?)(\[.*)?$/s);
        if (match) {
            return content.replace(/\[[^\]]*\]/, `[${newText}]`);
        }
    }
    return newText;
}
export function findSegment(draft, id) {
    const shortId = id.toLowerCase();
    for (const track of draft.tracks) {
        for (let i = 0; i < track.segments.length; i++) {
            const seg = track.segments[i];
            if (seg.id === id || seg.id.toLowerCase().startsWith(shortId)) {
                return { track, segment: seg, index: i };
            }
        }
    }
    return null;
}
export function findMaterial(arr, id) {
    return arr.find((m) => m.id === id);
}
export function getTracksByType(draft, type) {
    return draft.tracks.filter((t) => t.type === type);
}
export function getMaterialTypes(draft) {
    return Object.entries(draft.materials)
        .filter(([, v]) => Array.isArray(v))
        .map(([type, arr]) => ({ type, count: arr.length }))
        .sort((a, b) => b.count - a.count);
}
export function findMaterialGlobal(draft, id) {
    const shortId = id.toLowerCase();
    for (const [type, arr] of Object.entries(draft.materials)) {
        if (!Array.isArray(arr))
            continue;
        for (const mat of arr) {
            if (mat && typeof mat === "object" && typeof mat.id === "string") {
                const m = mat;
                const matId = m.id;
                if (matId === id || matId.toLowerCase().startsWith(shortId)) {
                    return { type, material: m };
                }
            }
        }
    }
    return null;
}
