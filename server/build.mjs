// Compiles the Axon host from the C# source in this repo using the csc.exe
// that ships with the Windows .NET Framework. No SDK, no toolchain, no npm
// native addon, no download.
//
// The build is content-addressed: the compiled exe is stamped with a hash of
// its source and the reference set, so it rebuilds only when something actually
// changed and is a no-op on every later start.

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const SOURCE = path.join(HERE, 'native', 'AxonHost.cs');

export function dataDir() {
  // The compiled host lives in plugin data, not in the plugin directory, so it
  // survives plugin updates and never lands inside a git checkout.
  const fromEnv = process.env.AXON_PLUGIN_DATA || process.env.CLAUDE_PLUGIN_DATA;
  if (fromEnv && fromEnv.trim() && !fromEnv.includes('${')) return fromEnv.trim();
  return path.join(os.homedir(), '.claude', 'plugins', 'data', 'axon');
}

export function binDir() {
  return path.join(dataDir(), 'bin');
}

export function exePath() {
  return path.join(binDir(), 'AxonHost.exe');
}

const stampPath = () => path.join(binDir(), 'AxonHost.stamp');

function findCsc() {
  const win = process.env.WINDIR || process.env.SystemRoot || 'C:\\Windows';
  const candidates = [
    path.join(win, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    path.join(win, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ];
  for (const c of candidates) if (fs.existsSync(c)) return c;
  return null;
}

// UIAutomation and WindowsBase live only in the GAC on a stock Windows box -
// there is no Reference Assemblies folder unless a dev SDK is installed - so
// resolve them by walking the versioned GAC directories.
function findGacAssembly(name) {
  const win = process.env.WINDIR || process.env.SystemRoot || 'C:\\Windows';
  const roots = [
    path.join(win, 'Microsoft.NET', 'assembly', 'GAC_MSIL', name),
    path.join(win, 'assembly', 'GAC_MSIL', name),
  ];
  for (const root of roots) {
    if (!fs.existsSync(root)) continue;
    let versions;
    try { versions = fs.readdirSync(root); } catch { continue; }
    // Prefer v4.x, and the highest version string among those.
    versions.sort().reverse();
    const preferred = versions.filter((v) => v.startsWith('v4.')).concat(versions);
    for (const v of preferred) {
      const dll = path.join(root, v, name + '.dll');
      if (fs.existsSync(dll)) return dll;
    }
  }
  return null;
}

function computeStamp(csc, refs) {
  const h = createHash('sha256');
  h.update(fs.readFileSync(SOURCE));
  h.update('\0' + csc);
  for (const r of refs) h.update('\0' + r);
  h.update('\0v2');
  return h.digest('hex');
}

export class BuildError extends Error {
  constructor(message, hint) {
    super(message);
    this.code = 'build_failed';
    this.hint = hint;
  }
}

export function ensureHost({ force = false, log = () => {} } = {}) {
  if (process.platform !== 'win32') {
    throw new BuildError(
      'Axon needs Windows. It drives the Windows UI Automation API directly.',
      'On macOS use Claude Code\'s built-in computer use instead: enable `computer-use` in /mcp.'
    );
  }

  const csc = findCsc();
  if (!csc) {
    throw new BuildError(
      'Could not find the in-box C# compiler (csc.exe) under %WINDIR%\\Microsoft.NET.',
      'Axon needs the .NET Framework 4.x runtime, which is part of Windows. On a stripped image, enable it in Windows Features.'
    );
  }

  const needed = ['UIAutomationClient', 'UIAutomationTypes', 'WindowsBase'];
  const refs = [];
  const missing = [];
  for (const n of needed) {
    const p = findGacAssembly(n);
    if (p) refs.push(p);
    else missing.push(n);
  }
  if (missing.length) {
    throw new BuildError(
      'Missing .NET Framework assemblies in the GAC: ' + missing.join(', ') + '.',
      'These ship with the .NET Framework. Enable ".NET Framework 4.x Advanced Services" in Windows Features.'
    );
  }

  const exe = exePath();
  const stamp = computeStamp(csc, refs);

  if (!force && fs.existsSync(exe) && fs.existsSync(stampPath())) {
    try {
      if (fs.readFileSync(stampPath(), 'utf8').trim() === stamp) {
        log('host up to date');
        return { exe, rebuilt: false, csc };
      }
    } catch { /* fall through and rebuild */ }
  }

  fs.mkdirSync(binDir(), { recursive: true });
  log('compiling host with ' + csc);

  const args = [
    '-nologo',
    '-optimize+',
    '-target:exe',
    '-platform:anycpu',
    '-out:' + exe,
    ...refs.map((r) => '-r:' + r),
    '-r:System.Web.Extensions.dll',
    '-r:System.Drawing.dll',
    '-r:System.Windows.Forms.dll',
    SOURCE,
  ];

  const res = spawnSync(csc, args, { encoding: 'utf8', windowsHide: true });
  if (res.error) {
    throw new BuildError('Could not run the C# compiler: ' + res.error.message, null);
  }
  if (res.status !== 0) {
    const out = ((res.stdout || '') + (res.stderr || '')).trim();
    throw new BuildError('Compiling the Axon host failed.', out.slice(0, 2000));
  }
  if (!fs.existsSync(exe)) {
    throw new BuildError('The compiler reported success but produced no executable.', null);
  }

  fs.writeFileSync(stampPath(), stamp, 'utf8');
  log('host built at ' + exe);
  return { exe, rebuilt: true, csc };
}

// Allow `node build.mjs` for a manual/diagnostic build.
if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  try {
    const r = ensureHost({ force: process.argv.includes('--force'), log: (m) => console.log(m) });
    console.log(r.rebuilt ? 'built: ' + r.exe : 'already current: ' + r.exe);
  } catch (e) {
    console.error(e.message);
    if (e.hint) console.error(e.hint);
    process.exit(1);
  }
}
