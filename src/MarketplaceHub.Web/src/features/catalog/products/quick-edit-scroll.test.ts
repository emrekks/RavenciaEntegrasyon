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

  it('keeps the value picker and ticks at the top of the window body', () => {
    expect(rule('.rr-cr-sticky')).toMatch(/position:\s*sticky/)
  })
})
