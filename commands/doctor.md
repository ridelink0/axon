---
description: Check that Axon's host is built and working, and report what it can see.
---

Diagnose the Axon installation and report the result concisely.

1. Call `axon_status` and report the host state, DPI mode, and any active grants.
2. Call `axon_apps` and report how many windows are visible and what tiers they fall into.
3. If the host failed to start, the error text names the cause. The usual ones:
   - not Windows - Axon drives the Windows UI Automation API and needs Windows
   - `csc.exe` missing - the .NET Framework 4.x runtime is not enabled
   - GAC assemblies missing - enable ".NET Framework 4.x Advanced Services" in Windows Features
   For a build problem, suggest running `node server/build.mjs --force` from the
   plugin directory to get the compiler's own output.
4. If `dpi mode` is anything other than `per-monitor-v2` or `per-monitor`, say so:
   coordinates may be offset on a scaled display, though tree and pattern
   targeting are unaffected.

Report findings as a short list. Do not take screenshots.
