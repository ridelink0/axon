// Virtual desktops, tested for real.
//
// This one is NOT in test-all, because it does something the other suites never
// do: it creates a second virtual desktop, switches to it, and closes it again.
// That moves the screen out from under whoever is sitting there, so it is opt-in
// and run on its own:
//
//   node tools/desktop-test.mjs
//
// It proves the two things that matter when a Claude session and a person are
// on different desktops:
//
//   1. A window on another desktop is reported as being on another desktop,
//      rather than looking like an ordinary window whose coordinates lie.
//   2. The banner carrying the Stop button follows the user to the desktop they
//      switched to. A running agent whose only visible "stop" is on a desktop
//      you are not looking at is an agent you cannot stop.
//
// Whatever happens, it puts the desktop back.

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Driver } from '../server/driver.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const TARGET = path.join(HERE, 'make-target.ps1');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

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
await d.start();

console.log('\n-- does this Windows have virtual desktops --');
const p0 = (await d.call('presence')).result;
console.log(`     ${p0.virtual_desktops}`);
if (p0.virtual_desktops !== 'available') {
  console.log('\nThis Windows does not expose the virtual desktop interface, so there is');
  console.log('nothing to test. Computer Use degrades to treating every window as being');
  console.log('on the desktop you are looking at, which is what it did before.');
  await d.stop();
  process.exit(0);
}

const target = await spawnTarget();
await sleep(900);
let created = false;

try {
  const win = (await d.call('list_apps')).result.windows.find((w) => w.title === target.title);
  check('the target window is here to begin with', !!win);

  const before = (await d.call('list_apps', { include_hidden: true })).result.windows
    .find((w) => w.title === target.title);
  check('and is not flagged as being somewhere else', !before.other_desktop);

  // Draw the banner, then check it is on this desktop.
  await d.call('focus', { hwnd: win.hwnd, mode: 'take' });
  await d.call('click', { hwnd: win.hwnd, selector: { name: 'Press Me' }, mode: 'take' });
  const p1 = (await d.call('presence')).result;
  check('the banner is on the desktop we are on', p1.overlay_on_current_desktop === true,
    JSON.stringify(p1.overlay_on_current_desktop));

  console.log('\n-- switching to a new virtual desktop --');
  await d.call('key', { keys: 'win+ctrl+d', mode: 'take' });
  created = true;
  await sleep(2500);   // the switch animation has to finish before anything is true

  const after = (await d.call('list_apps', { include_hidden: true })).result.windows
    .find((w) => w.title === target.title);
  check('the window left behind is still findable', !!after);
  check('and is now reported as being on another virtual desktop',
    !!(after && after.other_desktop), JSON.stringify(after && after.other_desktop));

  const plain = (await d.call('list_apps')).result.windows.find((w) => w.title === target.title);
  check('it is out of the ordinary listing, like a minimized window', !plain);

  console.log('\n-- what can be read from over here --');
  // Windows serves a cloaked window's frame and nothing else. That is an
  // operating system limit, not something to paper over. What matters is that
  // it is reported as what it is, rather than looking like an application that
  // has suddenly lost every control it had.
  const snap = (await d.call('snapshot', { hwnd: after.hwnd })).result;
  check('the window still resolves and reads', snap.node_count >= 1, String(snap.node_count));
  check('and the read is marked as being from another desktop', snap.other_desktop === true,
    JSON.stringify(snap.other_desktop));
  console.log('     ' + snap.nodes.map((n) => n.role + (n.name ? ':' + n.name : '')).join(', ').slice(0, 220));
  check('the frame is what comes back, which is all Windows serves',
    !snap.nodes.find((n) => n.name === 'Press Me'),
    'contents came back for a cloaked window - that would be new behaviour worth knowing about');

  console.log('\n-- and does the Stop button follow the user --');
  const p2 = (await d.call('presence')).result;
  check('the banner came to this desktop too', p2.overlay_on_current_desktop === true,
    JSON.stringify(p2.overlay_on_current_desktop));
} finally {
  if (created) {
    console.log('\n-- putting the desktop back --');
    await d.call('key', { keys: 'win+ctrl+f4', mode: 'take' });
    await sleep(2000);
    const p3 = (await d.call('presence')).result;
    check('back on the original desktop, banner still with us',
      p3.overlay_on_current_desktop !== false, JSON.stringify(p3.overlay_on_current_desktop));
  }
  try {
    const cur = (await d.call('list_apps', { include_hidden: true })).result.windows
      .find((w) => w.title === target.title);
    if (cur) await d.call('close_window', { hwnd: cur.hwnd, mode: 'take' });
  } catch {}
  try { target.proc.kill(); } catch {}
  await d.stop();
}

console.log(`\n${pass} passed, ${fail} failed`);
if (fail) { console.log('failures: ' + failures.join(', ')); process.exit(1); }
