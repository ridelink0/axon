// What Computer Use is allowed to touch.
//
// Two independent gates:
//   1. Tier - a property of the app itself. `blocked` apps are never readable
//      or actable, at any grant level, and no configuration lifts that.
//   2. Grant - per app, per session, and only for acting. Reading a window's
//      tree never implies permission to click in it.

import path from 'node:path';

// Never readable, never actable. Reading is blocked too, not only acting: a
// password manager's accessibility tree contains the secrets in plain text, so
// "just looking" is the leak.
const BLOCKED = [
  // Credential and secret stores
  'keepass', 'keepassxc', '1password', 'onepassword', 'bitwarden', 'lastpass',
  'dashlane', 'keeper', 'nordpass', 'enpass', 'roboform', 'passwordsafe',
  // Windows credential and elevation surfaces. An agent must never be able to
  // answer a UAC prompt or a login screen on the user's behalf.
  'consent', 'logonui', 'credentialuibroker', 'lsass', 'winlogon',
  'systemsettingsadminflows', 'useraccountcontrolsettings',
  // Security tooling
  'mmc', 'secpol', 'gpedit', 'certmgr', 'bitlockerwizard',
];

// Actable only after a grant that spells out what it covers. These reach far
// past their own window: a browser holds every logged-in session, a file
// manager can move or delete anything, Settings changes the machine.
const SENSITIVE = {
  'explorer':        'File Explorer can move, rename, and delete any file you can.',
  'systemsettings':  'Windows Settings changes machine-wide configuration.',
  'control':         'Control Panel changes machine-wide configuration.',
  'regedit':         'Registry Editor can change or break system configuration.',
  'taskmgr':         'Task Manager can end processes and discard their unsaved work.',
  'chrome':          'A browser carries every session you are signed in to.',
  'msedge':          'A browser carries every session you are signed in to.',
  'firefox':         'A browser carries every session you are signed in to.',
  'opera':           'A browser carries every session you are signed in to.',
  'brave':           'A browser carries every session you are signed in to.',
  'vivaldi':         'A browser carries every session you are signed in to.',
  'arc':             'A browser carries every session you are signed in to.',
  'outlook':         'An email client can read and send mail as you.',
  'thunderbird':     'An email client can read and send mail as you.',
  'slack':           'A messaging app can post as you in shared channels.',
  'teams':           'A messaging app can post as you in shared channels.',
  'discord':         'A messaging app can post as you in shared channels.',
};

// Shell-equivalent surfaces. Anything typed into these runs as you, so Computer Use
// reads them but never sends input.

// Dedicated terminals and IDEs: always shell, whatever they are showing.
const SHELL_APPS = [
  'windowsterminal', 'wt', 'conhost', 'mintty', 'conemu', 'conemu64', 'hyper',
  'alacritty', 'wezterm-gui', 'wezterm', 'kitty', 'kitty_portable', 'putty', 'tabby',
  'code', 'code - insiders', 'codium', 'devenv', 'idea64', 'pycharm64',
  'webstorm64', 'rider64', 'clion64', 'goland64', 'phpstorm64', 'rubymine64',
  'sublime_text', 'atom', 'cursor', 'windsurf', 'zed',
];

// Interpreters, which host both consoles and ordinary GUI windows. Process
// name alone would misjudge these: a WinForms dialog launched from a script is
// not a command prompt, and treating it as one would lock Computer Use out of every
// tool that happens to be script-hosted. Decide on the window class instead.
const INTERPRETERS = ['cmd', 'powershell', 'pwsh', 'bash', 'sh', 'zsh', 'git-bash', 'python', 'pythonw', 'node', 'wscript', 'cscript'];

const CONSOLE_CLASSES = [
  'ConsoleWindowClass', 'CASCADIA_HOSTING_WINDOW_CLASS', 'VirtualConsoleClass',
  'mintty', 'PuTTY', 'ConsoleWindowClass_0',
];

export const TIER = {
  BLOCKED: 'blocked',
  SHELL: 'shell',
  SENSITIVE: 'sensitive',
  STANDARD: 'standard',
};

function normalise(name) {
  if (!name) return '';
  let n = String(name).toLowerCase();
  if (n.endsWith('.exe')) n = n.slice(0, -4);
  return n;
}

// Apps the user added via the plugin's blocked_apps setting. Additive only:
// this can extend the blocklist, never shrink it.
const USER_BLOCKED = String(process.env.CU_BLOCKED_APPS || '')
  .split(',')
  .map((s) => normalise(s.trim()))
  // An unset plugin setting can arrive as the literal placeholder text rather
  // than an empty string, which would otherwise become a bogus blocklist entry.
  .filter((s) => s && !s.includes('${'));

