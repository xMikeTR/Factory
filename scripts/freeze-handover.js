// Freeze a handover the way Designc's app does (renderer/app.js freezeHandover), headless.
// Usage: node scripts/freeze-handover.js <designc>/renderer/core.js <registry> <by>
const fs = require('fs'), path = require('path');
const [core, root, by] = process.argv.slice(2);
const Core = require(core);
const cfg = JSON.parse(fs.readFileSync(path.join(root, 'designc.json'), 'utf8'));
const read = (dir, exts) => { const d = path.join(root, dir); if (!fs.existsSync(d)) return [];
  return fs.readdirSync(d).filter(f => exts.some(e => f.endsWith(e))).sort().map(f => ({ path: `${dir}/${f}`, text: fs.readFileSync(path.join(d, f), 'utf8') })); };
const decisions = read('decisions', ['.yaml', '.yml']).filter(f => /^status:\s*bound\b/m.test(f.text));
const now = new Date().toISOString();
const plan = Core.handoverPlan({ date: now.slice(0, 10), now, by, model: { path: cfg.model, text: fs.readFileSync(path.join(root, cfg.model), 'utf8') },
  requirements: read('requirements', ['.yaml', '.yml']), decisions, behaviour: read('behaviour', ['.p']),
  vocabulary: read('vocabulary', ['.yaml', '.yml']), evals: read('evals', ['.yaml', '.yml']) });
for (const f of plan.files) { fs.mkdirSync(path.dirname(path.join(root, f.path)), { recursive: true }); fs.writeFileSync(path.join(root, f.path), f.text); }
console.log(JSON.stringify({ id: plan.id, revision: plan.files[0].text.match(/^revision: (r-\w+)/m)[1], files: plan.files.map(f => f.path) }));
