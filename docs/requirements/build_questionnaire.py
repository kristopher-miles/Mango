"""Rebuild the standalone questionnaire from the unchanged requirements sources."""
from pathlib import Path
import hashlib
import html
import json
import re

ROOT = Path(__file__).resolve().parent
documents = json.loads((ROOT/'decisions/2026-11-11-answers.json').read_text(encoding='utf-8-sig'))['source_documents']
source = documents['08-open-decisions.md']
alternatives = [
    ['Narrow the launch platforms or scale; specify below'],
    ['Embed Tailscale from the first release'],
    ['Funnel only', 'Existing HTTPS reverse proxy only', 'Private tailnet only initially'],
    ['Enrolled peers can administer shared settings without a fresh master unlock'],
    ['Allow isolated administrative edits with eventual merging and visible conflicts'],
    ['Recovery always revokes peers and inference keys', 'Support recovery only with a surviving trusted peer or backup'],
    ['Use a longer bounded public revocation window; specify below', 'Prefer continued offline public access despite stale revocations'],
    ['Use a different key scope, priority, expiry, or limit policy; specify below'],
    ['Only manage servers Mango starts', 'Require lifecycle control for every contributing provider'],
    ['Change the initial provider or protocol coverage; specify below'],
    ['Allow provider-hosted tools with separate local permission and isolation'],
    ['Keep alias validation strictly modality-only'],
    ['Require full benchmarks before advertising any deployment'],
    ['Use different memory margins or overlap rules; specify below'],
    ['Use different activity triggers or cooldown; specify below', 'Manual contribution switching only initially'],
    ['Purge all lower-priority queued requests on the node'],
    ['Scarce placement outranks key priority'],
    ['Allow higher-priority arrivals to abort lower-priority active inference'],
    ['Require explicit session identifiers; no inferred affinity', 'Use different retention or cache-wait rules; specify below'],
    ['Allow explicit client opt-in to restarting partially completed requests'],
    ['Use different queue limits, deadlines, or error behavior; specify below'],
    ['Strict events, demand-driven checks, and daily refresh only; accept stale state'],
    ['Desktop GUI only initially', 'Authenticated browser GUI only'],
    ['Include response bodies in verbose logs', 'Default logging to off', 'Change logging policy or clarify tts; specify below'],
    ['Use different timezone, retention, or collection rules; specify below'],
    ['Coordinate simultaneous restarts', 'Notify and verify updates, but install manually per node'],
    ['Use a different supported protocol compatibility window; specify below'],
    ['Change the future best-routing policy; specify below', 'Document best routing but do not reserve its name yet'],
    ['Persist queued requests and resume them under an explicit recovery policy'],
]
questions = []
group = ''
for match in re.finditer(r'^(## .+|### Q\d+ .+)\n', source, re.M):
    heading = match.group(1)
    if heading.startswith('## '):
        group = heading[3:]
        continue
    qid, title = heading[4:].split(' ', 1)
    end = re.search(r'^##', source[match.end():], re.M)
    body = source[match.end():match.end() + end.start() if end else len(source)].strip()
    parts = body.split('\n\n')
    assert len(parts) >= 3 and parts[1].startswith('**Recommended:**'), qid
    questions.append(dict(id=qid, title=title, group=group, prompt=parts[0],
        recommendation=parts[1].removeprefix('**Recommended:**').strip(),
        context='\n\n'.join(parts[2:]), source_markdown=body,
        alternatives=alternatives[int(qid[1:])-1]))
assert [q['id'] for q in questions] == [f'Q{i:02}' for i in range(1,30)]
baseline_hash = hashlib.sha256(json.dumps(documents, sort_keys=True).encode()).hexdigest()
data = dict(baseline_date='2026-11-11', baseline_sha256=baseline_hash, questions=questions, documents=documents)

def inline(text):
    text = html.escape(text)
    text = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', text)
    return re.sub(r'`([^`]+)`', r'<code>\1</code>', text)

cards = []
nav = []
previous_group = None
for q in questions:
    qid = q['id']
    if q['group'] != previous_group:
        cards.append(f'<h2 class="group">{html.escape(q["group"])}</h2>')
        previous_group = q['group']
    options = '<option value="">Leave unanswered</option><option value="recommended">Accept the full recommendation</option>'
    options += ''.join(f'<option value="alternative_{i}">{html.escape(label)}</option>' for i,label in enumerate(q['alternatives']))
    options += '<option value="custom">My own answer / modify the recommendation</option><option value="deferred">Defer this decision</option>'
    context = ''.join('<p>'+inline(p)+'</p>' for p in q['context'].split('\n\n'))
    cards.append(f'''<section class="card" id="{qid}" aria-labelledby="title-{qid}">
<div class="eyebrow">{qid}</div><h3 id="title-{qid}">{html.escape(q['title'])}</h3>
<p class="prompt">{inline(q['prompt'])}</p>
<div class="recommendation"><strong>Recommended answer</strong><p>{inline(q['recommendation'])}</p></div>
<div class="context">{context}</div>
<label for="choice-{qid}">Your choice</label><select id="choice-{qid}" data-id="{qid}">{options}</select>
<label for="notes-{qid}">Your answer, changes, or additional details <span>(optional)</span></label>
<textarea id="notes-{qid}" data-id="{qid}" rows="3" placeholder="Specify values, explain an alternative, or write your full answer here."></textarea>
</section>''')
    nav.append(f'<a href="#{qid}">{qid} <span>{html.escape(q["title"])}</span></a>')
