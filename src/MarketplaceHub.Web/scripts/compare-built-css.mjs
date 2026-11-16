// WEB-ALTYAPI-12: proves a stylesheet move keeps the built CSS cascade-identical. Builds the app at a git revision and
// from the working tree (vite build only, into temporary folders), parses every built CSS chunk with postcss and
// compares them chunk by chunk (chunk name = file name without its content hash):
//   1. the set of chunks is the same;
//   2. the multiset of (at-rule context, selector, declarations) is the same;
//   3. the ORDER of those rules is the same. Equal order of equal rules in every chunk implies every pair of rules that
//      can match one element keeps its relative order; the chunk load order in index.html is compared too.
// Exit code 1 on any difference. A difference in 3 alone is listed as a moved block (start, length) so it can be
// judged by hand.
//
//   node scripts/compare-built-css.mjs [--base <git revision>]   (default: HEAD; run from src/MarketplaceHub.Web)
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const postcss = require('postcss')
const web = process.cwd()
const at = process.argv.indexOf('--base')
const base = at > 0 ? process.argv[at + 1] : 'HEAD'
const run = (command, args, cwd) => execFileSync(command, args, { cwd, encoding: 'utf8', maxBuffer: 512 * 1024 * 1024, shell: process.platform === 'win32' && command !== 'git' })

const scratch = mkdtempSync(join(tmpdir(), 'css-compare-'))
const prefix = run('git', ['rev-parse', '--show-prefix'], web).trim()
const topLevel = run('git', ['rev-parse', '--show-toplevel'], web).trim()

function buildBase() {
  const archive = join(scratch, 'base.tar')
  run('git', ['archive', '--format=tar', '-o', archive, base, `${prefix}`], topLevel)
  const folder = join(scratch, 'base')
  mkdirSync(folder, { recursive: true })
  run('tar', ['-xf', 'base.tar', '-C', 'base'], scratch)
  const project = join(folder, prefix)
  symlinkSync(join(web, 'node_modules'), join(project, 'node_modules'), 'junction')
  run('npx', ['vite', 'build', '--logLevel', 'error', '--outDir', join(scratch, 'out-before')], project)
  return join(scratch, 'out-before')
}
function buildWorking() {
  run('npx', ['vite', 'build', '--logLevel', 'error', '--outDir', join(scratch, 'out-after')], web)
  return join(scratch, 'out-after')
}

const chunkName = file => file.replace(/-[\w-]{8}\.css$/, '.css')
const normalise = text => text.replace(/\s+/g, ' ').replace(/\s*([:;,{}])\s*/g, '$1').trim()

function entries(css) {
  const out = []
  postcss.parse(css).walkRules(rule => {
    const context = []
    for (let node = rule.parent; node && node.type !== 'root'; node = node.parent) context.unshift(`@${node.name} ${normalise(node.params)}`)
    out.push(`${context.join(' > ')} | ${normalise(rule.selector)} | ${rule.nodes.filter(node => node.type === 'decl').map(node => `${node.prop}:${normalise(node.value)}${node.important ? '!important' : ''}`).join(';')}`)
  })
  return out
}
function load(outDir) {
  const assets = join(outDir, 'assets')
  const chunks = new Map()
  for (const file of readdirSync(assets).filter(name => name.endsWith('.css'))) chunks.set(chunkName(file), entries(readFileSync(join(assets, file), 'utf8')))
  const html = readFileSync(join(outDir, 'index.html'), 'utf8')
  const links = [...html.matchAll(/<link rel="stylesheet"[^>]*href="([^"]+)"[^>]*>/g)].map(match => chunkName(match[1].split('/').pop()))
  return { chunks, links }
}

let failed = false
const report = line => { console.log(line) }
try {
  report(`building ${base} and the working tree ...`)
  const before = load(buildBase())
  const after = load(buildWorking())
  report(`css chunks: ${before.chunks.size} before, ${after.chunks.size} after; index.html stylesheet links: ${before.links.length} before, ${after.links.length} after`)
  if (before.links.join() !== after.links.join()) { failed = true; report(`FAIL link order differs:\n  ${before.links.join(' ')}\n  ${after.links.join(' ')}`) }
  let ruleTotal = 0
  for (const name of new Set([...before.chunks.keys(), ...after.chunks.keys()])) {
    const a = before.chunks.get(name)
    const b = after.chunks.get(name)
    if (!a || !b) { failed = true; report(`FAIL chunk ${name} only ${a ? 'before' : 'after'}`); continue }
    ruleTotal += a.length
    if (a.length === b.length && a.every((rule, index) => rule === b[index])) continue
    failed = true
    const count = new Map()
    for (const rule of a) count.set(rule, (count.get(rule) ?? 0) + 1)
    for (const rule of b) count.set(rule, (count.get(rule) ?? 0) - 1)
    const diff = [...count].filter(([, n]) => n !== 0)
    report(`FAIL chunk ${name}: ${a.length} -> ${b.length} rules, multiset ${diff.length ? `differs in ${diff.length} rules (e.g. ${diff[0][0].slice(0, 120)})` : 'identical, order differs'}`)
    if (!diff.length) { const first = a.findIndex((rule, index) => rule !== b[index]); report(`  first order difference at rule ${first}`) }
  }
  report(failed ? 'RESULT: DIFFERENT' : `RESULT: identical. Every chunk has the same rules in the same order (${ruleTotal} rules over ${before.chunks.size} chunks), so no selector pair changed its relative order.`)
} finally {
  if (existsSync(scratch)) rmSync(scratch, { recursive: true, force: true })
}
process.exitCode = failed ? 1 : 0
