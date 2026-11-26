import { describe, expect, it } from 'vitest'

// A :has() anchored on html, body or the shell makes Chrome re-check it on every change anywhere below and restyle thousands of elements
// (Siparişler, 50 rows, one row selection on a phone: 25 800 elements, 2.3 s at 4x CPU). The dock's mode and warning row, the keyboard and an
// open gap list are marks on <html> instead (shared/root-mark.ts; the layers' html[data-rv-layer] is useScrollLock.ts); this keeps such a
// :has() from coming back.
// node:fs without @types/node, the way bottom-nav.test.tsx reads files (a ?raw import of a stylesheet is empty under vitest).
type Entry = { name: string; isDirectory(): boolean }
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string; readdirSync(path: URL, options: { withFileTypes: true }): Entry[] }
// The url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const testFileUrl = import.meta.url
const sourceRoot = new URL('../', testFileUrl)
const walk = (directory: URL): string[] => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? walk(new URL(entry.name + '/', directory)) : entry.name.endsWith('.css') ? [new URL(entry.name, directory).href] : [])
const sheets: Record<string, string> = Object.fromEntries(walk(sourceRoot).map(href => [href, fs.readFileSync(new URL(href), 'utf8')]))

/** Every rule's selector, comments left out, with the stylesheet it is in. */
function selectors() {
  const out: Array<{ file: string; selector: string }> = []
  for (const [file, text] of Object.entries(sheets)) {
    const code = text.replace(/\/\*[\s\S]*?\*\//g, '')
    for (const match of code.matchAll(/([^{}@;]+)\{/g)) {
      const selector = match[1].trim()
      if (selector && !/^(from|to|\d+%)/.test(selector)) out.push({ file, selector })
    }
  }
  return out
}

// A :has() whose own compound is :root, html, body or the shell.
const highAnchor = /(?:^|[\s>+~(,])(?::root|html|body|\.app-shell(?:\.[\w-]+)*)(?:\[[^\]]*\])*:has\(/

describe('no :has() on html, body or the shell', () => {
  it('is anchored there only in the print sheet and for the desktop shell mount (neither changes while a page is used)', () => {
    const allowed = [/^html:has\(body > \.shipping-label-backdrop\)/, /^body:has\(> \.shipping-label-backdrop\)/, /^html:has\(> body \.app-shell\.stitch-shell\)/]
    const found = selectors().filter(({ selector }) => selector.split(/,(?![^(]*\))/).some(part => highAnchor.test(part.trim()) && !allowed.some(rule => rule.test(part.trim()))))
    expect(found).toEqual([])
  })

  it('reads the dock mode, its warning row, the keyboard and an open gap list from <html> marks, not from a :has()', () => {
    const offenders = selectors().filter(({ selector }) => /:has\([^{]*(?:\.rv-dock-layer|\.publish-issue-tooltip\.is-open)/.test(selector) || /:has\([^{]*:focus\)[^{]*\.rv-dock-layer/.test(selector))
    expect(offenders).toEqual([])
    const all = selectors().map(item => item.selector).join('\n')
    expect(all).toContain('html[data-rv-dock-mode="task"] .app-shell.stitch-shell > main')
    expect(all).toContain('html[data-rv-dock-rows="2"] .app-shell.stitch-shell > main')
    expect(all).toContain('html[data-rv-dock-rows="2"] body :is(.app-toast-stack, .app-toast:not(.app-toast-stack > *))')
    expect(all).toContain('html[data-rv-typing] body .rv-dock-layer')
    expect(all).toContain('html[data-rv-issue-open] body .publish-issue-tooltip:not(.is-open)')
    expect(all).toContain('html[data-rv-issue-open] body .publish-platform-fact-publication .publish-platform-status[data-tooltip]::after')
  })

  it('sets the room under the page on every element that reads it (the value is not inherited: @property inherits: false)', () => {
    const nav = Object.entries(sheets).find(([file]) => file.endsWith('/app/bottom-nav.css'))![1]
    expect(nav).toContain('@property --rv-bottom-nav-space { syntax: "*"; inherits: false; }')
    // The elements bottom-nav.css sets it on.
    const set = /(?:^|[\s>])(?:main|input|textarea|select|button|\[tabindex\]|[\w.-]*\.(?:product-submit-sticky|qi-page|ss-page|rv-draftbar)[\w.-]*)$/
    const readers = Object.entries(sheets).flatMap(([file, text]) => [...text.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/([^{}]+)\{([^{}]*)\}/g)]
      .filter(match => match[2].includes('var(--rv-bottom-nav-space'))
      .map(match => ({ file, selector: match[1].trim() })))
    expect(readers.length).toBeGreaterThan(4)
    for (const { file, selector } of readers) {
      for (const part of selector.split(/,(?![^(]*\))/)) {
        // The element the rule styles; a field list (:is(input, …)) stands for its fields.
        const subject = part.trim().replace(/:is\(([^()]*)\)$/, (_, list: string) => list.split(',')[0].trim())
        expect(`${file}: ${subject}`).toMatch(set)
      }
    }
  })
})
