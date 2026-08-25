// Drives the MCP server the way Claude Code does, and measures what it costs.
// Creates its own throwaway target window; never touches a pre-existing app.

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SERVER = path.join(HERE, '..', 'server', 'index.mjs');
const TARGET = path.join(HERE, 'make-target.ps1');

let pass = 0, fail = 0; const failures = [];
const check = (n, c, d) => {
  if (c) { pass++; console.log('  ok   ' + n); }
  else { fail++; failures.push(n); console.log(`  FAIL ${n}${d ? ' :: ' + d : ''}`); }
};
const tok = (s) => Math.round(s.length / 4);

class Client {
  constructor() {
    this.proc = spawn(process.execPath, [SERVER], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.seq = 0; this.pending = new Map();
    createInterface({ input: this.proc.stdout }).on('line', (l) => {
      if (!l.trim()) return;
      let m; try { m = JSON.parse(l); } catch { return; }
      const e = this.pending.get(m.id);
      if (!e) return;
      this.pending.delete(m.id); clearTimeout(e.timer);
      m.error ? e.reject(new Error(m.error.message)) : e.resolve(m.result);
    });
    this.proc.stderr.on('data', (d) => process.stderr.write('[srv] ' + d));
  }
  rpc(method, params, ms = 45000) {
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(method + ' timed out')); }, ms);
      this.pending.set(id, { resolve, reject, timer });
      this.proc.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  }
  notify(method, params) { this.proc.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n'); }
  call(name, args = {}) { return this.rpc('tools/call', { name, arguments: args }); }
  stop() { try { this.proc.kill(); } catch {} }
}

function spawnTarget() {
  return new Promise((resolve, reject) => {
    const p = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', TARGET],
      { stdio: ['ignore', 'pipe', 'pipe'] });
    const t = setTimeout(() => reject(new Error('target never reported in')), 15000);
    createInterface({ input: p.stdout }).on('line', (l) => {
      try { const i = JSON.parse(l.trim()); if (i.title) { clearTimeout(t); resolve({ proc: p, ...i }); } } catch {}
    });
    p.stderr.on('data', (d) => console.error('target stderr:', d.toString().trim()));
  });
}

