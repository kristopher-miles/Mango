const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname,'mango-questionnaire.html'),'utf8');
const logic = html.match(/<script id="logic">([\s\S]*?)<\/script>/)[1];
const context = vm.createContext({});
vm.runInContext(logic,context);
const run = expression => JSON.parse(JSON.stringify(vm.runInContext(expression,context)));
const blank = run('makeExport({}, "", "test-time")');
assert.equal(blank.responses.length,29);
assert(blank.responses.every(r => r.status === 'unanswered' && r.choice === null && r.notes === ''));
const mixed = run('makeExport({Q01:{choice:"recommended",notes:""}, Q02:{choice:"alternative_0",notes:"Keep authentication"}, Q03:{choice:"",notes:"A custom answer\\nwith Unicode: 🥭"}, Q04:{choice:"custom",notes:""}, Q05:{choice:"deferred",notes:"Review later"}, Q06:{choice:"recommended",notes:"Change retention"}}, "Overall notes")');
assert.deepEqual(mixed.responses.slice(0,6).map(r=>r.status),['recommended','alternative','written_answer','needs_detail','deferred','recommended_with_notes']);
context.input = mixed;
const restored = run('readExport(input)');
context.restored = restored;
const roundtrip = run('makeExport(restored.answers,restored.overall_notes)');
assert.deepEqual(roundtrip.responses,mixed.responses);
assert.equal(roundtrip.overall_notes,'Overall notes');
for (const change of [v=>v.schema='wrong',v=>v.baseline_sha256='wrong',v=>v.responses.pop(),v=>v.responses[1].id='Q01',v=>v.responses[0].choice='alternative_99',v=>v.responses[0].notes=null]) {
  const damaged = structuredClone(blank); change(damaged); context.damaged = damaged;
  assert.throws(()=>vm.runInContext('readExport(damaged)',context));
}
const submitted = JSON.parse(fs.readFileSync(path.join(__dirname,'decisions/2026-11-11-answers.json'),'utf8').replace(/^\uFEFF/,''));
assert.deepEqual(blank.source_documents,submitted.source_documents);
context.submitted = submitted;
assert.equal(Object.keys(run('readExport(submitted)').answers).length,29);
assert.equal((html.match(/<select id="choice-Q/g)||[]).length,29);
assert.equal((html.match(/<textarea id="notes-Q/g)||[]).length,29);
assert(!/<option\b[^>]*\sselected(?:\s|=|>)/.test(html));
assert(!/<script[^>]+src=/.test(html));
assert(!/\brequired\b/.test(html.match(/<select[\s\S]*?<\/select>/)[0]));
new vm.Script(html.match(/<script id="ui">([\s\S]*?)<\/script>/)[1]);
console.log('PASS: blank/partial exports, answer statuses, Unicode round trip, invalid imports, all source text preserved, 29 unselected controls, and script syntax.');

// Exercise UI wiring without a browser; visual layout still needs browser review.
const elements = new Map();
for (const match of html.matchAll(/\bid="([^"]+)"/g)) {
  elements.set(match[1],{value:'',textContent:'',addEventListener(name,fn){this[name]=fn;},showModal(){this.open=true;},close(){this.open=false;},focus(){},select(){}});
}
const storage = new Map();
let exportedBlob;
Object.assign(context,{
  document:{getElementById:id=>elements.get(id),querySelectorAll:()=>[...elements].filter(([id])=>id.startsWith('choice-')||id.startsWith('notes-')||id==='global-notes').map(([,el])=>el),
    createElement:()=>({click(){},remove(){}}),body:{append(){}}},
  localStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value)},
  window:{addEventListener(){}}, URL:{createObjectURL(blob){exportedBlob=blob;return 'blob:test';},revokeObjectURL(){}},
  Blob, setTimeout:()=>0, navigator:{clipboard:{writeText:async()=>{}}}, confirm:()=>true, alert:message=>{throw Error(message);}
});
vm.runInContext(html.match(/<script id="ui">([\s\S]*?)<\/script>/)[1],context);
elements.get('download').onclick();
(async()=>{
  assert(JSON.parse(await exportedBlob.text()).responses.every(r=>r.status==='unanswered'));
  elements.get('choice-Q01').value='recommended'; elements.get('choice-Q01').input();
  elements.get('notes-Q02').value='A written answer'; elements.get('notes-Q02').input();
  assert.match(elements.get('progress').textContent,/2 of 29/);
  elements.get('show-json').onclick();
  assert.equal(elements.get('export-dialog').open,true);
  assert.equal(JSON.parse(elements.get('export-text').value).responses[1].status,'written_answer');
  const imported=JSON.stringify(mixed);
  await elements.get('import-file').onchange({target:{files:[{size:imported.length,text:async()=>imported}],value:'test.json'}});
  assert.equal(elements.get('notes-Q03').value,mixed.responses[2].notes);
  assert.equal(elements.get('choice-Q05').value,'deferred');
  assert.equal(storage.size,1);
  console.log('PASS: UI wiring for blank download, choice/text entry, progress, JSON dialog, draft save, and imported answer restoration.');
})().catch(error=>{console.error(error);process.exitCode=1;});
