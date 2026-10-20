// A static model of the panel's CSS cascade, used to prove a stylesheet refactor (moving rules between files or
// layers, merging duplicate selectors, dropping !important) cannot change which declaration wins on any element —
// including states and screens the visual capture does not reach (hover, focus, windows, pages without fixtures).
//
// Model: every declaration has a selector, specificity, layer, importance, media context and a load position. Load
// order is known inside one stylesheet group (the main bundle, the phone/tablet sheets, and each lazily loaded chunk's
// files in import order) and between the main groups and every chunk (chunks always load later); it is UNKNOWN between
// two different chunks (that depends on which screen opens first). Two declarations compete when their properties
// overlap (shorthands, logical/physical sides) and their selectors may match one element (the rightmost compounds
// share a class, or one of them has no class and their element types do not rule each other out).
import { readFileSync, globSync } from 'node:fs'
import { createRequire } from 'node:module'
import { join, posix } from 'node:path'

const require = createRequire(import.meta.url)
const postcss = require('postcss')
const csstree = require('css-tree')

// Layer order as declared in src/app.css; a layer first named later is appended (the cascade-layer rule).
export const declaredLayers = ['reset', 'tokens', 'base', 'components', 'layouts', 'features', 'pages', 'responsive', 'utilities']

/** Reads web-relative files from the working tree. */
export const workingTree = web => ({ read: file => readFileSync(join(web, file), 'utf8'), list: () => globSync('src/**/*.{ts,tsx}', { cwd: web }).map(file => file.replaceAll('\\', '/')) })

// Stylesheet groups in load order: the main bundle (src/app.css and its @imports), the tablet and phone sheets that
// main.tsx links after it, then one group per module that imports CSS (files in import order). A stylesheet imported
// by more than one module is its own group: its place depends on which of them loads first.
export function loadGroups(source) {
  const imports = file => [...source.read(file).matchAll(/^import\s+'([^']+\.css)'/gm)].map(match => posix.normalize(posix.join(posix.dirname(file), match[1])))
  const appImports = [...source.read('src/app.css').matchAll(/@import\s+'([^']+)'(?:\s+layer\(([^)]+)\))?/g)].map(match => ({ file: posix.normalize(posix.join('src', match[1])), layer: match[2] ?? null }))
  const groups = [
    { name: 'main', rank: 0, files: appImports },
    { name: 'responsive', rank: 1, media: '(max-width: 1100px)', files: [{ file: 'src/styles/responsive.css' }] },
    { name: 'phone', rank: 2, media: '(max-width: 760px)', files: [{ file: 'src/styles/phone-spacing.css' }] },
  ]
  const modules = source.list().filter(file => !/\.test\.tsx?$/.test(file) && file !== 'src/main.tsx').sort()
  const importers = new Map()
  for (const module of modules) for (const file of imports(module)) importers.set(file, [...(importers.get(file) ?? []), module])
  for (const module of modules) {
    const own = imports(module).filter(file => file !== 'src/app.css' && importers.get(file).length === 1)
    if (own.length) groups.push({ name: module, rank: 3, files: own.map(file => ({ file })) })
  }
  for (const [file, list] of importers) if (list.length > 1 && file !== 'src/app.css') groups.push({ name: file, rank: 3, files: [{ file }] })
  return groups
}

const specificityCache = new Map()
export function specificity(selector) {
  let cached = specificityCache.get(selector)
  if (cached) return cached
  const count = node => {
    const total = [0, 0, 0]
    const add = value => { total[0] += value[0]; total[1] += value[1]; total[2] += value[2] }
    for (const child of node.children ?? []) {
      if (child.type === 'IdSelector') total[0] += 1
      else if (child.type === 'ClassSelector' || child.type === 'AttributeSelector') total[1] += 1
      else if (child.type === 'TypeSelector') { if (child.name !== '*') total[2] += 1 }
      else if (child.type === 'PseudoElementSelector') total[2] += 1
      else if (child.type === 'PseudoClassSelector') {
        const name = child.name.toLowerCase()
        if (name === 'where') continue
        if (['is', 'not', 'has', 'matches'].includes(name) && child.children) {
          let best = [0, 0, 0]
          for (const list of child.children) for (const inner of list.children ?? [list]) { const value = count(inner); if (compareSpecificity(value, best) > 0) best = value }
          add(best)
        } else total[1] += 1
      } else if (child.type === 'Selector') add(count(child))
    }
    return total
  }
  try {
    const ast = csstree.parse(selector, { context: 'selector' })
    cached = count(ast)
  } catch {
    cached = [9, 9, 9]
  }
  specificityCache.set(selector, cached)
  return cached
}
export const compareSpecificity = (a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2]

