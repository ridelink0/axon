# Axon

**Semantic computer use for Windows, as a Claude Code plugin.**

Axon lets Claude drive Windows desktop applications by reading their
accessibility tree — the same structured data screen readers use — instead of
squinting at screenshots. Targeting becomes exact, DPI-proof, and roughly
**20x cheaper in tokens**.

It **adds to** Claude Code's built-in computer use. It does not wrap, patch, or
replace it. On Windows there is nothing to replace: [Claude Code's CLI computer
use is macOS-only](https://code.claude.com/docs/en/computer-use). Axon fills
that gap.

---

## Why tree-first

Most computer-use agents work like this: screenshot → model looks at pixels →
model guesses coordinates → click. That is expensive, fragile, and blind to
anything scrolled out of view.

Axon works like this: read the UI Automation tree → target an element by
identity → invoke its accessibility pattern. No pixels involved unless you ask.

Measured on the reference test window in this repo:

| | tokens | notes |
|---|---:|---|
| `axon_snapshot` of a window | **~459** | every element, with names, ids, states, and text |
| `axon_snapshot` interactive only | **~405** | just what you can act on |
| `axon_screenshot` of the same window | **~10,153** | one JPEG, downscaled, q60 |

**22x.** Reproduce it yourself with `node tools/mcp-test.mjs`, which prints the
cost summary at the end of every run.

The tree is also *better*, not just cheaper:

- **Exact identity.** `IUIAutomationElement.FindFirst` + pattern invoke resolves
  in single-digit milliseconds. No coordinate guessing, no missed clicks.
- **DPI, theme, and resolution proof.** Selectors carry semantic identity
  (role, AutomationId, name), so nothing breaks at 125% scaling or in dark mode.
- **Sees what pixels cannot.** A snapshot includes text scrolled *outside* the
  viewport. A screenshot by definition cannot.
- **Works on background windows.** Reading a window's tree does not raise it or
  steal your focus.
- **Fails loudly.** A stale element returns `element_stale`, not a click at
  whatever now occupies those coordinates.

Vision still wins for canvas-drawn surfaces — image editors, games, charts,
design tools — so `axon_screenshot` is there when the tree genuinely cannot
help. That split is the whole design: route each look to the cheapest surface
that can actually answer it.

---

## Install

```
/plugin marketplace add ridelink0/axon
/plugin install axon@axon
```

Requires **Windows** and **Node 18+**. Nothing else — no npm install, no Python,
no native addon, no download.

### How the host is built

Axon's host is a small C# program that talks to the UI Automation COM API. On
first run it is compiled locally by `csc.exe`, the C# compiler that ships inside
every Windows .NET Framework install, into your plugin data directory.

It is shipped as **source, not a binary** — [`server/native/AxonHost.cs`](server/native/AxonHost.cs)
is the whole thing, and what runs on your machine is exactly what you can read
there. The build is content-addressed, so it happens once and is a no-op after.

If the build fails, run `/axon:doctor`, or `node server/build.mjs --force` from
the plugin directory to see the compiler's own output.

---

## Using it

```
axon_apps                                  find the window, note its hwnd
axon_snapshot { hwnd }                     read it — every element gets an index
axon_grant { hwnd }                        only needed to act, not to read
axon_click { index: 12 }
axon_type { index: 5, text: "…", replace: true }
axon_wait_for { hwnd, selector: { name: "Done" } }
```

Full tool list: `axon_apps`, `axon_snapshot`, `axon_screenshot`, `axon_grant`,
`axon_focus`, `axon_click`, `axon_type`, `axon_key`, `axon_scroll`,
`axon_wait_for`, `axon_close_window`, `axon_status`.

The bundled skill teaches Claude the tree-first discipline, the targeting
hierarchy, and the error codes. The always-on cost of all twelve tool schemas is
about **1,200 tokens** — deliberately terse, with the teaching moved into the
skill so you only pay for it when it is used.

---

## Safety

Axon is deliberately narrower than it could be.

**Reading is free; acting needs a grant.** Every click, keystroke, and window
close requires `axon_grant` for that app, and grants last only for the session.
Reading a window never implies permission to touch it.

**Four tiers, and two of them are not negotiable:**

| tier | behaviour |
|---|---|
| `standard` | grant and go |
| `sensitive` | grantable, but the grant tells Claude what that app reaches — browsers carry every signed-in session, Explorer can delete anything |
| `shell` | terminals and editors: **readable, never typeable**. Anything typed there runs as you |
| `blocked` | password managers, UAC prompts, login screens: **not even readable**, because their accessibility tree contains the secrets in plain text |

**Axon cannot kill a process.** There is no such tool, by design.
`axon_close_window` sends `WM_CLOSE` to one specific window — the same thing
clicking its X does — so the app can still prompt to save. This exists because
of a real incident during development: terminating what looked like a spare
Notepad launcher destroyed an unrelated unsaved document, since Windows 11
Notepad shares one process across every window. Killing a PID discards work
without asking. Axon will not do it.

**Your Claude Code session is excluded** from listings and captures, so
on-screen text from the session cannot loop back into the model as if it were
observed content.

**On-screen text is treated as untrusted data.** Shell-tier reads carry an
explicit warning, and the skill instructs Claude to treat every string from a
window as information about the world, never as instructions.

Add your own blocked apps in the plugin's settings (`blocked_apps`). That list
is additive — it can extend the built-in blocklist, never shrink it.

---

## What it does not do

Being straight about the gaps:

- **Windows only.** The whole design sits on Windows UI Automation. On macOS,
  use Claude Code's built-in computer use.
- **Foreground input.** Reading works on background windows; clicking and typing
  do not. Axon moves the real cursor and focus, so you will see it working.
  OpenAI's Codex has background computer use with an isolated cursor on macOS —
  and notably [not on Windows either](https://learn.chatgpt.com/docs/computer-use),
  where it also takes over the active desktop. Axon does not pretend otherwise.
- **Canvas apps need pixels.** Roughly 5% of targets — games, image editors,
  design tools — have no accessible structure. Use `axon_screenshot` and point
  targeting there.
- **No process control, no elevation, no UAC.** Not oversights.

---

## Development

```
node tools/test-all.mjs        all 171 tests
node tools/policy-test.mjs      57  safety model: tiers, grants, refusals
node tools/build-test.mjs       13  local compile: cold build, idempotence, concurrent builds
node tools/host-test.mjs        52  compiled host: tree, patterns, input, crash recovery
node tools/mcp-test.mjs         49  MCP protocol end to end; prints the token cost summary
node server/build.mjs --force       rebuild the host
```

The host and MCP suites create their own throwaway target window and never
touch a pre-existing app. Beyond the happy paths they cover the things that
actually bite: stale snapshot indices, a dead host mid-session, five concurrent
calls, a window covered by another window, minimized windows, two sessions
compiling the host at the same time, and every typed error code.

There is also a behavioural eval (`claude plugin eval axon`) checking that
Claude reaches for the tree rather than a screenshot.

---

## Prior art and credit

The architecture is a deliberate study of what makes OpenAI Codex's computer use
good, rebuilt from scratch for Windows and for Claude Code.

That lineage runs through **Sky** (Software Applications Incorporated, acquired
by OpenAI in October 2025) and before it **Workflow**, which became Apple
Shortcuts. Sky's insight was that an agent should know what actions an app
*affords*, not just what pixels it is showing — reading window contents through
the accessibility layer regardless of whether the window is in front. Its
"Skyshot" (window image **plus** a textual representation of the window) became
Codex's "Appshot", and is the direct ancestor of `axon_snapshot`. The `app notes`
attached to grants are a scaled-down version of the same action-catalog idea.

Axon shares no code with any of them.

## License

MIT
