import io
p='tools/presence-test.mjs'; s=io.open(p,encoding='utf-8').read()
old = "console.log('\n-- overlay and presence can each be turned off --');"
new = """// The Stop control on the banner has to be reachable and default to off.
check('stop state is reported', pres.stop_requested === false, JSON.stringify(pres.stop_requested));

""" + old
if old not in s: raise SystemExit('MISS')
io.open(p,'w',encoding='utf-8').write(s.replace(old,new,1))
print('ok')