// Splits at top-level occurrences of the given characters (not inside parentheses or brackets).
function splitTopLevel(text, separators) {
  const parts = []
  let depth = 0
  let current = ''
  for (const character of text) {
    if (character === '(' || character === '[') depth += 1
    else if (character === ')' || character === ']') depth -= 1
    if (depth === 0 && separators.includes(character)) { parts.push(current); current = ''; continue }
    current += character
  }
  parts.push(current)
  return parts.map(part => part.trim()).filter(Boolean)
}

// The rightmost compound of a selector as alternatives (one per branch of an :is()/:where()/:matches() in it), each
// with its classes and element type (null = any element). :not()/:has()/:nth-*() do not narrow it. Pseudo-elements
// keep their own namespace so ::before rules only compete with ::before rules.
export function subject(selector) {
  const last = splitTopLevel(selector, [' ', '>', '+', '~']).at(-1) ?? selector
  const groups = [...last.matchAll(/:(?:is|where|matches)\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)/g)].map(match => splitTopLevel(match[1], [',']).flatMap(argument => subject(argument).alternatives))
  const outer = last.replace(/\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)/g, '')
  const classes = [...outer.matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)].map(match => match[1])
  const type = /^[a-zA-Z][\w-]*/.exec(outer)?.[0]?.toLowerCase() ?? null
  const pseudo = /::?(before|after|placeholder|marker|selection|backdrop|-webkit-[\w-]+|file-selector-button)/.exec(outer)?.[1] ?? ''
  let alternatives = [{ classes, type }]
  for (const group of groups) {
    alternatives = alternatives.flatMap(base => group.map(branch => ({ classes: [...base.classes, ...branch.classes], type: base.type ?? branch.type })))
  }
  return { alternatives, pseudo, classes: [...new Set(alternatives.flatMap(alternative => alternative.classes))], unclassed: alternatives.some(alternative => !alternative.classes.length) }
}

// Element types a class is used on, from the TSX: only for classes whose every mention in the source is a static
// className on a known element (Link/NavLink render <a>); anything else (dynamic classes, classList) stays unknown.
export function elementTypes(source) {
  const files = source.list().filter(file => !/\.test\.tsx?$/.test(file))
  const texts = files.map(file => source.read(file))
  const seen = new Map()
  const mentions = new Map()
  for (const text of texts) {
    for (const match of text.matchAll(/<([A-Za-z][\w.]*)\s((?:[^<>{}]|\{[^{}]*\})*?)className=(?:"([^"]*)"|'([^']*)'|\{'([^']*)'\}|\{"([^"]*)"\})/g)) {
      const tag = /^[a-z]/.test(match[1]) ? match[1] : match[1] === 'Link' || match[1] === 'NavLink' ? 'a' : null
      for (const name of (match[3] ?? match[4] ?? match[5] ?? match[6]).split(/\s+/).filter(Boolean)) {
        const list = seen.get(name) ?? []
        list.push(tag)
        seen.set(name, list)
      }
    }
  }
  const all = texts.join('\n')
  for (const name of seen.keys()) mentions.set(name, all.split(name).length - 1)
  const types = new Map()
  for (const [name, tags] of seen) {
    if (tags.includes(null) || mentions.get(name) !== tags.length) continue
    types.set(name, new Set(tags))
  }
  return types
}

function tagsOf(alternative, types) {
  if (alternative.type) return new Set([alternative.type])
  let tags = null
  for (const name of alternative.classes) {
    const known = types?.get(name)
    if (!known) continue
    tags = tags ? new Set([...tags].filter(tag => known.has(tag))) : new Set(known)
  }
  return tags
}

export function mayMatchSameElement(left, right) {
  if (left.pseudo !== right.pseudo) return false
  return left.alternatives.some(mine => right.alternatives.some(theirs => {
    const myTags = mine.tags ?? null
    const theirTags = theirs.tags ?? null
    if (myTags && theirTags && ![...myTags].some(tag => theirTags.has(tag))) return false
    if (!mine.classes.length || !theirs.classes.length) return true
    return mine.classes.some(name => theirs.classes.includes(name))
  }))
}

const physical = name => name
  .replace(/^-(webkit|moz|ms)-/, '')
  .replace(/-inline-start/, '-left').replace(/-inline-end/, '-right').replace(/-block-start/, '-top').replace(/-block-end/, '-bottom')
  .replace(/^inset-inline-start$/, 'left').replace(/^inset-inline-end$/, 'right').replace(/^inset-block-start$/, 'top').replace(/^inset-block-end$/, 'bottom')
