import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way variant-tip-css.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./quick-edit-matrix.css', testFileUrl), 'utf8')
const rule = (selector: string) => css.split('\n').find(line => line.includes(`${selector} {`)) ?? ''

// The quick edit window scrolls once, by its body: the colour list must not become a scroll box of its own (a wheel over it
// was swallowed at its end by overscroll-behavior: contain, so the second section could not be reached).
describe('quick edit colour list styles', () => {
  it('gives the list no scroll box, height cap or overscroll rule', () => {
    const list = rule('.rr-cr-list')
    expect(list).not.toBe('')
    expect(list).not.toMatch(/overflow-y\s*:\s*(auto|scroll)/)
    expect(list).not.toMatch(/max-height/)
    expect(list).not.toMatch(/overscroll-behavior/)
  })

  it('has nothing that sticks: the picker row and the header row flow with the list (only the window foot is fixed)', () => {
    expect(css).not.toMatch(/position:\s*sticky/)
    expect(css).not.toContain('rr-cr-sticky')
  })
})

describe('quick edit on a phone', () => {
  const phone = css.slice(css.lastIndexOf('@media (max-width: 760px) {'))
  it('lays the sizes out in equal columns (3, 4 from 414 px) with 44 px boxes in 16 px type', () => {
    expect(phone).toMatch(/\.rr-cr-sizes \{[^}]*grid-template-columns: repeat\(3, minmax\(0, 1fr\)\)/)
    expect(css).toMatch(/min-width: 414px\) \{[^}]*repeat\(4, minmax\(0, 1fr\)\)/)
    expect(phone).toMatch(/\.rr-mx-in \{[^}]*width: 100%; height: 44px; font-size: 16px/)
  })
  it('puts the head on one scrolling line and gives the list no frame of its own', () => {
    expect(phone).toMatch(/\.rr-cr-hrow \{[^}]*display: flex[^}]*overflow-x: auto/)
    expect(phone).toMatch(/\.rr-cr-list \{[^}]*border: 0/)
  })
})
