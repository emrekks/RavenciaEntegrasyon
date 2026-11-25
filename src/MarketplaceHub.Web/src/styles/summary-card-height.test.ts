import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way bottom-nav.test.ts read files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const oneRow = fs.readFileSync(new URL('./one-row.css', testFileUrl), 'utf8')
const tablet = fs.readFileSync(new URL('./tablet-ops.css', testFileUrl), 'utf8')

// Scan 25 Nov 2026 (F-2): the summary cards were a fixed 90 px from 761 px up, so a card with more content spilled out
// and the tablet minimum (114 px) never applied.
describe('summary card height', () => {
  const start = oneRow.indexOf('/* Owner, 9 Nov 2026: summary cards are 90 px')
  const rule = oneRow.slice(start, oneRow.indexOf('\n}\n', start))

  it('is a 90 px minimum on desktops, never a fixed height', () => {
    expect(rule).toContain('@media (min-width: 1025px)')
    expect(rule).toContain('min-height: 90px')
    expect(rule).not.toMatch(/[^-]height: 90px/)
  })

  it('leaves tablets to their own 114 px minimum', () => {
    expect(rule).not.toContain('@media (min-width: 761px)')
    expect(tablet).toMatch(/@media \(min-width: 761px\) and \(max-width: 1024px\) \{\n[^\n]*\.rr-metrics > \*[^\n]*min-height: 114px/)
  })
})
