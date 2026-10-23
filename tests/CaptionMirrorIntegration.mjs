import {mkdtempSync,cpSync,readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
const source=resolve(process.argv[2]), font=resolve(process.argv[3]);
const parent=resolve('bin/data/caption-mirror-tests');mkdirSync(parent,{recursive:true});
const run=mkdtempSync(join(parent,'run-')),draft=join(run,'draft');
const hash=p=>createHash('sha256').update(readFileSync(p)).digest('hex');
const original=hash(join(source,'template-2.tmp'));
cpSync(source,draft,{recursive:true});
for(const operation of ['reconcile','inspect','apply','inspect']){
  const request={operation,draftDirectory:draft,fontPath:font,presetPath:resolve('tools/company-subtitle-0725.preset.json'),supportedProfiles:[{appVersion:'9.2.0',schema:360000,newVersion:'181.0.0',requireExactNewVersion:true}]};
  const file=join(run,'request.json');writeFileSync(file,JSON.stringify(request));
  const result=spawnSync(process.execPath,[resolve('tools/draft-style-engine.mjs'),file],{encoding:'utf8',timeout:30000});
  assert.equal(result.status,0,result.stdout+'\n'+result.stderr);
  const response=JSON.parse(result.stdout);assert.equal(response.ok,true);
  if(operation!=='reconcile')assert.equal(response.captionCount,10);
  console.log(operation+': passed');
}
assert.equal(hash(join(source,'template-2.tmp')),original,'Original archive changed');
console.log('Copied-draft integration passed; original unchanged. '+run);
