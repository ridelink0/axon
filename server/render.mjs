// Turns host JSON into the compact text the model actually reads.
//
// This layer is where the token win is banked. The host returns verbose JSON
// because it is cheap to produce and easy to test; only the rendered form
// crosses into the context window. Everything here is about saying less:
// a line per element, nothing a role already implies, nothing that repeats,
// and in a browser, the page rather than the browser.

const ACTIONABLE = new Set(['Invoke', 'Toggle', 'Value', 'SelectionItem', 'ExpandCollapse', 'Scroll', 'RangeValue']);

// Patterns a role already implies. A Button that can be invoked is just a
// button; printing [Invoke] after every one of them costs tokens and says
// nothing. Only a pattern a role does NOT imply is worth a tag - a Group that
// invokes, a Text that toggles.
const IMPLIED = {
  Button: ['Invoke', 'Toggle', 'ExpandCollapse'],
  Hyperlink: ['Invoke', 'Value'],
  MenuItem: ['Invoke', 'ExpandCollapse', 'Toggle', 'SelectionItem'],
  TabItem: ['SelectionItem', 'Invoke'],
  ListItem: ['SelectionItem', 'Invoke', 'Toggle'],
  TreeItem: ['SelectionItem', 'ExpandCollapse', 'Invoke', 'Toggle'],
  DataItem: ['SelectionItem', 'Invoke'],
  HeaderItem: ['Invoke'],
  CheckBox: ['Toggle', 'Invoke'],
  RadioButton: ['SelectionItem', 'Invoke'],
  Edit: ['Value'],
  ComboBox: ['Value', 'ExpandCollapse', 'SelectionItem'],
  Document: ['Value', 'Scroll'],
  SplitButton: ['Invoke', 'ExpandCollapse'],
  Slider: ['RangeValue', 'Value'],
  Spinner: ['RangeValue', 'Value'],
  ScrollBar: ['RangeValue'],
  ProgressBar: ['RangeValue', 'Value'],
  List: ['Scroll'], Tree: ['Scroll'], DataGrid: ['Scroll'], Table: ['Scroll'],
  Pane: ['Scroll'], Window: ['Scroll'],
};

function rect(r) {
  if (!r) return '';
  return ` (${r[0]},${r[1]} ${r[2]}x${r[3]})`;
}

function flat(s) {
  return s == null ? '' : String(s).replace(/\s+/g, ' ').trim();
}

function oneLine(s, max) {
  const f = flat(s);
  if (f.length <= max) return f;
  return f.slice(0, max - 1) + '…';
}

// Long text is shown as head plus tail rather than a plain truncation, so the
// end of a scrolled-away document stays visible - that content is the whole
// reason for reading the tree instead of taking a picture - and the character
// count tells the model when it is worth asking for more.
function preview(s, limit) {
  const f = flat(s);
  if (f.length <= limit) return JSON.stringify(f);
  const head = Math.max(40, Math.floor(limit * 0.6));
  const tail = Math.max(20, limit - head);
  return `${JSON.stringify(f.slice(0, head))} … ${JSON.stringify(f.slice(-tail))} [${f.length} chars]`;
}

// A Text node whose whole content is one separator glyph is layout, not
// content: the dots between footer links, the pipes in a breadcrumb.
const SEPARATOR = /^[\s·•|\-–—,:;/\\*]+$/;

const URLISH = /^(https?:\/\/|mailto:|tel:|file:)/i;

function pageHost(url) {
  try { return new URL(url).host; } catch { return null; }
}

// A link on the same site is shown as its path; a link elsewhere in full.
function shortHref(href, host) {
  try {
    const u = new URL(href);
    if (host && u.host === host) {
      const p = u.pathname + u.search + u.hash;
      return p === '/' ? '/' : p;
    }
    return href;
  } catch { return href; }
}

// Past this many characters a read is worth a sentence about how to make the
// next one smaller. Roughly 3,000 tokens: still far under a screenshot, but
// enough that a conversation full of them is what fills a context window.
const CHATTY = 12000;

// In a browser, the page is what matters. Everything outside a Document -
// the tab strip, toolbar, bookmarks bar, sidebar - is the browser's own UI,
// and there are sixty-odd controls of it on every read. Only the tabs and the
// address field earn their place by default; `chrome: true` shows the rest.
function splitBrowser(nodes) {
  const page = new Set();
  let docDepth = -1;
  for (const n of nodes) {
    if (docDepth >= 0 && n.depth > docDepth) { page.add(n.i); continue; }
    docDepth = -1;
    if (n.role === 'Document') { page.add(n.i); docDepth = n.depth; }
  }
  return page;
}

function keepChrome(n) {
  if (n.role === 'TabItem') return true;
  if (n.role === 'Edit' && n.text && URLISH.test(flat(n.text))) return true;
  return false;
}

