// Drops every !important in the given stylesheets that the cascade does not need: with the flag gone the same
// declarations must win against every competitor (cascade.mjs). Flags on properties that inline styles (React
// style={…} or element.style) or @keyframes animations also set are kept — !important beats those, and the
// cascade model does not see them. The tool removes all candidates, puts back each flag whose loss changes a
// winner, and repeats until the change is proved neutral.
//
//   node tests/visual/drop-important.mjs src/styles/design-system.css [more.css …] [--dry]
import { writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { join, resolve } from 'node:path'
import { collect, compareCascades, propertiesOverlap, workingTree } from './cascade.mjs'
import { isDeadSelector, liveClassTest } from './dead-selectors.mjs'

const require = createRequire(import.meta.url)
const postcss = require('postcss')
const web = resolve(import.meta.dirname, '../..')
const files = process.argv.slice(2).filter(argument => !argument.startsWith('--'))
const tree = workingTree(web)
const isLive = liveClassTest(tree, web)

// Properties set outside the stylesheets: inline style objects and element.style writes in the source, and every
// property an @keyframes animates.
// Style objects built in code and passed as style={name} (popover and menu positions, remembered heights) set these.
const outside = new Set(['top', 'right', 'bottom', 'left', 'width', 'height', 'max-height', 'min-width', 'position', 'transform', 'transition', 'display', 'visibility', 'overflow'])
const kebab = name => name.startsWith('--') ? name : name.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)
for (const file of tree.list()) {
  const text = tree.read(file)
  for (const match of text.matchAll(/style=\{\{([^}]*)\}/g)) for (const key of match[1].matchAll(/['"]?([-\w]+)['"]?\s*:/g)) outside.add(kebab(key[1]))
  for (const match of text.matchAll(/\.style\.(?:setProperty\(\s*'([^']+)'|([a-zA-Z]+)\s*=)/g)) outside.add(kebab(match[1] ?? match[2]))
  for (const match of text.matchAll(/(?:CSSProperties|[sS]tyle)[^=\n]*=\s*\{([^{}]*)\}/g)) for (const key of match[1].matchAll(/([a-zA-Z]+)\s*:/g)) outside.add(kebab(key[1]))
}
const allCss = postcss.parse([...new Set([...files, 'src/styles/design-system.css'])].map(file => tree.read(file)).join('\n'))
allCss.walkAtRules(/keyframes$/, atRule => atRule.walkDecls(decl => outside.add(decl.prop)))
const touchedOutside = property => [...outside].some(other => propertiesOverlap(property, other))

const before = collect(tree)
const keep = new Set()
for (let round = 1; round <= 30; round += 1) {
  const texts = {}
  const dropped = []
  for (const file of files) {
    const root = postcss.parse(tree.read(file))
    root.walkDecls(decl => {
      if (!decl.important) return
      const key = `${file}:${decl.source.start.line}:${decl.source.start.column}`
      if (keep.has(key) || touchedOutside(decl.prop.toLowerCase())) return
      if (decl.parent.type === 'rule' && decl.parent.selectors.every(selector => isDeadSelector(selector, isLive))) return
      decl.important = false
      dropped.push({ key, file, selectors: decl.parent.selectors?.map(selector => selector.replace(/\s+/g, ' ').trim()) ?? [], property: decl.prop.toLowerCase(), value: decl.value.replace(/\s+/g, ' ').trim() })
    })
    texts[file] = root.toString()
  }
  const overlay = { read: file => texts[file] ?? tree.read(file), list: () => tree.list() }
  const { problems, added, removed } = compareCascades(before, collect(overlay))
  const real = problems.filter(problem => !isDeadSelector(problem.declaration.selector, isLive) && !isDeadSelector(problem.other.selector, isLive))
  if (!real.length && !added.length && !removed.length) {
    console.log(`round ${round}: ${dropped.length} !important dropped, ${keep.size} kept as needed; cascade unchanged`)
    if (!process.argv.includes('--dry')) for (const [file, text] of Object.entries(texts)) writeFileSync(join(web, file), text)
    process.exit(0)
  }
  const involved = real.flatMap(problem => [problem.declaration, problem.other])
  let kept = 0
  for (const item of dropped) {
    if (involved.some(declaration => declaration.file === item.file && item.selectors.includes(declaration.selector) && declaration.property === item.property && declaration.value === item.value)) { keep.add(item.key); kept += 1 }
  }
  console.log(`round ${round}: ${real.length} changed pairs; keeping ${kept} more`)
  if (!kept) { console.log(real.slice(0, 5)); process.exit(1) }
}
process.exit(1)
