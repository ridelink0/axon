// App profiles - the Shortcuts/Sky idea, scaled down.
//
// Sky's insight was that an agent should know what actions an app affords, not
// just what pixels it is showing. A profile carries the small amount of
// per-app knowledge that stops an agent burning turns rediscovering it: the
// shortcut that beats hunting for a menu, and the trap that looks like success
// but is not.
//
// Deliberately tiny. These are hints attached to a grant or a snapshot, never
// a second code path, so an unknown app degrades to plain tree-driving rather
// than failing.

import path from 'node:path';

const PROFILES = [
  {
    match: ['notepad'],
    hint: 'Windows 11 Notepad runs every window under one shared process and one tabbed frame. Closing what looks like a spare window can take unsaved tabs with it - close a tab, not the process, and never assume a second window is disposable.',
    keys: { save: 'ctrl+s', find: 'ctrl+f', 'new tab': 'ctrl+n' },
  },
  {
    match: ['explorer'],
    hint: 'The address bar accepts a typed path and Enter, which is far more reliable than clicking through folders.',
    keys: { 'address bar': 'ctrl+l', 'new folder': 'ctrl+shift+n', rename: 'f2' },
  },
  {
    match: ['chrome', 'msedge', 'brave', 'vivaldi', 'opera', 'firefox'],
    hint: 'Page content lives under a Document element and can be thousands of nodes - snapshot with interactive_only, or the tree will truncate. '
        + 'TO CHANGE THE URL, do exactly this: computer_key ctrl+l, then computer_type with the URL and NO target and NO replace, then computer_key enter. '
        + 'That path always works. Do NOT try computer_type replace:true on the address bar: browsers expose the omnibox twice, and the outer one '
        + '(named "Address bar" or "Address and search bar") is a container that refuses both a value and the focus - it returns focus_failed no matter how many times you focus the window. '
        + 'The inner "Address field" does accept a value, but you still need enter afterwards, so the keyboard path is fewer steps and never picks the wrong one.',
    keys: { omnibox: 'ctrl+l', 'new tab': 'ctrl+t', find: 'ctrl+f', reload: 'f5' },
  },
  {
    match: ['winword', 'excel', 'powerpnt'],
    hint: 'Office ribbons expose their controls only for the active tab, so switch ribbon tabs before looking for a command.',
    keys: { save: 'ctrl+s', undo: 'ctrl+z' },
  },
  {
    match: ['mspaint'],
    hint: 'The canvas is a single opaque element with no inner structure. Use computer_screenshot and point targeting for anything on the canvas; the tree only covers the tools and menus around it.',
  },
  {
    match: ['calc'],
    hint: 'Buttons carry AutomationIds such as num5Button and plusButton, which are stabler selectors than their display names.',
  },
];

function norm(name) {
  if (!name) return '';
  let n = String(name).toLowerCase();
  if (n.endsWith('.exe')) n = n.slice(0, -4);
  return n;
}

export function profileFor(win) {
  const names = [norm(win && win.process), norm(win && win.path ? path.basename(win.path) : '')].filter(Boolean);
  for (const p of PROFILES) {
    for (const n of names) if (p.match.includes(n)) return p;
  }
  return null;
}

export function profileHint(win) {
  const p = profileFor(win);
  if (!p) return null;
  let s = p.hint;
  if (p.keys) {
    const pairs = Object.entries(p.keys).map(([k, v]) => `${k}=${v}`).join(', ');
    s += ` Shortcuts: ${pairs}.`;
  }
  return s;
}
