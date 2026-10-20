// Compares two captures (capture.mjs output folders) element by element: added/removed elements, box changes, and
// every computed-style property that changed (also on ::before/::after), grouped so one CSS change reads as one line.
// Exit code 1 when anything differs.
//
//   node tests/visual/diff.mjs <baseline-folder> <current-folder> [--details 12]
import { readFile, readdir } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { chromiumExecutable } from './browser.mjs'
import { closePixelDiff, pixelDiff } from './pixel-diff.mjs'

const [baselineArg, currentArg] = process.argv.slice(2).filter(value => !value.startsWith('--'))
const detailsIndex = process.argv.indexOf('--details')
const detailLimit = detailsIndex > 0 ? Number(process.argv[detailsIndex + 1]) : 12
if (!baselineArg || !currentArg) {
  console.error('usage: node tests/visual/diff.mjs <baseline-folder> <current-folder> [--details 12]')
  process.exit(2)
}
const baseline = resolve(baselineArg)
const current = resolve(currentArg)
const executablePath = await chromiumExecutable()

const styleCache = new WeakMap()
const styleOf = (capture, id) => {
  if (id < 0) return null
  let cache = styleCache.get(capture)
  if (!cache) { cache = new Map(); styleCache.set(capture, cache) }
  let style = cache.get(id)
  if (!style) {
    const vector = capture.vectors[id].split(',').map(Number)
    const values = Object.fromEntries(capture.properties.map((name, index) => [name, capture.values[vector[index]]]))
    style = { values, key: JSON.stringify(values) }
    cache.set(id, style)
  }
  return style
}

let totalChanged = 0
const captures = folder => readdir(folder).then(files => files.filter(name => name.endsWith('.json') && !name.startsWith('_')).sort())
const names = await captures(current)
const skipped = (await captures(baseline)).filter(name => !names.includes(name))
if (skipped.length) console.log(`not captured this time (left out of the comparison): ${skipped.map(name => name.replace(/\.json$/, '')).join(', ')}`)
for (const file of names) {
  const name = file.replace(/\.json$/, '')
  if (!existsSync(join(baseline, file))) { console.log(`${name}: not in the baseline`); totalChanged += 1; continue }
  const before = JSON.parse(await readFile(join(baseline, file), 'utf8'))
  const after = JSON.parse(await readFile(join(current, file), 'utf8'))
  const beforeElements = new Map(before.elements.map(element => [element[0], element]))
  const afterElements = new Map(after.elements.map(element => [element[0], element]))
  const removed = [...beforeElements.keys()].filter(path => !afterElements.has(path))
  const added = [...afterElements.keys()].filter(path => !beforeElements.has(path))
  const groups = new Map()
  const note = (key, path) => {
    const list = groups.get(key) ?? []
    list.push(path)
    groups.set(key, list)
  }
  let changedElements = 0
  for (const [path, was] of beforeElements) {
    const now = afterElements.get(path)
    if (!now) continue
    let changed = false
    if (was[1] !== now[1]) { note(`class "${was[1]}" -> "${now[1]}"`, path); changed = true }
    if (was[3] !== now[3]) { note(`box ${was[3]} -> ${now[3]}`, path); changed = true }
    for (const [slot, label] of [[2, ''], [4, '::before '], [5, '::after ']]) {
      const wasStyle = styleOf(before, was[slot])
      const nowStyle = styleOf(after, now[slot])
      if (!wasStyle || !nowStyle) {
        if (wasStyle !== nowStyle) { note(`${label}pseudo-element ${wasStyle ? 'removed' : 'added'}`, path); changed = true }
        continue
      }
      if (wasStyle.key === nowStyle.key) continue
      for (const property of new Set([...Object.keys(wasStyle.values), ...Object.keys(nowStyle.values)])) {
        if (wasStyle.values[property] !== nowStyle.values[property]) { note(`${label}${property}: ${wasStyle.values[property]} -> ${nowStyle.values[property]}`, path); changed = true }
      }
    }
    if (changed) changedElements += 1
  }
  const beforePng = join(baseline, `${name}.png`)
  const afterPng = join(current, `${name}.png`)
  let screenshot = 'missing'
  if (existsSync(beforePng) && existsSync(afterPng)) {
    if (Buffer.compare(await readFile(beforePng), await readFile(afterPng)) === 0) screenshot = 'identical'
    else {
      const pixels = await pixelDiff(beforePng, afterPng, executablePath)
      screenshot = pixels.sizeChanged ? `size ${pixels.sizeChanged}` : pixels.count ? `${pixels.count} pixels differ in box ${pixels.box}` : 'identical pixels'
    }
  }
  const screenshotSame = screenshot.startsWith('identical')
  const scrollSame = String(before.scroll) === String(after.scroll)
  if (!removed.length && !added.length && !changedElements && scrollSame) {
    console.log(`${name}: identical (${before.elements.length} elements, screenshot ${screenshot})`)
    if (!screenshotSame) totalChanged += 1
    continue
  }
  totalChanged += 1
  console.log(`${name}: ${changedElements} changed, ${added.length} added, ${removed.length} removed elements; screenshot ${screenshot}${scrollSame ? '' : `; page size ${before.scroll} -> ${after.scroll}`}`)
  for (const path of removed.slice(0, 5)) console.log(`  - ${path}`)
  for (const path of added.slice(0, 5)) console.log(`  + ${path}`)
  const sorted = [...groups].sort((a, b) => b[1].length - a[1].length)
  for (const [key, paths] of sorted.slice(0, detailLimit)) console.log(`  ${paths.length}x ${key}\n      e.g. ${paths[0]}`)
  if (sorted.length > detailLimit) console.log(`  ... ${sorted.length - detailLimit} more kinds of change`)
}
await closePixelDiff()
console.log(totalChanged ? `\n${totalChanged} of ${names.length} captures differ.` : `\nAll ${names.length} captures identical.`)
process.exitCode = totalChanged ? 1 : 0
