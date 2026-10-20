// Selectors that can never match: they need a class (outside :not()/:where() etc. parentheses) that no source file
// mentions — not in any .ts/.tsx (as a whole word, in strings or code), index.html, nor as the start of a class built
// at runtime (`prefix-${value}`). Used to delete dead stylesheet rules with proof.
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

export function liveClassTest(source, web) {
  const files = source.list().filter(file => !/\.test\.tsx?$/.test(file))
  const text = [...files.map(file => source.read(file)), readFileSync(join(web, 'index.html'), 'utf8')].join('\n')
  // Every word, also as a lower-case and kebab-case class could be made from it (an API value like PENDING_REVIEW).
  const words = new Set()
  for (const word of text.match(/[A-Za-z_][\w-]*/g)) { words.add(word); words.add(word.toLowerCase()); words.add(word.toLowerCase().replaceAll('_', '-')) }
  // Static text right before a ${…} in a template literal: classes built at runtime start with one of these…
  const prefixes = new Set()
  for (const match of text.matchAll(/([A-Za-z_][\w-]*-)\$\{/g)) prefixes.add(match[1])
  // …or with a string concatenation: 'prefix-' + value; and ${…}-suffix ends one.
  for (const match of text.matchAll(/(['"])([A-Za-z_][\w-]*-)\1\s*\+/g)) prefixes.add(match[2])
  const suffixes = new Set()
  for (const match of text.matchAll(/\}(-[\w-]+)/g)) suffixes.add(match[1])
  return name => words.has(name) || [...prefixes].some(prefix => name.startsWith(prefix)) || [...suffixes].some(suffix => name.endsWith(suffix))
}

// Classes a selector needs on some element for any match: top-level ones (outside parentheses).
export function requiredClasses(selector) {
  const outer = selector.replace(/\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)/g, '')
  return [...outer.matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)].map(match => match[1])
}

export function isDeadSelector(selector, isLive) {
  return requiredClasses(selector).some(name => !isLive(name))
}
