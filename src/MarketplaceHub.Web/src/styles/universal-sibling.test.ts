import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way dashboard-sade-css.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./design-system.css', testFileUrl), 'utf8')

describe('design-system.css', () => {
  // ".invoice-workspace-preview-line > * + *" made Chrome restyle ~3 500 elements of Siparişler whenever anything was added to
  // the page: the phone menu opened ~40 % slower (5 333 against 1 879 elements on a 4x slowed CPU).
  it('has no rule ending in a universal sibling', () => {
    expect(css.includes('.invoice-workspace-preview-line > * + *')).toBe(false)
    expect(css).toContain('.invoice-workspace-preview-line > :where(:not(:first-child)) {')
  })
})
