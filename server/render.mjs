// Turns host JSON into the compact text the model actually reads.
//
// This layer is where the token win is banked. The host returns verbose JSON
// because it is cheap to produce and easy to test; only the rendered form
// crosses into the context window.

const ACTIONABLE = new Set(['Invoke', 'Toggle', 'Value', 'SelectionItem', 'ExpandCollapse', 'Scroll', 'RangeValue']);

function rect(r) {
  if (!r) return '';
  return ` (${r[0]},${r[1]} ${r[2]}x${r[3]})`;
}

function oneLine(s, max) {
  if (s == null) return '';
  const flat = String(s).replace(/\s+/g, ' ').trim();
  if (flat.length <= max) return flat;
  return flat.slice(0, max - 1) + '…';
}

// Roles that carry no meaning on their own. One of these with no name, no id,
// no text and nothing to act on is pure layout scaffolding, and printing it
// costs tokens for nothing.
const STRUCTURAL = new Set(['Pane', 'Group', 'Custom', 'Separator', 'TitleBar', 'Thumb', 'Header']);

// Long text is shown as head plus tail rather than a plain truncation, so the
// end of a scrolled-away document stays visible - that content is the whole
// reason for reading the tree instead of taking a picture - and the character
// count tells the model when it is worth asking for more.
function preview(s, limit) {
  const flat = String(s).replace(/\s+/g, ' ').trim();
  if (flat.length <= limit) return JSON.stringify(flat);
  const head = Math.max(40, Math.floor(limit * 0.6));
  const tail = Math.max(20, limit - head);
  return `${JSON.stringify(flat.slice(0, head))} … ${JSON.stringify(flat.slice(-tail))} [${flat.length} chars]`;
}

// Past this many characters a read is worth a sentence about how to make the
// next one smaller. Roughly 3,000 tokens: still far under a screenshot, but
// enough that a conversation full of them is what fills a context window.
const CHATTY = 12000;

export function renderSnapshot(snap, { textLimit = 200, withRects = false, lean = false } = {}) {
  const lines = [];
  const head = [`${snap.snapshot_id} "${snap.title || '(untitled)'}" hwnd=${snap.hwnd}`];

  let pruned = 0;
  const rows = [];

  for (const n of snap.nodes) {
    const acts = (n.patterns || []).filter((p) => ACTIONABLE.has(p));
    const st = n.state || {};
    const bare = !n.name && !n.aid && !n.text && acts.length === 0;
    if (bare && STRUCTURAL.has(n.role)) { pruned++; continue; }

    const indent = '  '.repeat(Math.min(n.depth, 8));
    let line = `[${n.i}]${indent} ${n.role}`;
    if (n.name) line += ` "${oneLine(n.name, 70)}"`;
    if (n.aid && n.aid !== n.name) line += ` #${n.aid}`;
    if (withRects) line += rect(n.rect);
    if (acts.length) line += ` [${acts.join(',')}]`;

    const flags = [];
    if (st.disabled) flags.push('disabled');
    if (st.offscreen) flags.push('offscreen');
    if (st.focused) flags.push('focused');
    if (st.toggle) flags.push(`toggle=${st.toggle}`);
    if (st.selected === true) flags.push('selected');
    if (st.expand) flags.push(st.expand.toLowerCase());
    if (st.value !== undefined) flags.push(`value=${st.value}`);
    if (flags.length) line += ` {${flags.join(' ')}}`;

    if (n.text) {
      const flat = String(n.text).replace(/\s+/g, ' ').trim();
      // Text that merely repeats the name is noise on every label.
      if (flat && flat !== String(n.name || '').replace(/\s+/g, ' ').trim()) {
        line += `\n${indent}      = ${preview(n.text, textLimit)}`;
      }
    }
    rows.push(line);
  }

  // Windows serves only a cloaked window's frame - title bar, minimise,
  // maximise, close - and none of its contents, so what comes back looks like
  // an application with nothing in it. Saying why is the difference between a
  // dead end and a next step.
  const elsewhere = snap.other_desktop
    ? ' | ON ANOTHER VIRTUAL DESKTOP: only the window frame is readable, because Windows does not serve the contents of a window on a desktop you are not looking at. This is not an empty or broken app. Its coordinates are not on screen either, so a physical click or point target would land on whatever the user has in front of them. Bring that desktop forward before driving this window'
    : '';
  const cut = snap.time_budget_ms
    ? `, TRUNCATED after ${snap.time_budget_ms}ms - this window's tree is slow to read; use interactive_only, or snapshot a smaller window`
    : (snap.truncated ? ', TRUNCATED (raise max_nodes or use interactive_only)' : '');
  head.push(`${rows.length} shown${pruned ? `, ${pruned} layout-only hidden` : ''}${cut}${elsewhere}`);
  lines.push(head.join(' | '), '');
  lines.push(...rows);
  const out = lines.join('\n');
  // Said in the output rather than in the docs, because the moment it matters
  // is the moment a big page has just been read - not before.
  if (!lean && out.length > CHATTY) {
    return out + `\n\n[this read cost about ${Math.round(out.length / 4)} tokens. ` +
      'If you only need controls, re-read with interactive_only:true; if you need one part of ' +
      'the page, target it with a selector rather than reading the whole tree again.]';
  }
  return out;
}

export function renderApps(windows, policy, classify) {
  if (!windows.length) return 'No visible top-level windows.';
  const lines = ['hwnd         tier       app                 title'];
  for (const w of windows) {
    const { tier } = classify(w);
    const granted = policy.granted(w) ? '*' : ' ';
    const app = (w.process || '?').slice(0, 18).padEnd(18);
    const hwnd = String(w.hwnd).padEnd(12);
    const t = (tier + granted).padEnd(10);
    let title = oneLine(w.title, 60) || '(untitled)';
    if (w.minimized) title += ' [minimized]';
    if (w.other_desktop) title += ' [other virtual desktop]';
    if (w.foreground) title += ' [foreground]';
    lines.push(`${hwnd} ${t} ${app} ${title}`);
  }
  lines.push('');
  lines.push('* = granted for input this session. tier: standard | sensitive | shell (read-only) | blocked');
  return lines.join('\n');
}