const families = { font: ['line-height'], inset: ['top', 'right', 'bottom', 'left', 'inset-inline', 'inset-block'], 'inset-inline': ['left', 'right'], 'inset-block': ['top', 'bottom'], gap: ['row-gap', 'column-gap'], 'grid-gap': ['row-gap', 'column-gap', 'gap'], 'place-items': ['align-items', 'justify-items'], 'place-content': ['align-content', 'justify-content'], 'place-self': ['align-self', 'justify-self'], 'grid-area': ['grid-row', 'grid-column', 'grid-row-start', 'grid-row-end', 'grid-column-start', 'grid-column-end'], 'padding-inline': ['padding-left', 'padding-right'], 'padding-block': ['padding-top', 'padding-bottom'], 'margin-inline': ['margin-left', 'margin-right'], 'margin-block': ['margin-top', 'margin-bottom'], 'border-inline': ['border-left', 'border-right'], 'border-block': ['border-top', 'border-bottom'], overflow: ['overflow-x', 'overflow-y'], 'text-decoration': ['text-decoration-line', 'text-decoration-color', 'text-decoration-style', 'text-decoration-thickness'] }
export function propertiesOverlap(a, b) {
  if (a === 'all' || b === 'all') return true
  if (a.startsWith('--') || b.startsWith('--')) return a === b
  const x = physical(a)
  const y = physical(b)
  if (x === y || x.startsWith(`${y}-`) || y.startsWith(`${x}-`)) return true
  const expand = name => [name, ...(families[name] ?? [])].flatMap(item => [item, ...(families[item] ?? [])])
  const ex = expand(x)
  const ey = expand(y)
  return ex.some(item => ey.some(other => item === other || item.startsWith(`${other}-`) || other.startsWith(`${item}-`)))
}

// Every declaration with its cascade facts, read through `source` (workingTree(web) or a git revision).
export function collect(source, { groups = loadGroups(source), types = elementTypes(source) } = {}) {
  const subjects = new Map()
  const subjectOf = selector => {
    let found = subjects.get(selector)
    if (!found) {
      found = subject(selector)
      for (const alternative of found.alternatives) alternative.tags = tagsOf(alternative, types)
      subjects.set(selector, found)
    }
    return found
  }
  const declarations = []
  let rules = 0
  for (const group of groups) {
    let position = 0
    let sequence = 0
    for (const { file, layer: importLayer } of group.files) {
      const root = postcss.parse(source.read(file), { from: file })
      root.walkRules(rule => {
        const contexts = []
        const layers = []
        let parent = rule.parent
        let inKeyframes = false
        while (parent && parent.type !== 'root') {
          if (parent.type === 'atrule') {
            if (parent.name === 'layer') layers.unshift(parent.params.trim())
            else if (/keyframes$/.test(parent.name)) inKeyframes = true
            else contexts.unshift(`@${parent.name} ${parent.params.replace(/\s+/g, ' ')}`)
          } else if (parent.type === 'rule') throw new Error(`${file}:${rule.source?.start?.line}: nested CSS rules are not modelled`)
          parent = parent.parent
        }
        if (inKeyframes) return
        if (importLayer) layers.unshift(importLayer)
        const layer = layers.length ? layers.join('.') : null
        rules += 1
        position += 1
        const media = [group.media ? `@media ${group.media}` : '', ...contexts].filter(Boolean).join(' / ')
        for (const selector of rule.selectors) {
          const clean = selector.replace(/\s+/g, ' ').trim()
          const facts = { file, group: group.name, groupRank: group.rank, layer, media, selector: clean, specificity: specificity(clean), subject: subjectOf(clean), line: rule.source?.start?.line }
          let at = 0
          for (const node of rule.nodes ?? []) {
            if (node.type !== 'decl') continue
            at += 1
            // position orders declarations in the cascade (rule, then place inside the rule); sequence is unique.
            sequence += 1
            declarations.push({ ...facts, position: position * 10000 + at, sequence, property: node.prop.toLowerCase(), value: node.value.replace(/\s+/g, ' ').trim(), important: Boolean(node.important) })
          }
        }
      })
    }
  }
  return { declarations, rules }
}

const layerIndex = layer => {
  if (layer === null) return 1000
  const index = declaredLayers.indexOf(layer.split('.')[0])
  return index >= 0 ? index : declaredLayers.length + 1
}

