// Batched runs and background tasks.
//
// One MCP round trip per step is what makes computer use slow and expensive:
// the model reads, thinks, acts, reads again, and every one of those is a
// turn. A run is a list of steps the server executes back to back through the
// same handlers a single call would use - same grants, same tiers, same
// confirmation gate, same input lease - stopping at the first failure. Run in
// the background, it returns a task id at once so the model can do other work
// while the desktop side proceeds.

const STEP_KINDS = ['click', 'type', 'key', 'scroll', 'wait_for', 'snapshot', 'sleep', 'focus'];

// Normalises one step object into { kind, args }. Accepts the short forms
// {key:"ctrl+s"} and {sleep:500} as well as the object forms.
export function parseStep(step, n) {
  if (!step || typeof step !== 'object' || Array.isArray(step)) {
    throw new Error(`step ${n} must be an object such as {click:{index:5}}`);
  }
  const kinds = Object.keys(step).filter((k) => STEP_KINDS.includes(k));
  if (kinds.length !== 1) {
    throw new Error(`step ${n} needs exactly one of ${STEP_KINDS.join(', ')}`);
  }
  const kind = kinds[0];
  let v = step[kind];
  if (kind === 'key' && typeof v === 'string') v = { keys: v };
  if (kind === 'sleep' && typeof v === 'number') v = { ms: v };
  if (kind === 'type' && typeof v === 'string') v = { text: v };
  if (kind === 'wait_for' && typeof v === 'string') v = { text: v };
  if (v === true || v == null) v = {};
  if (typeof v !== 'object') throw new Error(`step ${n}: ${kind} takes an object`);
  // Per-step overrides such as confirmed, mode or background live on the step.
  const extras = {};
  for (const k of ['confirmed', 'mode', 'background', 'physical']) if (step[k] !== undefined) extras[k] = step[k];
  return { kind, args: { ...extras, ...v } };
}

