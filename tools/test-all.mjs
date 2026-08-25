// Runs every Axon suite in order and reports one verdict.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const suites = ['policy-test.mjs', 'host-test.mjs', 'mcp-test.mjs'];

let failed = 0;
const totals = [];
for (const s of suites) {
  console.log(`\n${'='.repeat(60)}\n  ${s}\n${'='.repeat(60)}`);
  const r = spawnSync(process.execPath, [path.join(HERE, s)], { stdio: 'inherit' });
  if (r.status !== 0) failed++;
  totals.push(`${s}: ${r.status === 0 ? 'PASS' : 'FAIL'}`);
}
console.log(`\n${'='.repeat(60)}`);
for (const t of totals) console.log('  ' + t);
process.exit(failed ? 1 : 0);