// 1 when `a` beats `b`, -1 when `b` beats `a`, 0 when the order between them is unknown (two different chunks).
export function wins(a, b) {
  if (a.important !== b.important) return a.important ? 1 : -1
  const layerA = layerIndex(a.layer)
  const layerB = layerIndex(b.layer)
  if (layerA !== layerB) return (a.important ? layerA < layerB : layerA > layerB) ? 1 : -1
  const bySpecificity = compareSpecificity(a.specificity, b.specificity)
  if (bySpecificity) return bySpecificity > 0 ? 1 : -1
  if (a.group === b.group) return a.position === b.position ? 0 : a.position > b.position ? 1 : -1
  if (a.groupRank !== b.groupRank) return a.groupRank > b.groupRank ? 1 : -1
  return 0
}

// Properties that can override each other share a family (shorthand and longhands, logical and physical sides).
const familyRoots = { 'line-height': 'font', top: 'inset', right: 'inset', bottom: 'inset', left: 'inset', 'row-gap': 'gap', 'column-gap': 'gap', 'grid-gap': 'gap', align: 'place', justify: 'place' }
const familyOf = property => {
  if (property.startsWith('--')) return property
  const name = physical(property)
  return familyRoots[name] ?? familyRoots[name.split('-')[0]] ?? name.split('-')[0]
}
const competes = (a, b) => propertiesOverlap(a.property, b.property) && mayMatchSameElement(a.subject, b.subject)

// Who a declaration is, independent of where it lives: moving, merging or un-!important-ing keeps the identity.
const identity = declaration => `${declaration.media}|${declaration.selector}|${declaration.property}|${declaration.value}`

function indexByFamily(declarations) {
  const byIdentity = new Map()
  const byKey = new Map()
  const byFamily = new Map()
  const push = (map, key, value) => { const list = map.get(key); if (list) list.push(value); else map.set(key, [value]) }
  for (const declaration of declarations) {
    // The same selector, media, property and value may occur more than once (a selector repeated in two files):
    // one identity, several occurrences.
    declaration.identity = identity(declaration)
    push(byIdentity, declaration.identity, declaration)
    const family = familyOf(declaration.property)
    push(byFamily, family, declaration)
    if (declaration.subject.unclassed) push(byKey, `${family}|`, declaration)
    for (const name of declaration.subject.classes) push(byKey, `${family}|.${name}`, declaration)
  }
  return { byIdentity, byKey, byFamily }
}

function competitors(indexed, declaration) {
  const family = familyOf(declaration.property)
  if (declaration.subject.unclassed) return indexed.byFamily.get(family) ?? []
  return [...(indexed.byKey.get(`${family}|`) ?? []), ...declaration.subject.classes.flatMap(name => indexed.byKey.get(`${family}|.${name}`) ?? [])]
}

// Identities whose cascade facts may differ: added, removed, a different number of occurrences, an occurrence moved
// to another group/layer/importance, or moved inside its group (found as the occurrences outside the longest run
// that kept its order).
function changedIdentities(before, after) {
  const changed = new Set()
  const factKey = declaration => `${declaration.group}|${declaration.layer}|${declaration.important}|${declaration.specificity}`
  const facts = list => list.map(factKey).sort().join('\n')
  const stableByGroup = new Map()
  for (const [key, list] of after.byIdentity) {
    const old = before.byIdentity.get(key)
    if (!old || old.length !== list.length || facts(old) !== facts(list)) { changed.add(key); continue }
    list.forEach((declaration, at) => {
      const group = stableByGroup.get(declaration.group) ?? []
      group.push([old[at].sequence, declaration.sequence, key])
      stableByGroup.set(declaration.group, group)
    })
  }
  for (const key of before.byIdentity.keys()) if (!after.byIdentity.has(key)) changed.add(key)
  for (const entries of stableByGroup.values()) {
    // Longest increasing subsequence of new positions taken in old order: those kept their relative order.
    entries.sort((a, b) => a[0] - b[0])
    const tails = []
    const tailIndex = []
    const previous = new Array(entries.length).fill(-1)
    entries.forEach(([, position], at) => {
      let low = 0
      let high = tails.length
      while (low < high) { const mid = (low + high) >> 1; if (tails[mid] < position) low = mid + 1; else high = mid }
      tails[low] = position
      tailIndex[low] = at
      previous[at] = low ? tailIndex[low - 1] : -1
    })
    const kept = new Set()
    for (let at = tailIndex[tails.length - 1] ?? -1; at >= 0; at = previous[at]) kept.add(at)
    entries.forEach((entry, at) => { if (!kept.has(at)) changed.add(entry[2]) })
  }
  return changed
}

