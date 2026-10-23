export type Namespace = "capcut" | "jianying";
export interface EnumEntry {
    member: string;
    slug: string;
    name?: string;
    title?: string;
    effect_id?: string;
    resource_id?: string;
    md5?: string;
    default_duration?: number;
    duration?: number;
    is_overlap?: boolean;
    is_vip?: boolean;
    resource_type?: string;
    default_aspect_ratio?: number;
}
export type Category = "transitions" | "masks" | "image_intros" | "image_outros" | "image_combos" | "text_intros" | "text_outros" | "text_loop_anims" | "scene_effects" | "character_effects" | "audio_effects" | "fonts" | "filters";
export declare function listEnum(category: Category, namespace?: Namespace): EnumEntry[];
/**
 * Look up an enum entry by slug or by raw Python member name.
 * Alias map lets legacy CLI slugs (e.g. "linear" -> "Split") stay backward compatible.
 */
export declare function findEnum(category: Category, slugOrMember: string, namespace?: Namespace, aliases?: Record<string, string>): EnumEntry | null;
export declare function slugsFor(category: Category, namespace?: Namespace): string[];
