// Reproducible, additive schema closure. Only the cached Wilds dump whose CRCs
// match the installed game's representative PFB files is accepted.
import fs from 'node:fs';
import path from 'node:path';
const root=process.argv[2]||process.cwd();
const fixture=path.join(root,'tools/LiveEditor/test-output/pfb-editor-fixtures');
const target=path.join(root,'Assets/Lists/rsz_pfb_classes.json');
const current=JSON.parse(fs.readFileSync(target,'utf8'));
const source=JSON.parse(fs.readFileSync(path.join(root,'tools/LiveEditor/test-output/rszmhwilds-reasy.json'),'utf8'));
const names=new Map(Object.entries(source).map(([id,c])=>[c.name,id]));
const seeds=new Set(['via.effect.script.ObjectEffectManager2','via.effect.script.ObjectEffectManager2.ExternalDataContainer',
 'via.motion.Chain2','via.motion.Chain','via.motion.JointConstraints','via.motion.JointConstraintsLayer',
 'via.character.CollisionShapePreset','via.character.CollisionShapePresetInfo'].map(x=>names.get(x)));
const paths=JSON.parse(fs.readFileSync(path.join(fixture,'representative-paths.json'),'utf8'));
const files=new Set(paths.map(p=>path.basename(p)+'.18'));
files.add('ch03_000_0002.pfb.18');
for(const file of files){
 const b=fs.readFileSync(path.join(fixture,file)), r=Number(b.readBigInt64LE(48)), o=r+Number(b.readBigInt64LE(r+24)), n=b.readInt32LE(r+12);
 for(let i=1;i<n;i++){
  const id=b.readUInt32LE(o+i*8).toString(16),crc=b.readUInt32LE(o+i*8+4);
  if(parseInt(source[id]?.crc,16)!==crc)throw Error(`CRC mismatch ${file}: ${id}`);
  seeds.add(id);
 }
}
const visited=new Set(),queue=[...seeds];
while(queue.length){
 const id=queue.shift(); if(visited.has(id))continue;visited.add(id);
 const c=source[id];if(!c)throw Error(`Missing type ${id}`);
 if(!current[id])current[id]=c;
 else if(parseInt(current[id].crc,16)!==parseInt(c.crc,16))throw Error(`Existing schema CRC conflict ${id}`);
 for(const f of c.fields){
  const dep=names.get(f.original_type);
  if(dep&&['Object','Struct'].includes(f.type))queue.push(dep);
  else if(dep&&f.type==='UserData'&&!current[dep])current[dep]=source[dep];
 }
}
fs.writeFileSync(target,JSON.stringify(current,null,2)+'\n');
console.log(JSON.stringify({files:files.size,classes:Object.keys(current).length,closure:visited.size,bytes:fs.statSync(target).size}));
