// Deletes the selectors that can never match (dead-selectors.mjs) from the given stylesheets: a dead selector leaves
// its list, a rule left with none goes with the comment that only described it, and an emptied @media/@layer goes too.
//
//   node tests/visual/remove-dead-rules.mjs src/styles/design-system.css [more.css …]
import { readFileSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { join, resolve } from 'node:path'
import { workingTree } from './cascade.mjs'
import { isDeadSelector, liveClassTest } from './dead-selectors.mjs'

const require = createRequire(import.meta.url)
const postcss = require('postcss')
const web = resolve(import.meta.dirname, '../..')
const isLive = liveClassTest(workingTree(web), web)

for (const file of process.argv.slice(2)) {
  const path = join(web, file)
  const before = readFileSync(path, 'utf8')
  const root = postcss.parse(before)
  const doomed = new Set()
  let selectors = 0
  root.walkRules(rule => {
    if (rule.parent?.type === 'atrule' && /keyframes$/.test(rule.parent.name)) return
    const live = rule.selectors.filter(selector => !isDeadSelector(selector, isLive))
    if (!live.length) doomed.add(rule)
    else if (live.length < rule.selectors.length) { selectors += rule.selectors.length - live.length; rule.selectors = live }
  })
  // An at-rule whose rules are all doomed is doomed too (inside out).
  const doomedContainer = node => node.type === 'atrule' && node.nodes?.some(child => child.type !== 'comment') && node.nodes.every(child => child.type === 'comment' || doomed.has(child) || doomedContainer(child))
  root.walkAtRules(atRule => { if (doomedContainer(atRule)) doomed.add(atRule) })
  // A comment goes when everything it introduces (up to the next comment) goes.
  root.walkComments(comment => {
    const introduced = []
    for (let node = comment.next(); node && node.type !== 'comment'; node = node.next()) introduced.push(node)
    if (introduced.length && introduced.every(node => doomed.has(node))) doomed.add(comment)
  })
  for (const node of doomed) node.remove()
  const after = root.toString().replace(/\n{3,}/g, '\n\n')
  writeFileSync(path, after)
  const rules = [...doomed].filter(node => node.type === 'rule').length
  console.log(`${file}: ${rules} dead rules and ${selectors} dead selectors in live lists removed; ${(before.length / 1024).toFixed(1)} -> ${(after.length / 1024).toFixed(1)} KiB`)
}
