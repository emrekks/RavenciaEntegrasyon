import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way bottom-nav.test.ts read files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./category-workbench.css', testFileUrl), 'utf8')

// Scan 25 Nov 2026 (F-1): on a phone the Panel Kategorisi picker stayed 168 px wide in a one-column row.
describe('category workbench on a phone', () => {
  it('gives the Panel Kategorisi picker the full width after the 168 px desktop rule', () => {
    const desktop = css.indexOf('.cm-card .cm-context > label.cm-field { width: var(--cm-lead-w); }')
    const phone = css.indexOf('@media (max-width: 760px) { .cm-card .cm-context > label.cm-field { width: 100%; } }')
    expect(desktop).toBeGreaterThan(-1)
    expect(phone).toBeGreaterThan(desktop)
  })
})
