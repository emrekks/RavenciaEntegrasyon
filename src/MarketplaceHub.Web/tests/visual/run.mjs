// npm run test:visual [-- --update] [-- --no-build] [-- --only orders,returns] [-- --widths 375,1280]
//
// Builds the panel, captures every scenario, and compares with the baseline in tests/visual/.out/baseline. The first
// run (or --update) writes the baseline instead: capture it on the commit you start from, then run again after a change.
import { spawnSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { rm } from 'node:fs/promises'
import { join, resolve } from 'node:path'

const web = resolve(import.meta.dirname, '../..')
const argv = process.argv.slice(2)
const flag = name => argv.includes(`--${name}`)
const value = name => { const at = argv.indexOf(`--${name}`); return at >= 0 ? argv[at + 1] : undefined }
const outRoot = join(web, 'tests', 'visual', '.out')
const baseline = resolve(web, value('baseline') ?? join(outRoot, 'baseline'))
const current = join(outRoot, 'current')
const run = (command, args) => {
  const result = spawnSync(command, args, { cwd: web, stdio: 'inherit', shell: process.platform === 'win32' })
  if (result.status) process.exit(result.status)
}

if (!flag('no-build')) run('npx', ['vite', 'build', '--logLevel', 'warn'])
const passOn = ['only', 'widths', 'workers', 'dist'].flatMap(name => value(name) ? [`--${name}`, value(name)] : [])
if (flag('update') || !existsSync(baseline)) {
  await rm(baseline, { recursive: true, force: true })
  run('node', ['tests/visual/capture.mjs', '--out', baseline, ...passOn])
  console.log(`Baseline written to ${baseline}. Make the change, then run npm run test:visual again.`)
} else {
  await rm(current, { recursive: true, force: true })
  run('node', ['tests/visual/capture.mjs', '--out', current, ...passOn])
  run('node', ['tests/visual/diff.mjs', baseline, current])
}