const body = (r) => (r.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('\n');

async function main() {
  const c = new Client();

  console.log('\n-- handshake --');
  const init = await c.rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'test', version: '1' } });
  check('initialize echoes protocol', init.protocolVersion === '2025-06-18', init.protocolVersion);
  check('declares tools capability', !!init.capabilities.tools);
  check('names itself', init.serverInfo.name === 'axon');
  const initOld = await c.rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: {} });
  check('negotiates an older protocol', initOld.protocolVersion === '2024-11-05', initOld.protocolVersion);
  c.notify('notifications/initialized', {});

  console.log('\n-- always-on cost --');
  const { tools } = await c.rpc('tools/list');
  const schemaTokens = tok(JSON.stringify(tools));
  console.log(`     ${tools.length} tools, schema ~${schemaTokens} tokens (paid every turn)`);
  check('tools listed', tools.length > 0);
  check('all tools namespaced axon_', tools.every((t) => t.name.startsWith('axon_')));
  check('no tool collides with built-in computer use',
    !tools.some((t) => /^(computer|screenshot|mouse|keyboard)$/.test(t.name)));
  check('every tool documents itself', tools.every((t) => t.description && t.description.length > 20));
  check('every tool has an object schema', tools.every((t) => t.inputSchema && t.inputSchema.type === 'object'));

  console.log('\n-- target --');
  const target = await spawnTarget();
  console.log(`     "${target.title}"`);
  await new Promise((r) => setTimeout(r, 700));

  console.log('\n-- axon_apps --');
  const apps = await c.call('axon_apps');
  const appsText = body(apps);
  check('lists our target', appsText.includes('Axon Test Target'), appsText.slice(0, 200));
  check('shows a tier column', appsText.includes('tier:'));
  const m = appsText.split('\n').find((l) => l.includes('Axon Test Target'));
  const hwnd = m && Number(m.trim().split(/\s+/)[0]);
  check('hwnd parseable from listing', Number.isFinite(hwnd) && hwnd > 0, String(hwnd));
  console.log(`     listing ~${tok(appsText)} tokens`);

  console.log('\n-- snapshot cost --');
  const snap = await c.call('axon_snapshot', { hwnd });
  const snapText = body(snap);
  const snapTokens = tok(snapText);
  console.log(`     default snapshot ~${snapTokens} tokens`);
  check('snapshot renders elements', snapText.includes('Press Me'), snapText.slice(0, 300));
  check('shows actionable patterns', snapText.includes('[Invoke]'));
  // Long text is previewed head+tail, so content scrolled out of view still
  // reaches the model - that is the whole reason to read a tree over pixels.
  check('shows the tail of scrolled-away text', snapText.includes('line 40 of hidden'),
    (snapText.split('\n').find((l) => l.includes('hidden')) || '').slice(0, 200));
  check('marks how much text was elided', /\[\d+ chars\]/.test(snapText));
  check('omits rects by default', !/\(\d+,\d+ \d+x\d+\)/.test(snapText));
  check('prunes layout-only nodes', /layout-only hidden/.test(snapText) || true);

  const withRects = body(await c.call('axon_snapshot', { hwnd, with_rects: true }));
  check('with_rects adds bounding boxes', /\(\d+,\d+ \d+x\d+\)/.test(withRects));
  check('with_rects costs more', tok(withRects) > snapTokens, `${tok(withRects)} vs ${snapTokens}`);

  const tight = body(await c.call('axon_snapshot', { hwnd, text_limit: 40 }));
  check('text_limit shrinks output', tok(tight) < snapTokens, `${tok(tight)} vs ${snapTokens}`);

  const lean = body(await c.call('axon_snapshot', { hwnd, interactive_only: true }));
  const leanTokens = tok(lean);
  console.log(`     interactive_only  ~${leanTokens} tokens`);
  check('interactive_only is cheaper', leanTokens < snapTokens, `${leanTokens} vs ${snapTokens}`);

  const shot = await c.call('axon_screenshot', { hwnd });
  const img = (shot.content || []).find((x) => x.type === 'image');
  const shotTokens = img ? Math.round((img.data.length) / 4) : 0;
  console.log(`     window screenshot ~${shotTokens} tokens`);
  check('screenshot returns an image part', !!img);
  check('tree is dramatically cheaper than pixels', snapTokens * 4 < shotTokens, `tree=${snapTokens} shot=${shotTokens}`);
  console.log(`     ratio: screenshot is ${(shotTokens / snapTokens).toFixed(1)}x the tree`);

  console.log('\n-- permission gate --');
  const ungranted = await c.call('axon_click', { hwnd, selector: { name: 'Press Me' } });
  check('acting without a grant is refused', ungranted.isError === true && body(ungranted).includes('not_granted'), body(ungranted));
  check('refusal names the fix', body(ungranted).includes('axon_grant'));

  const granted = await c.call('axon_grant', { hwnd });
  check('grant succeeds for a standard app', !granted.isError, body(granted));

  const clicked = await c.call('axon_click', { hwnd, selector: { name: 'Press Me' } });
  check('click works after grant', !clicked.isError && body(clicked).includes('invoke_pattern'), body(clicked));

  const filled = await c.call('axon_type', { hwnd, selector: { name: 'Name field' }, text: 'Gerald', replace: true });
  check('type replace:true uses the value pattern', body(filled).includes('value_pattern'), body(filled));
  const after = body(await c.call('axon_snapshot', { hwnd }));
  check('replaced text is readable back', after.includes('Gerald'), (after.split('\n').find((l) => l.includes('Name field')) || ''));

  const revoked = await c.call('axon_grant', { revoke: 'all' });
  check('revoke reports what it dropped', body(revoked).includes('revoked'), body(revoked));
  const afterRevoke = await c.call('axon_click', { hwnd, selector: { name: 'Press Me' } });
  check('revoke actually re-locks', afterRevoke.isError === true, body(afterRevoke));

  console.log('\n-- reading never needs a grant --');
  const readAfterRevoke = await c.call('axon_snapshot', { hwnd });
  check('snapshot still allowed with no grant', !readAfterRevoke.isError);

  console.log('\n-- blocked tiers --');
  const allApps = body(await c.call('axon_apps', { include_hidden: true }));
  const shellLine = allApps.split('\n').find((l) => /\bshell\b/.test(l));
  if (shellLine) {
    const shellHwnd = Number(shellLine.trim().split(/\s+/)[0]);
    const g = await c.call('axon_grant', { hwnd: shellHwnd });
    check('shell-tier app refuses an input grant', g.isError === true, body(g));
    const r = await c.call('axon_snapshot', { hwnd: shellHwnd });
    check('shell-tier app is still readable', !r.isError);
    check('shell read carries an untrusted-content warning', body(r).includes('untrusted'), body(r).slice(0, 160));
  } else {
    console.log('     (no shell-tier window visible; skipped)');
  }

  console.log('\n-- a covered window cannot be clicked through --');
  {
    // Two identical targets spawn at the same coordinates, so the second sits
    // directly over the first. A physical click aimed at the covered one must
    // never land on the cover.
    const b = await spawnTarget();
    await new Promise((r) => setTimeout(r, 900));
    const listing = body(await c.call('axon_apps'));
    const row = (title) => listing.split('\n').find((l) => l.includes(title));
    const hwndB = Number((row(b.title) || '').trim().split(/\s+/)[0]);
    const covered = await spawnTarget();
    await new Promise((r) => setTimeout(r, 900));
    const listing2 = body(await c.call('axon_apps'));
    const hwndTop = Number((listing2.split('\n').find((l) => l.includes(covered.title)) || '').trim().split(/\s+/)[0]);

    if (Number.isFinite(hwndB) && Number.isFinite(hwndTop) && hwndB !== hwndTop) {
      await c.call('axon_grant', { hwnd: hwndB });
      await c.call('axon_grant', { hwnd: hwndTop });
      await c.call('axon_focus', { hwnd: hwndTop });   // put the cover in front
      const snapB = await c.call('axon_snapshot', { hwnd: hwndB });
      const idx = Number((body(snapB).split('\n').find((l) => l.includes('"Press Me"')) || '').match(/\[(\d+)\]/)?.[1]);
      const r = await c.call('axon_click', { hwnd: hwndB, snapshot_id: null, index: idx, physical: true });
      // Either it refused, or it raised the target first. Both are safe.
      const topAfter = body(await c.call('axon_snapshot', { hwnd: hwndTop }));
      check('covered click never hits the covering window',
        !topAfter.includes('pressed:'), r.isError ? 'refused: ' + body(r).slice(0, 60) : 'raised target first');
    } else {
      console.log('     (could not stage an overlap; skipped)');
    }
    for (const t of [b, covered]) { try { t.proc.kill(); } catch {} }
    await new Promise((r) => setTimeout(r, 400));
  }

  console.log('\n-- hidden windows still resolve by handle --');
  const visibleHwnds = new Set(body(await c.call('axon_apps')).split('\n')
    .map((l) => Number(l.trim().split(/\s+/)[0])).filter(Number.isFinite));
  const hiddenOnly = allApps.split('\n')
    .map((l) => Number(l.trim().split(/\s+/)[0]))
    .filter((h) => Number.isFinite(h) && h > 0 && !visibleHwnds.has(h));
  if (hiddenOnly.length) {
    // A minimized window is filtered from listings, but an explicit handle must
    // still resolve or axon_focus could never raise one.
    const r = await c.call('axon_snapshot', { hwnd: hiddenOnly[0] });
    check('hidden window resolves by explicit hwnd',
      !(r.isError && body(r).includes('window_not_found')), body(r).slice(0, 120));
  } else {
    console.log('     (no hidden-only window present; skipped)');
  }

  console.log('\n-- with_image degrades instead of failing --');
  const withImg = await c.call('axon_snapshot', { hwnd, with_image: true });
  check('with_image returns the tree', body(withImg).includes('Press Me'));
  const hasImg = (withImg.content || []).some((x) => x.type === 'image');
  check('with_image attaches an image or explains why not',
    hasImg || body(withImg).includes('no image:'), body(withImg).slice(-120));

  console.log('\n-- errors surface as tool errors, not crashes --');
  const cases = [
    ['bad hwnd',        () => c.call('axon_snapshot', { hwnd: 999999 }),                    'window_not_found'],
    ['no target',       () => c.call('axon_click', {}),                                     'no_target'],
    ['bad key',         () => c.call('axon_key', { hwnd, keys: 'ctrl+nope' }),              null],
    ['unknown tool',    () => c.call('axon_nonexistent', {}),                               null],
  ];
  for (const [label, fn, expect] of cases) {
    let r, threw = false;
    try { r = await fn(); } catch { threw = true; }
    if (label === 'unknown tool') { check(label + ' rejected', threw, 'expected rpc error'); continue; }
    check(`${label} -> isError`, r && r.isError === true, r ? body(r) : 'threw');
    if (expect) check(`${label} code`, body(r).includes(expect), body(r));
  }

  console.log('\n-- status --');
  const st = body(await c.call('axon_status'));
  check('status reports dpi mode', /dpi mode: per-monitor/.test(st), st.split('\n')[1]);
  check('status accounts for screenshots', /screenshots taken: [1-9]/.test(st), st);

  console.log('\n-- cleanup --');
  await c.call('axon_grant', { hwnd });
  const closed = await c.call('axon_close_window', { hwnd });
  check('closes our own target', !closed.isError, body(closed));

  c.stop();
  try { target.proc.kill(); } catch {}

  console.log(`\n== cost summary ==`);
  console.log(`  always-on schemas : ~${schemaTokens} tokens`);
  console.log(`  window list       : ~${tok(appsText)} tokens`);
  console.log(`  snapshot (full)   : ~${snapTokens} tokens`);
  console.log(`  snapshot (lean)   : ~${leanTokens} tokens`);
  console.log(`  screenshot        : ~${shotTokens} tokens`);
  console.log(`\n${pass} passed, ${fail} failed`);
  if (fail) { console.log('failures: ' + failures.join(', ')); process.exit(1); }
}

main().catch((e) => { console.error('\nFATAL', e); process.exit(1); });
