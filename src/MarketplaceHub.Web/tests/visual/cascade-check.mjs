// Proves a stylesheet refactor keeps every cascade outcome: compares the CSS of a git revision (default HEAD) with the
// working tree using the model in cascade.mjs. Prints the declarations that appeared or disappeared (those change
// styles on purpose and need a reason) and every competing pair whose winner changed. Exit code 1 on any problem.
//
//   node tests/visual/cascade-check.mjs [--base origin/main] [--limit 40]
import { execFileSync } from 'node:child_process'
import { resolve } from 'node:path'
import { collect, compareCascades, describe, workingTree } from './cascade.mjs'

const argument = (name, fallback) => { const at = process.argv.indexOf(`--${name}`); return at > 0 ? process.argv[at + 1] : fallback }
const base = argument('base', 'HEAD')
const limit = Number(argument('limit', 40))
const web = resolve(import.meta.dirname, '../..')
const git = (...args) => execFileSync('git', args, { cwd: web, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 })
const prefix = git('rev-parse', '--show-prefix').trim()
const revision = {
  read: file => git('show', `${base}:${prefix}${file}`),
  list: () => git('ls-tree', '-r', '--name-only', '--full-name', base, '--', 'src').split('\n').filter(file => /\.tsx?$/.test(file)).map(file => file.slice(prefix.length)),
}

const started = Date.now()
const before = collect(revision)
const after = collect(workingTree(web))
const { changed, problems, added, removed } = compareCascades(before, after)
const harmful = removed.filter(declaration => !declaration.dominated)
console.log(`${before.declarations.length} -> ${after.declarations.length} declarations; ${changed} identities moved or changed; ${added.length} added, ${removed.length} removed (${removed.length - harmful.length} always lost on their own selector), ${problems.length} competing pairs changed winner (${Math.round((Date.now() - started) / 1000)} s)`)
for (const declaration of added.slice(0, limit)) console.log(`  + ${describe(declaration)}`)
for (const declaration of harmful.slice(0, limit)) console.log(`  - ${describe(declaration)}`)
for (const problem of problems.slice(0, limit)) console.log(`  ! ${describe(problem.declaration)}\n      vs ${describe(problem.other)}\n      winner ${problem.before} -> ${problem.after}`)
process.exitCode = added.length || harmful.length || problems.length ? 1 : 0
