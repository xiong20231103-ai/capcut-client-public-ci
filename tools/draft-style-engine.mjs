import { createHash } from "node:crypto";
import {
  existsSync,
  readFileSync,
  readdirSync,
  statSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { validateCaptionMirrorPromotion } from "./caption-mirror-policy.mjs";
import { applyTextPreset, loadPresetFile } from "./capcut-cli-v0.15.0/dist/preset.js";
import {
  assertTargetsUnchangedOnDisk,
  commitDraftTargets,
} from "./capcut-cli-v0.15.0/dist/draft.js";
import {
  discoverDraftStore,
} from "./capcut-cli-v0.15.0/dist/store.js";

const ENGINE_VERSION = "1.0.0";
const UPSTREAM_VERSION = "0.15.0";
const STANDARD_FILES = ["draft_content.json", "draft_info.json", "draft_meta_info.json", "template-2.tmp"];
const STYLE_WRITE_FILES = new Set(["draft_content.json", "template-2.tmp"]);

function fail(message, details = {}) {
  process.stdout.write(JSON.stringify({ ok: false, engineVersion: ENGINE_VERSION, error: message, ...details }));
  process.exitCode = 1;
}

function sha(value) {
  return createHash("sha256").update(value).digest("hex");
}

function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${stable(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

function readRequest() {
  const requestPath = process.argv[2];
  if (!requestPath) throw new Error("Missing request JSON path");
  const request = JSON.parse(readFileSync(requestPath, "utf8").replace(/^\uFEFF/, ""));
  if (!["apply", "inspect", "reconcile"].includes(request.operation)) {
    throw new Error(`Unsupported operation: ${String(request.operation)}`);
  }
  if (!request.draftDirectory || !existsSync(request.draftDirectory)) {
    throw new Error(`Draft staging directory does not exist: ${String(request.draftDirectory)}`);
  }
  if (request.operation === "apply" && (!request.fontPath || !existsSync(request.fontPath))) {
    throw new Error(`ZY Modern font file does not exist: ${String(request.fontPath)}`);
  }
  return request;
}

function hasTimelineFile(directory) {
  return STANDARD_FILES.some((name) => existsSync(join(directory, name)));
}

function discoverGroups(draftDirectory) {
  const root = resolve(draftDirectory);
  const groups = [];
  if (hasTimelineFile(root)) groups.push(root);
  const timelines = join(root, "Timelines");
  if (existsSync(timelines)) {
    const pending = [timelines];
    while (pending.length > 0) {
      const current = pending.pop();
      for (const entry of readdirSync(current, { withFileTypes: true })) {
        if (!entry.isDirectory()) continue;
        const child = join(current, entry.name);
        if (hasTimelineFile(child)) groups.push(child);
        pending.push(child);
      }
    }
  }
  return [...new Set(groups)].sort();
}

function materialById(draft) {
  return new Map((draft.materials?.texts ?? []).map((material) => [material.id, material]));
}

function mainSubtitleSegments(draft) {
  const byId = materialById(draft);
  const result = [];
  for (const track of draft.tracks ?? []) {
    if (String(track.type).toLowerCase() !== "text") continue;
    for (const segment of track.segments ?? []) {
      const material = byId.get(segment.material_id);
      if (!material || String(material.type).toLowerCase() !== "subtitle") continue;
      result.push({ segment, material });
    }
  }
  if (result.length === 0) throw new Error("No main subtitle segments referenced by a text track");
  return result;
}

function embeddedText(material) {
  try {
    return JSON.parse(material.content ?? "{}").text ?? "";
  } catch {
    return "";
  }
}

function fingerprint(draft) {
  const subtitleSegments = mainSubtitleSegments(draft);
  const captions = subtitleSegments.map(({ segment, material }) => ({
    segmentId: segment.id,
    materialId: material.id,
    recognizeText: material.recognize_text ?? "",
    text: embeddedText(material),
    words: material.words ?? null,
    currentWords: material.current_words ?? null,
  }));
  const captionIds = new Set(subtitleSegments.map(({ material }) => material.id));
  const protectedTracks = structuredClone(draft.tracks ?? []);
  for (const track of protectedTracks) {
    for (const segment of track.segments ?? []) {
      if (!captionIds.has(segment.material_id)) continue;
      if (segment.clip?.transform && Object.hasOwn(segment.clip.transform, "y")) {
        delete segment.clip.transform.y;
      }
      if (segment.clip?.transform && Object.keys(segment.clip.transform).length === 0) {
        delete segment.clip.transform;
      }
      if (segment.clip && Object.keys(segment.clip).length === 0) delete segment.clip;
    }
  }
  return {
    captionCount: captions.length,
    captionIdentity: sha(stable(captions)),
    tracks: sha(stable(protectedTracks)),
    media: sha(stable({
      videos: draft.materials?.videos ?? [],
      audios: draft.materials?.audios ?? [],
      soundChannelMappings: draft.materials?.sound_channel_mappings ?? [],
      videoMute: draft.config?.video_mute ?? null,
    })),
  };
}

function assertVersion(draft, request) {
  const appVersion = String(draft.platform?.app_version ?? "");
  const schema = Number(draft.version ?? 0);
  const newVersion = String(draft.new_version ?? "");
  const profiles = Array.isArray(request.supportedProfiles) ? request.supportedProfiles : [];
  const matched = profiles.find((profile) =>
    appVersion === String(profile.appVersion ?? "") &&
    schema === Number(profile.schema ?? 0) &&
    (!profile.requireExactNewVersion || newVersion === String(profile.newVersion ?? "")));
  if (!matched) {
    const expected = profiles.map((profile) =>
      `${profile.appVersion}/${profile.schema}` +
      (profile.requireExactNewVersion ? `/${profile.newVersion}` : ""),
    ).join(", ");
    const error = new Error(
      `Unsupported draft version: app=${appVersion || "unknown"}, schema=${schema || "unknown"}, ` +
      `new_version=${newVersion || "unknown"}; allowed=${expected || "none"}`,
    );
    error.code = "CAPCUT_DRAFT_VERSION_UNSUPPORTED";
    throw error;
  }

  // Historical 9.1 fixtures remain supported. The newly pinned 9.2 profile
  // is intentionally stricter: sharing schema 360000 alone never authorizes
  // a write when the required 9.2 track/material shape is absent.
  if (String(matched.appVersion ?? "") === "9.2.0") {
    if (!draft.platform || !Array.isArray(draft.tracks) ||
        !draft.materials || !Array.isArray(draft.materials.texts) ||
        !Array.isArray(draft.materials.videos) || !Array.isArray(draft.materials.audios)) {
      const error = new Error(
        `Unsupported draft layout for app=${appVersion}, schema=${schema}`,
      );
      error.code = "CAPCUT_DRAFT_LAYOUT_UNSUPPORTED";
      throw error;
    }
    const subtitles = mainSubtitleSegments(draft);
    for (const { segment, material } of subtitles) {
      let content;
      try { content = JSON.parse(material.content ?? "{}"); } catch { content = null; }
      if (!segment.id || !segment.material_id || !material.id ||
          !content || !Array.isArray(content.styles) || content.styles.length === 0) {
        const error = new Error(
          `Unsupported subtitle layout for material ${String(material.id ?? "unknown")}`,
        );
        error.code = "CAPCUT_DRAFT_LAYOUT_UNSUPPORTED";
        throw error;
      }
    }
  }
  return { appVersion, schema, newVersion };
}

function runtimePreset(request) {
  const presetPath = request.presetPath
    ? resolve(request.presetPath)
    : resolve(dirname(fileURLToPath(import.meta.url)), "company-subtitle-0725.preset.json");
  const source = loadPresetFile(presetPath);
  const preset = structuredClone(source);
  delete preset.transform;
  delete preset.bubble;
  delete preset.text_ranges;
  delete preset.style.font_id;
  delete preset.style.font_name;
  delete preset.style.font_resource_id;
  preset.style.font_path = resolve(request.fontPath).replaceAll("\\", "/");
  return preset;
}

function patchVerifiedFields(material, fontPath) {
  material.font_title = "ZY Modern";
  material.font_size = 10;
  material.font_path = fontPath;
  material.text_color = "#000000";
  material.check_flag = 23;
  material.background_color = "#ffffff";
  material.background_alpha = 0.5;
  material.background_style = 1;
  material.background_round_radius = 0.4000000059604645;
  material.background_width = 0.2800000011920929;
  material.background_height = 0.2800000011920929;

  const content = JSON.parse(material.content);
  if (!Array.isArray(content.styles) || content.styles.length === 0) {
    throw new Error(`Subtitle ${material.id} has no embedded styles`);
  }
  for (const style of content.styles) {
    style.size = 10;
    style.useLetterColor = true;
    style.font = { ...(style.font ?? {}), path: fontPath };
    style.fill = {
      alpha: 1,
      content: {
        render_type: "solid",
        solid: { alpha: 1, color: [0, 0, 0] },
      },
    };
  }
  material.content = JSON.stringify(content);
}

function verifyStyle(draft, fontPath, captionTargetY) {
  const normalizedFont = fontPath.replaceAll("\\", "/").toLowerCase();
  for (const { segment, material } of mainSubtitleSegments(draft)) {
    if (Number(material.font_size) !== 10) throw new Error(`Subtitle ${material.id} font size was not persisted`);
    if (String(material.font_path ?? "").replaceAll("\\", "/").toLowerCase() !== normalizedFont) {
      throw new Error(`Subtitle ${material.id} font path was not persisted`);
    }
    if (Number(material.check_flag) !== 23) throw new Error(`Subtitle ${material.id} check_flag was not persisted`);
    if (String(material.text_color).toLowerCase() !== "#000000") {
      throw new Error(`Subtitle ${material.id} text color was not persisted`);
    }
    if (String(material.background_color).toLowerCase() !== "#ffffff" ||
        Number(material.background_alpha) !== 0.5 ||
        Number(material.background_style) !== 1) {
      throw new Error(`Subtitle ${material.id} background style was not persisted`);
    }
    const content = JSON.parse(material.content);
    for (const style of content.styles ?? []) {
      const embeddedFont = String(style.font?.path ?? "").replaceAll("\\", "/").toLowerCase();
      const color = style.fill?.content?.solid?.color;
      if (Number(style.size) !== 10 || style.useLetterColor !== true || embeddedFont !== normalizedFont ||
          !Array.isArray(color) || color.length !== 3 || color.some((value) => Number(value) !== 0)) {
        throw new Error(`Subtitle ${material.id} embedded style was not persisted`);
      }
    }
    if (Number.isFinite(captionTargetY)) {
      const actualY = Number(segment.clip?.transform?.y);
      if (!Number.isFinite(actualY) || Math.abs(actualY - captionTargetY) > 0.001) {
        throw new Error(`Subtitle ${material.id} target position was not persisted`);
      }
    }
  }
}

function assertInvariant(before, after, group) {
  for (const key of ["captionCount", "captionIdentity", "tracks", "media"]) {
    if (before[key] !== after[key]) throw new Error(`Protected draft data changed (${key}) in ${group}`);
  }
}

function applyTarget(target, request, preset) {
  const draft = structuredClone(target.draft);
  assertVersion(draft, request);
  const before = fingerprint(draft);
  const subtitles = mainSubtitleSegments(draft);
  for (const { segment, material } of subtitles) {
    applyTextPreset(draft, segment.id, preset);
    patchVerifiedFields(material, preset.style.font_path);
    if (Number.isFinite(Number(request.captionTargetY))) {
      segment.clip = { ...(segment.clip ?? {}) };
      segment.clip.transform = {
        ...(segment.clip.transform ?? {}),
        y: Number(request.captionTargetY),
      };
    }
  }
  assertTargetsUnchangedOnDisk([target]);
  commitDraftTargets([target], draft, { backup: true });
  const persisted = discoverDraftStore(target.path).canonical.draft;
  const after = fingerprint(persisted);
  assertInvariant(before, after, target.path);
  verifyStyle(persisted, preset.style.font_path, Number(request.captionTargetY));
  return {
    path: target.path,
    captionCount: subtitles.length,
  };
}

function applyGroup(group, request, preset) {
  const store = discoverDraftStore(group);
  const writeTargets = store.targets.filter((target) => STYLE_WRITE_FILES.has(target.name));
  if (writeTargets.length === 0) throw new Error(`No approved subtitle-style targets in ${group}`);
  const targetResults = writeTargets.map((target) => applyTarget(target, request, preset));
  const captionCounts = new Set(targetResults.map((result) => result.captionCount));
  if (captionCounts.size !== 1) {
    throw new Error(`Subtitle counts differ across draft mirrors in ${group}`);
  }
  return {
    group,
    canonical: store.canonical.path,
    captionCount: targetResults[0].captionCount,
    files: targetResults.map((result) => result.path),
  };
}

function inspectGroup(group, request) {
  const store = discoverDraftStore(group);
  const canonical = store.canonical;
  const version = assertVersion(canonical.draft, request);
  const current = fingerprint(canonical.draft);
  return {
    group,
    canonical: canonical.path,
    captionCount: current.captionCount,
    captionIdentityHash: current.captionIdentity,
    timingAndTrackHash: current.tracks,
    protectedMediaHash: current.media,
    appVersion: version.appVersion,
    schemaVersion: version.schema,
    newVersion: version.newVersion,
    files: store.targets
      .filter((target) => STYLE_WRITE_FILES.has(target.name))
      .map((target) => target.path)
      .sort(),
  };
}

try {
  const request = readRequest();
  if (request.operation === "reconcile") {
    const root = resolve(request.draftDirectory);
    const projectPath = join(root, "Timelines", "project.json");
    let promoted = false;
    if (existsSync(projectPath) && existsSync(join(root, "template-2.tmp"))) {
      const project = JSON.parse(readFileSync(projectPath, "utf8").replace(/^\uFEFF/, ""));
      const id = project.main_timeline_id;
      if (!id || /[\\/]/.test(id)) throw new Error("CAPCUT_CAPTION_MIRROR_CONFLICT: invalid active timeline");
      const read = path => discoverDraftStore(path).canonical;
      const current = read(join(root, "draft_content.json"));
      const stale = read(join(root, "template-2.tmp"));
      const active = read(join(root, "Timelines", id, "draft_content.json"));
      const cache = read(join(root, "Timelines", id, "template-2.tmp"));
      if (validateCaptionMirrorPromotion(current.draft, stale.draft, active.draft, cache.draft, id)) {
        assertTargetsUnchangedOnDisk([current, stale, active, cache]);
        commitDraftTargets([stale], current.draft, { backup: true });
        promoted = true;
      }
    }
    process.stdout.write(JSON.stringify({ok:true, engineVersion:ENGINE_VERSION, groups:[], captionCount:0, promoted}));
  } else {
  const groups = discoverGroups(request.draftDirectory);
  if (groups.length === 0) throw new Error("No readable draft groups were found");
  if (request.operation === "inspect") {
    const results = groups.map((group) => inspectGroup(group, request));
    process.stdout.write(JSON.stringify({
      ok: true,
      engineVersion: ENGINE_VERSION,
      upstreamVersion: UPSTREAM_VERSION,
      groups: results,
      captionCount: results[0].captionCount,
    }));
  } else {
  const preset = runtimePreset(request);
  const results = groups.map((group) => applyGroup(group, request, preset));
  process.stdout.write(JSON.stringify({
    ok: true,
    engineVersion: ENGINE_VERSION,
    upstreamVersion: UPSTREAM_VERSION,
    groups: results,
    captionCount: results[0].captionCount,
  }));
  }
  }
} catch (error) {
  fail(error instanceof Error ? error.message : String(error), {
    errorCode: error && typeof error === "object" && "code" in error
      ? String(error.code)
      : "CAPCUT_DRAFT_STYLE_ENGINE_FAILED",
  });
}