export function renderSnapshot(snap, { textLimit = 200, withRects = false, lean = false, chrome = false, ms = null } = {}) {
  const nodes = snap.nodes || [];
  const page = snap.web && !chrome ? splitBrowser(nodes) : null;
  const hasPage = page && page.size > 0;

  // The page URL lives in the Document's value in every Chromium browser.
  let url = null;
  if (hasPage) {
    const doc = nodes.find((n) => n.role === 'Document' && n.text && URLISH.test(flat(n.text)));
    if (doc) url = flat(doc.text);
  }
  const host = url ? pageHost(url) : null;

  let pruned = 0;
  let hiddenChrome = 0;
  const rows = [];
  // Indentation follows the ancestors actually shown, so a control twenty
  // wrapper-divs deep does not arrive with twenty levels of spaces in front.
  const shown = [];

  for (const n of nodes) {
    if (hasPage && !page.has(n.i) && !keepChrome(n)) { hiddenChrome++; continue; }

    const acts = (n.patterns || []).filter((p) => ACTIONABLE.has(p));
    const st = n.state || {};
    const name = flat(n.name);
    const txt = flat(n.text);
    const bare = !name && !n.aid && !txt && acts.length === 0;
    if (bare) { pruned++; continue; }
    if (n.role === 'Text' && !n.aid && acts.length === 0 && SEPARATOR.test(name) && !txt) { pruned++; continue; }

    while (shown.length && shown[shown.length - 1] >= n.depth) shown.pop();
    const indent = '  '.repeat(Math.min(shown.length, 8));
    shown.push(n.depth);

    let line = `[${n.i}]${indent} ${n.role}`;
    if (name) line += ` "${oneLine(name, 70)}"`;
    if (n.aid && n.aid !== name && !/^view_\d+$/.test(n.aid)) line += ` #${n.aid}`;
    if (withRects) line += rect(n.rect);

    const implied = IMPLIED[n.role] || [];
    const tags = acts.filter((p) => !implied.includes(p));
    if (tags.length) line += ` [${tags.join(',')}]`;

    const flags = [];
    if (st.disabled) flags.push('disabled');
    if (st.offscreen) flags.push('offscreen');
    if (st.focused) flags.push('focused');
    if (st.toggle) flags.push(`toggle=${st.toggle}`);
    if (st.selected === true) flags.push('selected');
    if (st.expand) flags.push(st.expand.toLowerCase());
    if (st.value !== undefined) flags.push(`value=${st.value}`);
    if (flags.length) line += ` {${flags.join(' ')}}`;

    // Text that merely repeats the name is noise on every label; a link's
    // href goes inline, shortened when it stays on this site; the page URL
    // is already in the header.
    if (txt && txt !== name) {
      if (n.role === 'Hyperlink' && URLISH.test(txt)) {
        line += ` -> ${shortHref(txt, host)}`;
      } else if (n.role === 'Document' && txt === url) {
        // header has it
      } else if (txt.length <= 60) {
        line += ` = ${JSON.stringify(txt)}`;
      } else {
        line += `\n${indent}      = ${preview(n.text, textLimit)}`;
      }
    }
    rows.push(line);
  }

  const head = [`${snap.snapshot_id} "${snap.title || '(untitled)'}" hwnd=${snap.hwnd}`];
  if (url) head.push(`url ${url}`);
  let count = `${rows.length} shown`;
  if (hiddenChrome) count += `, ${hiddenChrome} browser controls hidden (chrome:true)`;
  else if (pruned) count += `, ${pruned} layout-only hidden`;
  if (snap.time_budget_ms) {
    count += `, TRUNCATED after ${snap.time_budget_ms}ms - slow tree; use interactive_only or a smaller window`;
  } else if (snap.truncated) {
    count += ', TRUNCATED (raise max_nodes or use interactive_only)';
  }
  head.push(count);
  if (ms != null) head.push(`${ms}ms`);
  if (snap.other_desktop) {
    // Windows serves only a cloaked window's frame - title bar, minimise,
    // maximise, close - and none of its contents, so what comes back looks
    // like an application with nothing in it. Saying why is the difference
    // between a dead end and a next step.
    head.push('ON ANOTHER VIRTUAL DESKTOP: only the frame is readable (Windows does not serve the contents of a window on a desktop you are not looking at); its coordinates are not on screen, so do not click by point. Bring that desktop forward first');
  }

  const out = [head.join(' | '), '', ...rows].join('\n');
  // Said in the output rather than in the docs, because the moment it matters
  // is the moment a big page has just been read - not before.
  if (!lean && out.length > CHATTY) {
    return out + `\n\n[~${Math.round(out.length / 4)} tokens. Re-read with interactive_only:true for controls only, or target one part with a selector.]`;
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
