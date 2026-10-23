// Only reconcile the pre-caption root cache when three independently saved
// current documents agree. Unknown differences remain a hard stop.
export function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.keys(value).sort().map(k => `${JSON.stringify(k)}:${stable(value[k])}`).join(',')}}`;
  return JSON.stringify(value);
}

function projection(value) {
  const d = structuredClone(value);
  d.tracks = (d.tracks ?? []).filter(t => t.type !== 'text');
  for (const t of d.tracks) for (const s of t.segments ?? []) delete s.track_render_index;
  delete d.extra_info;
  if (d.config) delete d.config.subtitle_taskinfo;
  if (d.materials) {
    delete d.materials.texts;
    delete d.materials.material_animations;
    for (const m of d.materials.sound_channel_mappings ?? []) {
      if (m.type === '' || m.type === 'none') m.type = 'none';
    }
  }
  return stable(d);
}

export function validateCaptionMirrorPromotion(current, stale, active, activeCache, activeId) {
  const block = message => { throw new Error(`CAPCUT_CAPTION_MIRROR_CONFLICT: ${message}`); };
  if (!activeId || current.id !== activeId || stale.id !== activeId || active.id !== activeId || activeCache.id !== activeId)
    block('timeline identity mismatch');
  if (stable(current) !== stable(active) || stable(current) !== stable(activeCache)) block('current mirrors disagree');
  if (stable(current) === stable(stale)) return false;
  if ((stale.tracks ?? []).some(t => t.type === 'text') || (stale.materials?.texts ?? []).length ||
      (stale.materials?.material_animations ?? []).length || stale.extra_info != null ||
      (stale.config?.subtitle_taskinfo ?? []).length) block('old cache is not a pre-caption snapshot');
  const texts = new Map((current.materials?.texts ?? []).map(t => [t.id, t]));
  const segments = (current.tracks ?? []).filter(t => t.type === 'text').flatMap(t => t.segments ?? []);
  if (!segments.length || segments.some(s => texts.get(s.material_id)?.type !== 'subtitle' || !(s.target_timerange?.duration > 0)))
    block('current captions are not verified subtitle segments');
  const references = new Set(segments.flatMap(s => s.extra_material_refs ?? []));
  if ((current.materials?.material_animations ?? []).some(m => !references.has(m.id))) block('unowned animation material');
  if (projection(current) !== projection(stale)) block('non-caption content changed');
  return true;
}
