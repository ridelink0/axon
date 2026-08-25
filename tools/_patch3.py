import io
p='server/native/AxonHost.cs'; s=io.open(p,encoding='utf-8').read()
def sub(o,n,l):
    global s
    if o not in s: raise SystemExit('MISS '+l)
    s=s.replace(o,n,1)
sub("                if (h == Overlay.Handle) return true;",
    "                if (h == Overlay.Handle || h == Overlay.ChromeHandle) return true;",'self chrome')
sub("                if (owner == Overlay.Handle) owner = expectWindow;",
    "                if (owner == Overlay.Handle || owner == Overlay.ChromeHandle) owner = expectWindow;",'obscured chrome')
io.open(p,'w',encoding='utf-8').write(s)

p='server/index.mjs'; s=io.open(p,encoding='utf-8').read()
old = """      if (err instanceof HostError) {
        reply(id, fail(err.code, err.message, err.hint));"""
new = """      if (err instanceof HostError) {
        // Stop means stop. Withdrawing every grant makes that structural rather
        // than advisory: nothing can act again until the user says so.
        if (err.code === 'stopped_by_user') {
          const n = policy.revokeAll();
          reply(id, fail(err.code, err.message,
            `${err.hint} All input permissions (${n}) have been withdrawn; acting again needs a fresh axon_grant.`));
          return;
        }
        reply(id, fail(err.code, err.message, err.hint));"""
if old not in s: raise SystemExit('MISS mcp stop')
s = s.replace(old,new,1)
io.open(p,'w',encoding='utf-8').write(s)
print('chrome excluded; stop revokes grants')
