// Coexistence tests: can Axon tell the human apart from itself, and does it
// stay out of the way when the human is working?
//
// The load-bearing assertion is that Axon's own SendInput never registers as
// user activity. Windows flags injected events at the kernel and the injecting
// process cannot clear that flag, so this is checkable rather than hopeful.

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Driver } from '../server/driver.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const TARGET = path.join(HERE, 'make-target.ps1');

let pass = 0, fail = 0; const failures = [];
const check = (n, c, d) => {
  if (c) { pass++; console.log('  ok   ' + n); }
  else { fail++; failures.push(n); console.log(`  FAIL ${n}${d ? ' :: ' + d : ''}`); }
};

function spawnTarget() {
  return new Promise((resolve, reject) => {
    const p = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', TARGET],
      { stdio: ['ignore', 'pipe', 'pipe'] });
    const t = setTimeout(() => reject(new Error('target never reported in')), 15000);
    createInterface({ input: p.stdout }).on('line', (l) => {
      try { const i = JSON.parse(l.trim()); if (i.title) { clearTimeout(t); resolve({ proc: p, ...i }); } } catch {}
    });
  });
}

const d = new Driver({ onLog: () => {} });

console.log('\n-- monitoring --');
await d.start();
const p0 = (await d.call('presence')).result;
check('presence is available', p0.monitoring === true, JSON.stringify(p0));
console.log(`     source: ${p0.source}${p0.source === 'last-input' ? ' (hooks installed but not delivering - security tooling can throttle a fresh unsigned binary)' : ''}`);
check('reports idle time', typeof p0.idle_ms === 'number');
check('reports commit idle separately', typeof p0.commit_idle_ms === 'number');
check('reports the foreground window', typeof p0.foreground_hwnd === 'number');
check('default mode is share', p0.mode === 'share', p0.mode);

console.log('\n-- Axon\'s own input must never look like the user --');
const t = await spawnTarget();
await new Promise((r) => setTimeout(r, 800));
const win = (await d.call('list_apps')).result.windows.find((w) => w.title === t.title);
check('target window found', !!win);

await d.call('focus', { hwnd: win.hwnd, mode: 'take' });
const snap = (await d.call('snapshot', { hwnd: win.hwnd })).result;
const btn = snap.nodes.find((n) => n.name === 'Press Me');
const box = snap.nodes.find((n) => n.name === 'Name field');

const before = (await d.call('presence')).result;
for (let i = 0; i < 6; i++) {
  await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, physical: true, mode: 'take' });
  await d.call('type', { hwnd: win.hwnd, snapshot_id: snap.snapshot_id, index: box.i, text: 'xy', mode: 'take' });
}
const after = (await d.call('presence')).result;
const injDelta = after.injected_events - before.injected_events;
const realDelta = after.real_events - before.real_events;
console.log(`     injected +${injDelta}, real +${realDelta}, idle now ${after.idle_ms}ms`);

// The property that must hold on either source: a burst of Axon's own input
// must not make the machine look busy. How that is established differs.
if (after.source === 'hooks') {
  check('our own input is observed and flagged injected', injDelta > 10, `+${injDelta}`);
  if (realDelta === 0) {
    check('our own input never counts as the user', true);
  } else {
    console.log(`     (the user genuinely moved: +${realDelta} real events)`);
    check('injected and real are counted separately', injDelta > realDelta, `inj=${injDelta} real=${realDelta}`);
  }
} else {
  // The hook-free path discounts input that lands right after one of Axon's
  // own injections, so idle must survive the burst.
  check('fallback source is reported honestly', after.source === 'last-input');
  check('hooks_delivering is false, not pretended', after.hooks_delivering === false);
}
check('Axon acting alone leaves the machine looking idle', after.idle_ms > 400, `${after.idle_ms}ms via ${after.source}`);

console.log('\n-- cursor courtesy --');
const clicked = (await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, physical: true })).result;
check('the pointer is put back after a real click', clicked.cursor_restored === true, JSON.stringify(clicked));
const taken = (await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, physical: true, mode: 'take' })).result;
check('take mode leaves the pointer where it clicked', !taken.cursor_restored, JSON.stringify(taken));

console.log('\n-- pattern actions are invisible and never wait --');
const started = Date.now();
const pat = (await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, mode: 'share' })).result;
const elapsed = Date.now() - started;
check('pattern path used', pat.method === 'invoke_pattern', pat.method);
// A pattern action must not sit in the courtesy queue. It can still be slow
// if the target app's provider is slow, which is the app's fault, not Axon's.
check('no courtesy wait for a pattern action', elapsed < 6000 && !pat.waited_for_user_ms, `${elapsed}ms`);
check('pattern actions never move the pointer', pat.cursor_restored === undefined);

console.log('\n-- modes --');
for (const m of ['share', 'yield', 'take']) {
  const r = await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, mode: m });
  check(`mode ${m} accepted`, !!r.result.method, JSON.stringify(r.result));
}

console.log('\n-- the same-window rule is specific to that window --');
// The user last acted in the terminal, not in our freshly raised target, so
// acting on the target must be allowed even though they were recently busy.
const pres = (await d.call('presence')).result;
const sameWindow = pres.user_window === win.hwnd;
const r2 = await d.call('click', { hwnd: win.hwnd, snapshot_id: snap.snapshot_id, index: btn.i });
check('a window the user is not working in stays available',
  !!r2.result.method || sameWindow, JSON.stringify(r2.result));
console.log(`     user last acted in hwnd=${pres.user_window}, target=${win.hwnd}`);

console.log('\n-- overlay: showing the user what happened --');
check('overlay started', d.info.overlay === true, JSON.stringify(d.info));

// It must never be a window Claude can see or target.
const listed = (await d.call('list_apps', { include_hidden: true })).result.windows;
check('overlay never appears in list_apps',
  !listed.some((w) => (w.process || '').toLowerCase().indexOf('axonhost') === 0),
  listed.filter((w) => (w.process || '').toLowerCase().indexOf('axonhost') === 0).map((w) => w.title).join(','));

// Draw the marker, then click straight through where it sits. If it could
// intercept a click, or be mistaken for a window covering the target, this
// is where it breaks.
await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, mode: 'take' });
const through = await d.call('click', { snapshot_id: snap.snapshot_id, index: btn.i, physical: true, mode: 'take' });
check('a physical click lands with the marker on screen',
  through.result.method === 'physical', JSON.stringify(through.result));

const shot = (await d.call('screenshot', { hwnd: win.hwnd, max_width: 600 })).result;
check('screenshot succeeds with the marker up', shot.bytes > 500);

console.log('\n-- overlay and presence can each be turned off --');
for (const [envVar, field] of [['AXON_OVERLAY', 'overlay'], ['AXON_PRESENCE', 'presence']]) {
  process.env[envVar] = 'off';
  const d2 = new Driver({ onLog: () => {} });
  const info2 = await d2.start();
  check(`${envVar}=off disables ${field}`, info2[field] === false, JSON.stringify(info2));
  await d2.stop();
  delete process.env[envVar];
}

console.log('\n-- config is honoured --');
check('idle threshold reported', pres.idle_threshold_ms === 1200, String(pres.idle_threshold_ms));

await d.call('close_window', { hwnd: win.hwnd, mode: 'take' });
try { t.proc.kill(); } catch {}
await d.stop();

console.log(`\n${pass} passed, ${fail} failed`);
if (fail) { console.log('failures: ' + failures.join(', ')); process.exit(1); }
