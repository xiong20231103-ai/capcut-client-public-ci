/**
 * Migrate a draft's schema across known version jumps.
 *
 * Currently implements:
 *   - mask -> common_masks (JianYing 5.9 -> 9.6+, CapCut older -> newer)
 *
 * Migrations are best-effort: if a field doesn't apply (e.g. no masks present),
 * we record it as skipped rather than failing. The draft is mutated in place.
 */
export function migrateDraft(draft, from, to) {
    const applied = [];
    const skipped = [];
    const warnings = [];
    const knownJump = isJumpAcrossMaskRename(from, to);
    if (knownJump.direction === "legacy-to-new") {
        const moved = migrateMaskToCommonMasks(draft);
        if (moved > 0)
            applied.push(`mask->common_masks (${moved} entries)`);
        else
            skipped.push("mask->common_masks (no `masks[]` entries to migrate)");
    }
    else if (knownJump.direction === "new-to-legacy") {
        const moved = migrateCommonMasksToMask(draft);
        if (moved > 0)
            applied.push(`common_masks->mask (${moved} entries)`);
        else
            skipped.push("common_masks->mask (no `common_masks[]` entries to migrate)");
    }
    else if (knownJump.direction === "none") {
        warnings.push(`No registered migration for ${from} -> ${to}. Only known migration so far: mask <-> common_masks across JianYing 5.9 / CapCut 9.6 boundary.`);
    }
    return { ok: true, from, to, applied, skipped, warnings };
}
function isJumpAcrossMaskRename(from, to) {
    const fromN = parseVer(from);
    const toN = parseVer(to);
    if (fromN === null || toN === null)
        return { direction: "none" };
    const boundary = 9.6;
    if (fromN < boundary && toN >= boundary)
        return { direction: "legacy-to-new" };
    if (fromN >= boundary && toN < boundary)
        return { direction: "new-to-legacy" };
    return { direction: "none" };
}
function parseVer(s) {
    const m = /^(\d+(?:\.\d+)?)/.exec(s);
    return m ? parseFloat(m[1]) : null;
}
function migrateMaskToCommonMasks(draft) {
    const masks = draft.materials.masks ?? [];
    if (masks.length === 0)
        return 0;
    if (!Array.isArray(draft.materials.common_masks))
        draft.materials.common_masks = [];
    const target = draft.materials.common_masks;
    const targetIds = new Set(target.map((m) => m.id));
    let moved = 0;
    for (const mat of masks) {
        if (typeof mat.id === "string" && targetIds.has(mat.id))
            continue;
        target.push(mat);
        moved++;
    }
    draft.materials.masks = [];
    return moved;
}
function migrateCommonMasksToMask(draft) {
    const cm = draft.materials.common_masks ?? [];
    if (cm.length === 0)
        return 0;
    if (!Array.isArray(draft.materials.masks))
        draft.materials.masks = [];
    const target = draft.materials.masks;
    const targetIds = new Set(target.map((m) => m.id));
    let moved = 0;
    for (const mat of cm) {
        if (typeof mat.id === "string" && targetIds.has(mat.id))
            continue;
        target.push(mat);
        moved++;
    }
    draft.materials.common_masks = [];
    return moved;
}
