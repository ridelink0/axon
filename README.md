# computer-use

Semantic computer use for Claude Code, on Windows and macOS.

---

## Read this before you install it

> **WARNING — this plugin lets Claude control your computer. It clicks real
> buttons in your real applications and types into your real documents.**
>
> There is no undo. If Claude clicks Delete, the thing is deleted. If it types
> into the wrong window, that text is in the wrong window. If a web page you
> told it to read contains text designed to fool it, it has been fooled while
> holding your mouse.
>
> **Only install this if all of these are true:**
>
> - You are comfortable with software that moves your cursor and presses keys.
> - You will be at the machine, watching, the first several times you use it.
> - The work you care about is saved, committed, or backed up first.
> - You are the owner of this computer and nobody else's data is on it.
>
> **Do not install this** on a work machine you do not administer, on a machine
> holding anyone else's data, or on anything you would not hand to a careful but
> literal-minded stranger for ten minutes.
>
> Computer Use refuses to touch password managers, UAC prompts and login screens, and it
> has no ability to kill a process. Those limits are real and they are enforced
> in code. They are not a substitute for supervising it.
>
> MIT licensed, which means no warranty. If it breaks something, that is on you.

---

Most computer-use agents look at **pixels**. They take a screenshot, ask a model
to find the button in the picture, and click where it guesses. That is expensive,
it breaks when you change your display scaling, and it cannot see anything that
is scrolled out of view.

This one reads the **accessibility tree** — the same structured data a screen
reader uses. It gets the real name and identity of every control, so it does not
guess coordinates, and it can invoke a button through its own accessibility
action without moving your mouse at all.

That last part is what makes the next bit possible.

## It gets out of your way

You can keep working while it works. Same desktop, same keyboard, same mouse.

Windows stamps every synthetic input event with a flag in the kernel that the
injecting process cannot clear. So Computer Use can tell, exactly and continuously, which
input came from you and which came from itself. macOS gets the same thing a
different way.

