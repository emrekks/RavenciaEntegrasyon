import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way bottom-nav.test.ts read files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./segments.css', testFileUrl), 'utf8')

describe('sideways strips on a phone', () => {
  it('close their vertical overflow but never take the finger from the page (no touch-action)', () => {
    const rule = css.slice(css.indexOf('@media (max-width: 768px) {\n  html body :is(.rr-seg'))
    expect(rule).toContain('overflow-y: hidden')
    expect(rule).toContain('overscroll-behavior-x: contain')
    expect(css).not.toMatch(/touch-action/)
  })
  it('leaves the grids that do not scroll sideways out of the rule', () => {
    expect(css).not.toContain('.rr-kgrid')
  })
})
