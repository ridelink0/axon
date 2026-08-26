// Direct tests of the safety model. No windows, no host - just classification
// and grant logic against synthetic window records, so every tier and every
// refusal path is covered deterministically.

import { Policy, classify, TIER } from '../server/policy.mjs';

let pass = 0, fail = 0; const failures = [];
const check = (n, c, d) => {
  if (c) { pass++; console.log('  ok   ' + n); }
  else { fail++; failures.push(n); console.log(`  FAIL ${n}${d ? ' :: ' + d : ''}`); }
};

const win = (process, extra = {}) => ({ hwnd: 1, pid: 99, process, title: 't', class: 'X', path: `C:\\a\\${process}.exe`, ...extra });

console.log('\n-- blocked: never readable, never actable --');
for (const p of ['keepass', 'KeePassXC', '1password', 'bitwarden', 'lastpass', 'dashlane', 'keeper']) {
  check(`${p} is blocked`, classify(win(p)).tier === TIER.BLOCKED, classify(win(p)).tier);
}
for (const p of ['consent', 'LogonUI', 'CredentialUIBroker', 'lsass', 'winlogon']) {
  check(`${p} (elevation/login) is blocked`, classify(win(p)).tier === TIER.BLOCKED, classify(win(p)).tier);
}
check('blocked apps are not readable either', new Policy().checkRead(win('keepass')).ok === false);
check('blocked read names the reason', /credential|elevation|security/i.test(new Policy().checkRead(win('keepass')).message));

console.log('\n-- shell: readable, never typeable --');
for (const p of ['WindowsTerminal', 'conhost', 'Code', 'devenv', 'idea64', 'cursor', 'alacritty']) {
  check(`${p} is shell tier`, classify(win(p)).tier === TIER.SHELL, classify(win(p)).tier);
}
{
  const p = new Policy();
  check('shell apps are readable', p.checkRead(win('Code')).ok === true);
  check('shell apps refuse input', p.checkAct(win('Code')).ok === false);
  check('shell refusal has its own code', p.checkAct(win('Code')).code === 'app_input_blocked');
  check('granting a shell app fails', p.grant(win('Code')).ok === false);
  check('a failed grant is not recorded', p.granted(win('Code')) === false);
}

console.log('\n-- interpreters are judged by window class, not process name --');
check('powershell console is shell',
  classify(win('powershell', { class: 'ConsoleWindowClass' })).tier === TIER.SHELL);
check('powershell hosting a GUI window is not shell',
  classify(win('powershell', { class: 'WindowsForms10.Window.8.app.0.141b42a_r6_ad1' })).tier === TIER.STANDARD,
  classify(win('powershell', { class: 'WindowsForms10.Window' })).tier);
check('cascadia-hosted console is shell',
  classify(win('powershell', { class: 'CASCADIA_HOSTING_WINDOW_CLASS' })).tier === TIER.SHELL);
check('node hosting a GUI window is not shell',
  classify(win('node', { class: 'Chrome_WidgetWin_1' })).tier === TIER.STANDARD);

console.log('\n-- sensitive: grantable, but warned --');
for (const p of ['explorer', 'chrome', 'msedge', 'firefox', 'outlook', 'slack', 'regedit', 'taskmgr']) {
  check(`${p} is sensitive`, classify(win(p)).tier === TIER.SENSITIVE, classify(win(p)).tier);
}
{
  const p = new Policy();
  const r = p.grant(win('chrome'));
  check('sensitive apps can be granted', r.ok === true);
  check('sensitive grant explains the reach', typeof r.reason === 'string' && r.reason.length > 20, r.reason);
  check('granted sensitive app can act', p.checkAct(win('chrome')).ok === true);
}

console.log('\n-- standard flow --');
{
  const p = new Policy();
  const w = win('myapp');
  check('unknown apps are standard', classify(w).tier === TIER.STANDARD);
  check('reading needs no grant', p.checkRead(w).ok === true);
  check('acting without a grant is refused', p.checkAct(w).ok === false);
  check('refusal code is not_granted', p.checkAct(w).code === 'not_granted');
  check('refusal names the tool to call', /computer_grant/.test(p.checkAct(w).hint));
  p.grant(w);
  check('acting after a grant is allowed', p.checkAct(w).ok === true);
  check('revoke re-locks', p.revoke('myapp') === true && p.checkAct(w).ok === false);
}

console.log('\n-- grants key on the app, not the window --');
{
  const p = new Policy();
  p.grant(win('myapp', { hwnd: 1 }));
  check('a second window of a granted app is covered', p.checkAct(win('myapp', { hwnd: 2 })).ok === true);
  check('a different app is not covered', p.checkAct(win('otherapp')).ok === false);
}

console.log('\n-- own session is excluded --');
{
  const p = new Policy();
  p.markSelf([4242]);
  const self = win('node', { pid: 4242 });
  check('self window is flagged', p.isSelf(self) === true);
  check('self window is unreadable', p.checkRead(self).ok === false);
  check('self refusal has its own code', p.checkRead(self).code === 'self_window');
}

console.log('\n-- revokeAll --');
{
  const p = new Policy();
  p.grant(win('a')); p.grant(win('b')); p.grant(win('c'));
  check('revokeAll reports the count', p.revokeAll() === 3);
  check('nothing is granted afterwards', p.listGrants().length === 0);
}

console.log('\n-- case and extension are normalised --');
check('.exe suffix ignored', classify({ process: 'KEEPASS.EXE', path: '' }).tier === TIER.BLOCKED);
check('path is used when process name is missing',
  classify({ process: null, path: 'C:\\x\\1password.exe' }).tier === TIER.BLOCKED);

console.log(`\n${pass} passed, ${fail} failed`);
if (fail) { console.log('failures: ' + failures.join(', ')); process.exit(1); }
