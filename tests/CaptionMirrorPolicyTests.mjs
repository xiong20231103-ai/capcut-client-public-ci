import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { validateCaptionMirrorPromotion as validate } from '../tools/caption-mirror-policy.mjs';
const old = {id:'t',duration:100,config:{},tracks:[{type:'video',segments:[{id:'v',volume:0,track_render_index:0}]}],materials:{videos:[{id:'v',path:'source.mp4'}],texts:[],material_animations:[]}};
const current = structuredClone(old);
current.materials.texts=[{id:'c',type:'subtitle'}];
current.tracks.push({type:'text',segments:[{material_id:'c',target_timerange:{start:0,duration:100}}]});
assert.equal(validate(current,old,current,current,'t'),true);
assert.equal(validate(current,current,current,current,'t'),false);
for (const mutate of [d=>d.id='other',d=>d.materials.videos[0].path='other.mp4',d=>d.tracks[0].segments[0].volume=1,d=>d.unknown='x',d=>d.materials.texts.push({id:'unknown'})]) {
  const bad=structuredClone(old);mutate(bad);
  assert.throws(()=>validate(current,bad,current,current,'t'),/CAPCUT_CAPTION_MIRROR_CONFLICT/);
}
assert.throws(()=>validate(current,old,old,current,'t'),/disagree/);
if (process.argv[2]) {
  const root=process.argv[2], read=p=>JSON.parse(readFileSync(p,'utf8').replace(/^\uFEFF/,''));
  const id=read(join(root,'Timelines','project.json')).main_timeline_id;
  assert.equal(validate(read(join(root,'draft_content.json')),read(join(root,'template-2.tmp')),read(join(root,'Timelines',id,'draft_content.json')),read(join(root,'Timelines',id,'template-2.tmp')),id),true);
}
console.log('Caption mirror policy: passed (including conflict rejection).');