It shows you what it touched, too: a small marker in Claude’s colour flashes
around each control as it acts. Computer Use never touches your system cursor, so it
cannot leave it in a bad state, which is the [standing Codex bug on
Windows](https://github.com/openai/codex/issues/25200). The marker is
click-through, never takes focus, stays out of alt-tab, and is excluded from
screen capture so Computer Use never photographs its own UI. `CU_OVERLAY=off` if you
would rather not see it.

Once it knows that, it can behave:

- Reading a window never touches your input at all, so it can look at anything
  any time, including windows that are behind others.
- Pressing a button through its accessibility action does not move your cursor
  or steal your focus, so most of what it does you will never feel.
- When it genuinely needs the pointer, it waits for a gap in your typing, takes
  it, and puts the pointer back where you left it.
- If you are actively typing in a window, it will not touch that window. It goes
  and does something else.

If the input hooks it uses are blocked - security tooling sometimes throttles a
freshly compiled unsigned binary that installs global hooks - it falls back to a
hook-free idle check and says which one it is using in `computer_status`. It never
pretends to know you are there when it does not.

Measured during the test suite: Computer Use fired 36 input events, and the presence
tracker counted **zero** user activity from them.

```
injected +36, real +0, idle now 3443ms
```

That is the whole feature in one line. It knows the difference.

Codex does not do this on Windows. Its own documentation says so:

> "On Windows, the model is simpler and more constrained: Codex takes over the
> foreground, and you cannot use the same desktop session while a Computer Use
> task runs."

There is no idle detection and no user-disturbance prevention there. On macOS it
solves the problem the other way, with a sandboxed second session. Computer Use solves it
on the shared desktop, on both.

## The cost difference

| | tokens |
|---|---:|
| Reading a window as a tree | **~457** |
| The same window as a screenshot | **~10,600** |

**Twenty-three times.** Run `node tools/mcp-test.mjs`; it prints this at the end
of every run, on your machine, with your windows.

And the tree is not just cheaper, it is better:

| Screenshot | Tree |
| --- | --- |
| Guesses coordinates from pixels | Knows the control's name and id |
| Breaks at 125% display scaling | Scaling makes no difference |
| Sees only what is visible | Includes text scrolled out of view |
| Needs the window in front | Reads windows sitting behind others |
| A wrong guess clicks the wrong thing | A dead element returns `element_stale` |

Pixels still win for canvas apps — image editors, games, charts, design tools —
so `computer_screenshot` is there. It is just not the default any more.

## Install

```
/plugin marketplace add ridelink0/claude-computer-use
/plugin install computer-use@computer-use
```

Windows or macOS, and Node 18+. Nothing else. No npm install, no Python, no
native module, no download.

The host is a small program that talks to the OS accessibility API. On first run
it is compiled on your machine by a compiler you already have — `csc.exe` from
the .NET Framework on Windows, `swiftc` from the Xcode tools on macOS.

It ships as **source, not a binary**. What runs on your machine is exactly what
you can read in `server/native/`. Nothing is downloaded.

## Using it

```
computer_apps                                find the window, note its hwnd
computer_snapshot { hwnd }                   read it, every element gets an index
computer_grant { hwnd }                      needed to act, never to read
computer_click { index: 12 }
computer_type { index: 5, text: "...", replace: true }
```

Every acting tool takes a `mode`:

| mode | what it does |
| --- | --- |
| `share` | default. Waits for a gap before using your cursor or focus. |
| `yield` | refuses outright while you are active. |
| `take` | interrupts you. For when you asked it to drive and are watching. |
| `exclusive` | like `take`, and also holds your mouse and keyboard off for the length of each action. |

### Working behind you, on a window you can't see

This is the default, and it is the whole point. You can be typing in a document
in front while Claude operates a different app sitting behind it. Clicking a
control through its accessibility action, and filling a field through its value
pattern, **do not raise the window** — the stack stays exactly as you left it,
your foreground app stays in front, and your keystrokes keep going where you are
looking. Reading a window never disturbs anything either.

Only three things need a window in front, and Claude saves them for when you are
idle: raw typing with no target element, keyboard chords, and the rare
physical-click fallback for a control that exposes no accessibility action.

### The two ways to take it back

- **The Stop button** on the "Claude is using your computer" banner. It aborts
  the run and withdraws every input grant, so nothing can act again without a
  fresh grant.
- **Escape.** Always releases everything and halts the run, even mid-action.
  This is the guaranteed escape hatch: the key hook lives in the same process
  that would be holding your input, and that process still sees Escape even while
  everything else is blocked. On top of that, any input hold releases itself
  after a few seconds no matter what, Windows releases it the instant the host
  process exits, and Ctrl+Alt+Del can never be blocked by anything.

### About `exclusive` mode

`exclusive` holds your physical mouse and keyboard off while each action runs, so
your hand cannot land in the middle of a click. It needs Claude Code to be
running **elevated** — Windows refuses to block input from an ordinary process,
and when that happens the action still runs, just without the hold, and the
result says `exclusive_unavailable`. Escape releases it regardless. It is
opt-in and never the default, precisely because a locked-out user cannot reach
the Stop button — Escape is why it is safe to offer at all.

Twelve tools, about **1,380 tokens** of always-on cost. One screenshot you did
not take pays for that eight times over.

## What it will not do

Reading is free. Acting needs a grant, per app, for that session only.

| tier | behaviour |
| --- | --- |
| `standard` | grant and go |
| `sensitive` | grantable, but it tells Claude what that app reaches. A browser carries every session you are signed in to. |
| `shell` | terminals and editors. Readable, **never typeable** — anything typed there runs as you. |
| `blocked` | password managers, UAC prompts, login screens. **Not even readable**, because their accessibility tree has the secrets in plain text. |

**It cannot kill a process.** There is no such tool. `computer_close_window` asks one
window to close, exactly as clicking its X does, so the app can still offer to
save.

That one is there because of a real accident while building this. Terminating
what looked like a spare Notepad launcher destroyed an unrelated unsaved note,
because Windows 11 Notepad runs every window inside one shared process. A PID is
not a window. Computer Use does not get to make that mistake.

Your Claude Code session is excluded from its own listings, so text on your
screen cannot loop back into the model as if it had observed it. Add your own
blocked apps in the plugin settings; that list only ever grows.

## What it does not do

- **Linux.** Would need an AT-SPI driver. Does not exist yet.
- **Canvas apps.** Roughly one target in twenty has no accessible structure.
  Use `computer_screenshot` and point targeting there.
- **No elevation, no UAC, no process control.** Deliberate.
- **macOS is a port I could not test.** See below.

## About the macOS host

Straight about this: the Windows host has 190 tests and I have driven real
applications with it end to end. The macOS host was written on a Windows machine
and has never been compiled or run.

The design is sound — same protocol, AXUIElement instead of UI Automation, a
listen-only event tap instead of low-level hooks — but sound is not tested.

Before trusting it:

```
node server/build.mjs --self-test
```

If it does not compile you get the compiler's own output, not a mystery. If that
happens, [open an issue](https://github.com/ridelink0/claude-computer-use/issues) with it and I
will fix it.

## Tests

```
node tools/test-all.mjs        all 224
node tools/verify.mjs         28  drives real windows end to end, cursor never moves
node tools/policy-test.mjs      57  tiers, grants, refusals
node tools/presence-test.mjs    25  telling you apart from Computer Use, overlay, modes
node tools/build-test.mjs       13  compile, idempotence, concurrent builds
node tools/host-test.mjs        52  tree, patterns, input, crash recovery
node tools/mcp-test.mjs         49  the protocol end to end, prints token costs
```

They build their own throwaway window and never touch anything already open.
Past the happy paths they cover stale indices, a host killed mid-session, five
concurrent calls, a window covered by another window, minimized windows, two
sessions compiling at once, and every error code.

## Where the idea came from

This is a study of what makes Codex's computer use good, rebuilt from scratch.

The lineage runs through **Sky** (Software Applications Incorporated, bought by
OpenAI in October 2025) and before it **Workflow**, which became Apple Shortcuts.
Sky's insight was that an agent should know what actions an app *affords*, not
just what pixels it is showing, and that it should read a window whether or not
that window is in front. Its "Skyshot" — a picture of a window plus a textual
representation of it — became Codex's "Appshot", and is the direct ancestor of
`computer_snapshot`.

No code is shared with any of them.

## License

MIT. No warranty. Read the warning at the top again.
