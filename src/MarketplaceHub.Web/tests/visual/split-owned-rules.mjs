// Moves the design-system rules that only one lazily loaded screen module can use into a stylesheet that module
// imports, so the main CSS stops carrying them. A rule moves when every selector in it needs a class that only that
// module mentions. Moving a rule puts it later in the cascade; every rule whose move would change a winner
// (cascade.mjs) stays where it is — the tool repeats until the move is proved neutral.
//
//   node tests/visual/split-owned-rules.mjs <module.tsx> <new stylesheet.css> [--source src/styles/design-system.css] [--dry]
import { readFileSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { join, posix, resolve } from 'node:path'
import { collect, compareCascades, describe, workingTree } from './cascade.mjs'
import { isDeadSelector, liveClassTest, requiredClasses } from './dead-selectors.mjs'

const require = createRequire(import.meta.url)
const postcss = require('postcss')
const web = resolve(import.meta.dirname, '../..')
const [moduleFile, targetFile] = process.argv.slice(2).filter(argument => !argument.startsWith('--'))
const sourceIndex = process.argv.indexOf('--source')
const sourceFile = sourceIndex > 0 ? process.argv[sourceIndex + 1] : 'src/styles/design-system.css'
const tree = workingTree(web)
const isLive = liveClassTest(tree, web)

// Which modules mention each word.
const mentions = new Map()
for (const file of tree.list().filter(file => !/\.test\.tsx?$/.test(file))) {
  for (const word of new Set(tree.read(file).match(/[A-Za-z_][\w-]*/g) ?? [])) {
    const list = mentions.get(word) ?? new Set()
    list.add(file)
    mentions.set(word, list)
  }
}
const ownedBy = selector => requiredClasses(selector).some(name => mentions.get(name)?.size === 1 && mentions.get(name).has(moduleFile))

const importLine = `import './${posix.relative(posix.dirname(moduleFile), targetFile)}'`
const moduleText = tree.read(moduleFile)
// The new sheet is imported first in the module, so the module's own stylesheets still come after it.
const firstImportEnd = moduleText.indexOf('\n', moduleText.search(/^import /m))
const newModuleText = moduleText.includes(importLine) ? moduleText : `${moduleText.slice(0, firstImportEnd + 1)}${importLine}\n${moduleText.slice(firstImportEnd + 1)}`

function split(keep) {
  const root = postcss.parse(tree.read(sourceFile))
  const moved = postcss.root()
  const movedRules = []
  root.walkRules(rule => {
    if (rule.parent?.type === 'atrule' && /keyframes$/.test(rule.parent.name)) return
    if (keep.has(rule.source.start.line)) return
    if (!rule.selectors.every(selector => ownedBy(selector) && !isDeadSelector(selector, isLive))) return
    // Rebuild the rule's at-rule wrappers (@layer, @media) around a copy in the new sheet.
    let copy = rule.clone()
    for (let parent = rule.parent; parent && parent.type !== 'root'; parent = parent.parent) {
      const wrapper = parent.clone()
      wrapper.removeAll()
      wrapper.append(copy)
      copy = wrapper
    }
    moved.append(copy)
    movedRules.push(rule)
  })
  for (const rule of movedRules) {
    const parent = rule.parent
    rule.remove()
    for (let container = parent; container && container.type === 'atrule' && !container.nodes.some(node => node.type !== 'comment'); ) { const next = container.parent; container.remove(); container = next }
  }
  return { source: root.toString(), target: moved.toString(), count: movedRules.length, movedRules }
}

const before = collect(tree)
const keep = new Set()
let result
for (let round = 1; round <= 20; round += 1) {
  result = split(keep)
  const files = { [sourceFile]: result.source, [targetFile]: `/* Rules of the design system that only ${moduleFile.split('/').pop()} uses, loaded with it (moved out of the main CSS). */\n${result.target}`, [moduleFile]: newModuleText }
  const overlay = { read: file => files[file] ?? tree.read(file), list: () => tree.list() }
  const { problems, added, removed } = compareCascades(before, collect(overlay))
  const real = problems.filter(problem => !isDeadSelector(problem.declaration.selector, isLive) && !isDeadSelector(problem.other.selector, isLive))
  const lost = [...added, ...removed]
  if (!real.length && !lost.length) {
    console.log(`round ${round}: ${result.count} rules move, ${keep.size} stay; cascade unchanged`)
    if (!process.argv.includes('--dry')) for (const [file, text] of Object.entries(files)) writeFileSync(join(web, file), text)
    process.exit(0)
  }
  // Keep every moved rule that holds a declaration of a changed pair, then try again.
  const lines = new Set(real.flatMap(problem => [problem.declaration, problem.other]).filter(declaration => declaration.file === targetFile).map(declaration => declaration.selector))
  let kept = 0
  for (const rule of result.movedRules) if (rule.selectors.some(selector => lines.has(selector.replace(/\s+/g, ' ').trim()))) { keep.add(rule.source.start.line); kept += 1 }
  console.log(`round ${round}: ${real.length} changed pairs, ${lost.length} added/removed; keeping ${kept} more rules`)
  if (!kept) { for (const problem of real.slice(0, 10)) console.log(`  ! ${describe(problem.declaration)}\n      vs ${describe(problem.other)} ${problem.before} -> ${problem.after}`); for (const declaration of lost.slice(0, 10)) console.log(`  ? ${describe(declaration)}`); process.exit(1) }
}
process.exit(1)
