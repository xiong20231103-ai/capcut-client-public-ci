import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
let cache = null;
function load() {
    if (cache)
        return cache;
    const here = dirname(fileURLToPath(import.meta.url));
    const raw = readFileSync(resolve(here, "enums.json"), "utf-8");
    cache = JSON.parse(raw);
    return cache;
}
export function listEnum(category, namespace = "capcut") {
    const ns = load()[namespace];
    return ns[category] ?? [];
}
/**
 * Look up an enum entry by slug or by raw Python member name.
 * Alias map lets legacy CLI slugs (e.g. "linear" -> "Split") stay backward compatible.
 */
export function findEnum(category, slugOrMember, namespace = "capcut", aliases) {
    const want = (aliases?.[slugOrMember] ?? slugOrMember).toLowerCase();
    for (const e of listEnum(category, namespace)) {
        if (e.slug && e.slug.toLowerCase() === want)
            return e;
        if (e.member.toLowerCase() === want)
            return e;
    }
    return null;
}
export function slugsFor(category, namespace = "capcut") {
    return listEnum(category, namespace)
        .map((e) => e.slug)
        .filter((s) => s.length > 0);
}