// Which of two identities sets the value where both apply: 'own' when one occurrence of it beats every occurrence of
// the other, 'other' the other way round, '?' when that depends on chunk load order, '' when they do not compete.
function outcome(indexed, ownKey, otherKey) {
  const own = indexed.byIdentity.get(ownKey) ?? []
  const other = indexed.byIdentity.get(otherKey) ?? []
  if (!own.length || !other.length) return ''
  if (own[0].value === other[0].value && own[0].property === other[0].property) return ''
  if (!competes(own[0], other[0])) return ''
  if (own.some(mine => other.every(theirs => wins(mine, theirs) === 1))) return 'own'
  if (other.some(theirs => own.every(mine => wins(theirs, mine) === 1))) return 'other'
  return '?'
}

/**
 * Compares the cascade before and after an edit: for every identity the edit touched, each competing declaration
 * must keep the same winner. An order that was unknown before (two chunks) and stays unknown is accepted — that
 * ambiguity exists today and is not made by the edit. Returns one problem per pair whose winner changed, plus the
 * declarations that appeared or disappeared altogether.
 */
export function compareCascades(beforeCollection, afterCollection) {
  const before = indexByFamily(beforeCollection.declarations)
  const after = indexByFamily(afterCollection.declarations)
  const changed = changedIdentities(before, after)
  const problems = []
  const seen = new Set()
  // A declaration that appears changes styles by definition: listed for review, not proved.
  const added = [...after.byIdentity.keys()].filter(key => !before.byIdentity.has(key)).map(key => after.byIdentity.get(key)[0])
  // One that disappears is harmless when, on the same selector and media, a declaration of the same property that
  // still exists always beat it (merging a duplicate selector drops exactly those).
  const removed = [...before.byIdentity.keys()].filter(key => !after.byIdentity.has(key)).flatMap(key => before.byIdentity.get(key)).map(declaration => {
    const covers = other => other.property === declaration.property || (!declaration.property.startsWith('--') && declaration.property.startsWith(`${other.property}-`))
    const dominated = beforeCollection.declarations.some(other => other !== declaration && other.selector === declaration.selector && other.media === declaration.media && covers(other) && after.byIdentity.has(other.identity) && wins(other, declaration) === 1)
    // …or when an identical declaration (same selector, property, value, importance) that stays wins and loses
    // against exactly the same competitors (a repeated rule in a sheet that only narrows the media).
    const twins = dominated ? [] : after.byIdentity.size && beforeCollection.declarations.filter(other => other !== declaration && other.selector === declaration.selector && other.property === declaration.property && other.value === declaration.value && other.important === declaration.important && after.byIdentity.has(other.identity))
    const redundant = !dominated && twins.some(twin => competitors(before, declaration).every(other => other.value === declaration.value || !competes(other, declaration) || other === twin || wins(other, twin) === wins(other, declaration)))
    return { ...declaration, dominated: dominated || redundant }
  })
  for (const key of changed) {
    if (!before.byIdentity.has(key) || !after.byIdentity.has(key)) continue
    const others = new Set()
    for (const indexed of [before, after]) for (const own of indexed.byIdentity.get(key) ?? []) for (const other of competitors(indexed, own)) others.add(other.identity)
    for (const otherKey of others) {
      if (otherKey === key) continue
      const pairKey = key < otherKey ? `${key}\u0000${otherKey}` : `${otherKey}\u0000${key}`
      if (seen.has(pairKey)) continue
      seen.add(pairKey)
      const was = outcome(before, key, otherKey)
      const now = outcome(after, key, otherKey)
      // A competitor that is gone or new is reported with the removed/added declarations.
      if (was === now || !before.byIdentity.has(otherKey) || !after.byIdentity.has(otherKey)) continue
      if (was === '?' && now === '?') continue
      problems.push({ declaration: after.byIdentity.get(key)[0], other: after.byIdentity.get(otherKey)[0], before: was || 'no competition', after: now || 'no competition' })
    }
  }
  return { changed: changed.size, problems, added, removed }
}

export const describe = declaration => `${declaration.file}:${declaration.line} ${declaration.media ? `[${declaration.media}] ` : ''}${declaration.selector} { ${declaration.property}: ${declaration.value}${declaration.important ? ' !important' : ''} }${declaration.layer ? ` @layer ${declaration.layer}` : ''}`

