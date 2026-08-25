import io
p='server/native/AxonHost.cs'; s=io.open(p,encoding='utf-8').read()
def sub(o,n,l):
    global s
    if o not in s: raise SystemExit('MISS '+l)
    s=s.replace(o,n,1)

# Stop is checked before anything that acts, and honoured once.
sub("""        static void GuardSameWindow(Dictionary<string, object> a, IntPtr target)
        {""",
"""        // A press of Stop on the banner halts the run. It is consumed here so
        // one press stops one run, and the caller is told plainly.
        static void GuardStopped()
        {
            if (Overlay.ConsumeStop())
                throw new AxonError("stopped_by_user",
                    "The user pressed Stop on the on-screen banner.",
                    "They have taken the machine back. Do not retry: say what you had done so far and ask before continuing.");
        }

        static void GuardSameWindow(Dictionary<string, object> a, IntPtr target)
        {
            GuardStopped();
            Overlay.MarkActive();""",'stop guard')

# ops that do not route through GuardSameWindow still need the check
sub("""        static int GuardDisturb(Dictionary<string, object> a)
        {""",
"""        static int GuardDisturb(Dictionary<string, object> a)
        {
            GuardStopped();
            Overlay.MarkActive();""",'disturb stop')

sub("""            r["idle_threshold_ms"] = _idleMs;""",
"""            r["idle_threshold_ms"] = _idleMs;
            r["stop_requested"] = Overlay.StopRequested;""",'presence stop')
io.open(p,'w',encoding='utf-8').write(s)
print('stop wired into host')