// One line about a step, from the handler's text. Presence and peer-listing
// lines are dropped (they were said on the run as a whole); a WARNING about
// another session in the same window is not.
export function briefResult(text, max = 220) {
  const lines = String(text || '').split('\n')
    .filter((l) => l.trim() && !/^\[(user active|\d+ other Claude session)/.test(l.trim()));
  let s = lines.join(' ').replace(/\s+/g, ' ').trim();
  if (s.length > max) s = s.slice(0, max - 1) + '…';
  return s;
}

function describeStep(kind, args) {
  const tgt = args.index != null ? `[${args.index}]`
    : args.selector ? (args.selector.automation_id ? `#${args.selector.automation_id}` : args.selector.name ? `"${args.selector.name}"` : args.selector.role || 'selector')
    : args.point ? `(${args.point.join(',')})` : '';
  switch (kind) {
    case 'click': return `click ${tgt}`.trim();
    case 'type': return `type ${tgt}${args.replace ? ' replace' : ''} ${JSON.stringify(String(args.text || '').slice(0, 30))}`.trim();
    case 'key': return `key ${args.keys}`;
    case 'scroll': return `scroll ${tgt} ${args.amount != null ? args.amount : ''}`.trim();
    case 'wait_for': return `wait_for ${args.text ? `text ${JSON.stringify(args.text)}` : args.change ? 'change' : args.new_window ? 'new window' : args.selector ? `${args.gone ? 'gone ' : ''}${tgt}` : ''}`.trim();
    case 'snapshot': return `snapshot${args.find ? ` find ${JSON.stringify(args.find)}` : ''}${args.index != null ? ` [${args.index}]` : ''}`;
    case 'sleep': return `sleep ${args.ms}ms`;
    case 'focus': return 'focus';
  }
  return kind;
}

export class Tasks {
  constructor() {
    this.seq = 0;
    this.tasks = new Map();
  }

  // Executes steps in order. `call(kind, args)` runs one step through the
  // server's own handler and returns { text, isError }. Snapshot steps keep
  // their full text; everything else is one line.
  async runSteps(steps, hwnd, call, { stopOnError = true, task = null } = {}) {
    const lines = [];
    let failed = 0;
    let stoppedAt = -1;
    for (let n = 0; n < steps.length; n++) {
      if (task && task.cancelled) { stoppedAt = n; break; }
      let parsed;
      try { parsed = parseStep(steps[n], n + 1); }
      catch (err) {
        lines.push(`${n + 1} INVALID: ${err.message}`);
        failed++;
        stoppedAt = n + 1;
        break;
      }
      const { kind, args } = parsed;
      const started = Date.now();
      let r;
      if (kind === 'sleep') {
        const ms = Math.max(0, Math.min(Number(args.ms) || 0, 30000));
        await new Promise((res) => setTimeout(res, ms));
        r = { text: `slept ${ms}ms`, isError: false };
      } else {
        r = await call(kind, { ...args, hwnd });
      }
      const ms = Date.now() - started;
      const label = `${n + 1} ${describeStep(kind, args)}`;
      if (r.isError) {
        failed++;
        lines.push(`${label}: FAILED ${briefResult(r.text, 300)}`);
        if (task) task.lines = lines.slice();
        if (stopOnError) { stoppedAt = n + 1; break; }
      } else if (kind === 'snapshot') {
        lines.push(`${label}: (${ms}ms)\n${r.text}`);
      } else {
        lines.push(`${label}: ${briefResult(r.text)} (${ms}ms)`);
      }
      if (task) { task.lines = lines.slice(); task.done = n + 1; }
    }
    if (stoppedAt >= 0 && stoppedAt < steps.length) {
      lines.push(`stopped: ${steps.length - stoppedAt} step(s) not run`);
    }
    return { lines, failed, stoppedAt, ran: stoppedAt >= 0 ? stoppedAt : steps.length };
  }

  start(steps, hwnd, title, call, opts) {
    const id = 't' + (++this.seq);
    const task = {
      id, hwnd, title, total: steps.length, done: 0, lines: [], status: 'running',
      cancelled: false, startedAt: Date.now(), finishedAt: null, result: null, tail: '',
    };
    this.tasks.set(id, task);
    task.promise = this.runSteps(steps, hwnd, call, { ...opts, task })
      .then(async (r) => {
        task.result = r;
        task.lines = r.lines;
        if (opts && opts.after) { try { task.tail = await opts.after(); } catch (e) { task.tail = `(no closing read: ${e && e.message})`; } }
        task.status = task.cancelled ? 'cancelled' : r.failed ? 'failed' : 'done';
      })
      .catch((err) => {
        task.lines.push(`crashed: ${err && err.message ? err.message : String(err)}`);
        task.status = 'failed';
      })
      .finally(() => { task.finishedAt = Date.now(); });
    // Bound the registry; finished tasks older than the last twenty go.
    while (this.tasks.size > 20) {
      const oldest = [...this.tasks.values()].find((t) => t.status !== 'running');
      if (!oldest) break;
      this.tasks.delete(oldest.id);
    }
    return task;
  }

  get(id) {
    if (id) return this.tasks.get(String(id)) || null;
    // No id: the most recent task.
    let last = null;
    for (const t of this.tasks.values()) last = t;
    return last;
  }

  async wait(task, ms) {
    if (task.status !== 'running' || ms <= 0) return task;
    await Promise.race([task.promise, new Promise((r) => setTimeout(r, ms))]);
    return task;
  }

  describe(task) {
    const secs = Math.round(((task.finishedAt || Date.now()) - task.startedAt) / 1000);
    const head = `task ${task.id} on "${task.title}" (hwnd ${task.hwnd}): ${task.status}, ${task.done}/${task.total} steps, ${secs}s`;
    const body = task.lines.length ? '\n' + task.lines.join('\n') : '';
    const tail = task.tail ? '\n\n' + task.tail : '';
    return head + body + tail;
  }

  running() {
    return [...this.tasks.values()].filter((t) => t.status === 'running');
  }
}