export function classify(win) {
  const proc = normalise(win && win.process);
  const exe = normalise(win && win.path ? path.basename(win.path) : '');
  const candidates = [proc, exe].filter(Boolean);

  for (const c of candidates) {
    if (USER_BLOCKED.includes(c)) {
      return { tier: TIER.BLOCKED, reason: `"${c}" is on your blocked_apps list.` };
    }
  }
  for (const c of candidates) {
    if (BLOCKED.some((b) => c === b || c.startsWith(b))) {
      return { tier: TIER.BLOCKED, reason: 'This is a credential, elevation, or security surface. Computer Use never reads or drives these.' };
    }
  }
  const shellReason = 'Typing here runs commands as you. Computer Use reads this window but never sends input to it.';
  for (const c of candidates) {
    if (SHELL_APPS.includes(c)) return { tier: TIER.SHELL, reason: shellReason };
  }
  const cls = win && win.class ? String(win.class) : '';
  if (CONSOLE_CLASSES.some((k) => cls === k || cls.startsWith(k))) {
    return { tier: TIER.SHELL, reason: shellReason };
  }
  // An interpreter showing a console is a shell; an interpreter showing a
  // normal window is just an app that happens to be script-hosted.
  for (const c of candidates) {
    if (INTERPRETERS.includes(c) && CONSOLE_CLASSES.some((k) => cls.startsWith(k))) {
      return { tier: TIER.SHELL, reason: shellReason };
    }
  }
  for (const c of candidates) {
    if (Object.prototype.hasOwnProperty.call(SENSITIVE, c)) {
      return { tier: TIER.SENSITIVE, reason: SENSITIVE[c] };
    }
  }
  return { tier: TIER.STANDARD, reason: null };
}

export class Policy {
  constructor() {
    // key: normalised process name -> { grantedAt, reason }
    this.grants = new Map();
    this.selfPids = new Set();
  }

  // Windows belonging to this Claude Code session are excluded from listings
  // and captures entirely, so on-screen text from the session can never feed
  // back into the model as if it were observed content.
  markSelf(pids) {
    for (const p of pids) if (p) this.selfPids.add(Number(p));
  }

  isSelf(win) {
    return win && this.selfPids.has(Number(win.pid));
  }

  key(win) {
    return normalise(win && win.process) || normalise(win && win.path ? path.basename(win.path) : '') || '?';
  }

  grant(win) {
    const { tier, reason } = classify(win);
    if (tier === TIER.BLOCKED) {
      return { ok: false, tier, reason };
    }
    if (tier === TIER.SHELL) {
      return { ok: false, tier, reason };
    }
    this.grants.set(this.key(win), { grantedAt: Date.now(), tier });
    return { ok: true, tier, reason };
  }

  revoke(key) {
    return this.grants.delete(normalise(key));
  }

  revokeAll() {
    const n = this.grants.size;
    this.grants.clear();
    return n;
  }

  granted(win) {
    return this.grants.has(this.key(win));
  }

  listGrants() {
    const out = [];
    for (const [k, v] of this.grants) out.push({ app: k, tier: v.tier, granted_at: new Date(v.grantedAt).toISOString() });
    return out;
  }

  // Gate for read operations: snapshot, screenshot of a window.
  checkRead(win) {
    if (this.isSelf(win)) {
      return {
        ok: false,
        code: 'self_window',
        message: 'That window belongs to this Claude Code session.',
        hint: 'Computer Use excludes its own session so on-screen text cannot be fed back to the model as observed content.',
      };
    }
    const { tier, reason } = classify(win);
    if (tier === TIER.BLOCKED) {
      return { ok: false, code: 'app_blocked', message: reason, hint: 'This is not configurable.' };
    }
    return { ok: true, tier };
  }

  // Gate for anything that sends input or closes a window.
  checkAct(win) {
    const read = this.checkRead(win);
    if (!read.ok) return read;
    const { tier, reason } = classify(win);
    if (tier === TIER.SHELL) {
      return { ok: false, code: 'app_input_blocked', message: reason, hint: 'Use the Bash tool for shell work; it is sandboxed and auditable.' };
    }
    if (!this.granted(win)) {
      return {
        ok: false,
        code: 'not_granted',
        message: `No grant for "${this.key(win)}" in this session.`,
        hint: `Call computer_grant with app "${this.key(win)}" first. Grants last for this session only.`,
      };
    }
    return { ok: true, tier };
  }
}