references = ''.join(f'<details><summary>{html.escape(name)}</summary><pre>{html.escape(text)}</pre></details>' for name,text in documents.items())
template = r'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light"><title>Mango · Requirements decisions</title>
<style>
:root{font:16px/1.6 system-ui,sans-serif;color:#233d36;background:#f6f5ef;--green:#194d3e;--line:#d8dfd5;--orange:#f4bf65}
*{box-sizing:border-box}body{margin:0}header{background:var(--green);color:#fff;padding:48px max(24px,calc((100vw - 1160px)/2)) 36px}h1{font-size:clamp(2rem,4vw,3.1rem);line-height:1.1;margin:10px 0 18px;letter-spacing:-.04em}header p{max-width:760px;color:#e0eae2}.eyebrow{font-size:.78rem;font-weight:750;letter-spacing:.12em;text-transform:uppercase}header .eyebrow{color:var(--orange)}.toolbar{position:sticky;top:0;z-index:2;background:#fffefa;border-bottom:1px solid var(--line);padding:12px 24px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}.toolbar-inner{max-width:1160px;margin:auto;width:100%;display:flex;align-items:center;gap:12px;flex-wrap:wrap}button,.file-button{font:inherit;cursor:pointer;border:1px solid var(--green);border-radius:7px;background:transparent;padding:9px 14px;color:var(--green)}button.primary{background:var(--green);color:#fff;font-weight:650}button:hover,.file-button:hover{filter:brightness(.9)}#progress{margin-right:auto;font-size:.9rem}.layout{max-width:1208px;margin:auto;padding:30px 24px;display:grid;grid-template-columns:230px minmax(0,1fr);gap:32px}nav{position:sticky;top:110px;max-height:calc(100vh - 130px);overflow:auto;align-self:start}nav a{display:block;color:var(--green);text-decoration:none;font-size:.78rem;padding:6px 8px;border-left:2px solid transparent}nav a:hover{background:#e9eee5;border-color:var(--green)}nav span{margin-left:6px}h2.group{font-size:1.2rem;margin:34px 0 16px}.card{background:white;border:1px solid var(--line);border-radius:12px;padding:26px;margin:0 0 22px;scroll-margin-top:150px}.card .eyebrow{color:#6a7569}h3{font-size:1.45rem;line-height:1.25;margin:6px 0 18px}.prompt{font-size:1.06rem}.recommendation{background:#eef4e9;border-left:3px solid #78a36d;padding:16px 18px;border-radius:4px}.recommendation p{margin:8px 0 0}.context{font-size:.94rem;color:#4f5c55}label{display:block;font-weight:650;margin:18px 0 7px}label span{font-weight:400;color:#617368}select,textarea{width:100%;font:inherit;color:inherit;border:1px solid #a8b8aa;border-radius:6px;padding:11px;background:#fff}select{white-space:normal}textarea{resize:vertical}button:focus-visible,a:focus-visible,select:focus-visible,textarea:focus-visible,summary:focus-visible{outline:3px solid #c97e12;outline-offset:3px}.note{font-size:.91rem;color:#4f5c55}#save-status{font-size:.85rem;margin:4px 0}details{background:#fff;border:1px solid var(--line);border-radius:8px;padding:14px;margin:12px 0}summary{cursor:pointer;font-weight:650}pre{white-space:pre-wrap;overflow-wrap:anywhere;font: .88rem/1.6 ui-monospace,monospace}.bottom{padding:20px 0 40px}dialog{max-width:800px;width:90%;border:1px solid var(--line);border-radius:12px;padding:24px}dialog::backdrop{background:#132c2470}#export-text{font:.8rem/1.4 monospace;height:300px}#global-notes{min-height:100px}a{color:var(--green)}code{font-size:.9em}noscript{display:block;padding:24px;background:#ffdfb0}
@media(max-width:800px){.layout{display:block;padding:18px 14px}nav{position:static;max-height:170px;margin-bottom:20px;border-bottom:1px solid var(--line)}header{padding:32px 20px}.toolbar{padding:10px 14px}.card{padding:20px}#progress{flex-basis:100%}.toolbar-inner{gap:8px}button,.file-button{font-size:.9rem}.card{scroll-margin-top:190px}}
</style></head><body>
<header><div class="eyebrow">Mango / Requirements review / 11 November 2026</div><h1>Your network. Your decisions.</h1><p>29 decisions to settle before implementation planning. Every question includes the original recommendation and its tradeoffs. Choose an answer, add your own details, or leave it blank.</p></header>
<noscript>This questionnaire needs JavaScript to save and export answers. The questions and source documents below remain readable.</noscript>
<div class="toolbar"><div class="toolbar-inner"><div id="progress" role="status" aria-live="polite">0 of 29 responses recorded</div><button class="primary" id="download">Download answers JSON</button><button id="show-json">View / copy JSON</button><button id="import-button">Resume from JSON</button><input id="import-file" type="file" accept=".json,application/json" hidden></div></div>
<div class="layout"><nav aria-label="Questions">@@NAV@@<a href="#reference">Full source documents</a></nav><main>
<p class="note">Nothing is selected automatically. “Accept the full recommendation” accepts the whole recommendation; notes can qualify it. Alternative choices resolve only the stated change, so explain any other preferences below. Written answers work without choosing an option.</p>
<p id="save-status" role="status" aria-live="polite">Checking local draft storage…</p><p class="note">Download JSON to keep a portable copy and send that single file back. Blank answers are exported explicitly as unanswered. No information is sent to a server.</p>
@@CARDS@@
<section class="card"><h2>Anything else?</h2><label for="global-notes">Overall changes, priorities, or questions</label><textarea id="global-notes" rows="4"></textarea></section>
<section id="reference"><h2>Full requirements reference</h2><p class="note">The original Markdown sources are embedded in full, including requirements, acceptance scenarios, research links, and the question set. They are also included in your JSON export so it is self-contained.</p>@@REFERENCES@@</section>
<div class="bottom"><button class="primary" id="download-bottom">Download answers JSON</button><p class="note">Partial answers are welcome. Exporting does not finalize or approve unanswered decisions.</p></div>
</main></div>
<dialog id="export-dialog" aria-labelledby="export-title"><h2 id="export-title">Your answers as JSON</h2><p>Same format as the download. Copy all of this text if downloading is unavailable.</p><label for="export-text">Export contents</label><textarea id="export-text" readonly></textarea><p><button id="copy">Copy JSON</button> <button id="close-dialog">Close</button></p><p id="copy-status" role="status"></p></dialog>
<script id="logic">
const DATA = @@DATA@@;
const SCHEMA = 'mango.requirements.answers/v1';
function choiceLabel(q, value) {
  if (!value) return null;
  if (value === 'recommended') return 'Accept the full recommendation';
  if (value === 'custom') return 'My own answer / modify the recommendation';
  if (value === 'deferred') return 'Defer this decision';
  const index = q.alternatives.findIndex((_, i) => value === 'alternative_' + i);
  if (index < 0) throw new Error('Unknown choice for ' + q.id);
  return q.alternatives[index];
}
function responseStatus(choice, notes) {
  if (choice === 'deferred') return 'deferred';
  if (choice === 'recommended') return notes.trim() ? 'recommended_with_notes' : 'recommended';
  if (choice.startsWith('alternative_')) return 'alternative';
  if (notes.trim()) return 'written_answer';
  return choice === 'custom' ? 'needs_detail' : 'unanswered';
}
function makeExport(answers, overall_notes, exported_at = new Date().toISOString()) {
  return {schema: SCHEMA, baseline_date: DATA.baseline_date, baseline_sha256: DATA.baseline_sha256,
    exported_at, overall_notes, responses: DATA.questions.map(q => {
      const {choice = '', notes = ''} = answers[q.id] || {};
      return {id:q.id, title:q.title, group:q.group, question:q.prompt, recommendation:q.recommendation,
        context:q.context, status:responseStatus(choice,notes), choice:choice || null,
        choice_label:choiceLabel(q,choice), notes};
    }), source_documents: DATA.documents};
}
function readExport(value) {
  if (!value || value.schema !== SCHEMA || value.baseline_sha256 !== DATA.baseline_sha256)
    throw new Error('This file is not an answer export for this requirements baseline.');
  if (typeof value.overall_notes !== 'string' || !Array.isArray(value.responses) || value.responses.length !== DATA.questions.length)
    throw new Error('The answer file is incomplete or malformed.');
  const answers = {};
  for (const response of value.responses) {
    const q = DATA.questions.find(q => q.id === response.id);
    if (!q || Object.hasOwn(answers,response.id) || typeof response.notes !== 'string' ||
        !(response.choice === null || typeof response.choice === 'string')) throw new Error('Invalid answer record.');
    const choice = response.choice || '';
    choiceLabel(q,choice);
    answers[q.id] = {choice,notes:response.notes};
  }
  return {answers, overall_notes:value.overall_notes};
}
</script>
<script id="ui">
const byId = id => document.getElementById(id);
const storageKey = 'mango-questionnaire-' + DATA.baseline_sha256;
let dirty = false;
function collect() {
  return Object.fromEntries(DATA.questions.map(q => [q.id, {choice:byId('choice-'+q.id).value,notes:byId('notes-'+q.id).value}]));
}
function payload() { return makeExport(collect(),byId('global-notes').value); }
function updateProgress() {
  const rows = payload().responses;
  const count = rows.filter(r => !['unanswered','needs_detail'].includes(r.status)).length;
  const needs = rows.filter(r => r.status === 'needs_detail').length;
  byId('progress').textContent = count + ' of 29 responses recorded' + (needs ? ' · '+needs+' need details' : '');
}
function saveDraft() {
  dirty = true;
  updateProgress();
  try {
    localStorage.setItem(storageKey,JSON.stringify(payload()));
    byId('save-status').textContent = 'Draft saved in this browser. Download JSON before moving to another device or browser.';
  } catch {
    byId('save-status').textContent = 'Browser draft storage is unavailable or full. Download JSON to preserve your answers before closing.';
  }
}
function applyAnswers(state) {
  for (const q of DATA.questions) {
    byId('choice-'+q.id).value = state.answers[q.id].choice;
    byId('notes-'+q.id).value = state.answers[q.id].notes;
  }
  byId('global-notes').value = state.overall_notes;
  updateProgress();
}
document.querySelectorAll('select, textarea:not([readonly])').forEach(el => el.addEventListener('input',saveDraft));
try {
  const saved = localStorage.getItem(storageKey);
  if (saved) { applyAnswers(readExport(JSON.parse(saved))); byId('save-status').textContent = 'Restored your saved draft from this browser.'; }
  else byId('save-status').textContent = 'Answers will be saved in this browser when storage is available. Download JSON for a portable backup.';
} catch { byId('save-status').textContent = 'A local draft could not be restored. You can resume from a downloaded JSON file.'; }
function showJSON() {
  byId('export-text').value = JSON.stringify(payload(),null,2);
  byId('copy-status').textContent = '';
  byId('export-dialog').showModal();
}
function download() {
  try {
    const url = URL.createObjectURL(new Blob([JSON.stringify(payload(),null,2)], {type:'application/json'}));
    const link = document.createElement('a');
    link.href = url; link.download = 'mango-answers-'+new Date().toISOString().slice(0,10)+'.json';
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url),30000);
    dirty = false;
    byId('save-status').textContent = 'Download requested. Check your downloads; if unavailable, use View / copy JSON.';
  } catch { showJSON(); }
}
byId('download').onclick = download;
byId('download-bottom').onclick = download;
byId('show-json').onclick = showJSON;
byId('close-dialog').onclick = () => byId('export-dialog').close();
byId('copy').onclick = async () => {
  try { await navigator.clipboard.writeText(byId('export-text').value); byId('copy-status').textContent = 'JSON copied.'; }
  catch { byId('export-text').focus(); byId('export-text').select(); byId('copy-status').textContent = 'Use Ctrl+C or Command+C to copy the selected JSON.'; }
};
byId('import-button').onclick = () => byId('import-file').click();
byId('import-file').onchange = async event => {
  const file = event.target.files[0];
  if (!file) return;
  try {
    if (file.size > 2 * 1024 * 1024) throw new Error('This file exceeds the 2 MiB import limit.');
    const state = readExport(JSON.parse(await file.text()));
    if ((dirty || payload().responses.some(r => r.choice || r.notes) || byId('global-notes').value) &&
        !confirm('Replace the answers currently shown with the answers in this JSON file? Download your current answers first if you want to keep both.')) return;
    applyAnswers(state); saveDraft();
  } catch (error) { alert('Could not import answers: '+error.message); }
  finally { event.target.value = ''; }
};
window.addEventListener('beforeunload',event => {
  if (dirty) { event.preventDefault(); event.returnValue = ''; }
});
</script></body></html>'''
result = template.replace('@@NAV@@',''.join(nav)).replace('@@CARDS@@',''.join(cards)).replace('@@REFERENCES@@',references)
result = result.replace('@@DATA@@',json.dumps(data,ensure_ascii=False).replace('<','\\u003c'))
assert '@@' not in result
(ROOT/'mango-questionnaire.html').write_text(result,encoding='utf-8')
print(f'Built 29-question standalone HTML with {len(documents)} complete source documents.')
