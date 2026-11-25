import { describe, expect, it } from 'vitest'

// node:fs without @types/node; the url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./rr-components.css', testFileUrl), 'utf8')

// The İadeler table must fit a 1920 px window with the sidebar pinned (1638 px of table card) without a sideways scroll.
describe('İadeler desktop table width', () => {
  it('the last (winning) fit-pass min-widths of the returns table are at most 1638 px', () => {
    const block = css.slice(css.lastIndexOf('/* Fit pass (owner, 14 Nov)'))
    const widths = [...block.matchAll(/rr-returns \.rr-tbl\.rr-tbl[^{]*\{ min-width: (\d+)px; \}/g)].map(match => Number(match[1]))
    expect(widths.length).toBeGreaterThanOrEqual(4)
    for (const width of widths) expect(width).toBeLessThanOrEqual(1638)
  })
})

// The order cell's longest fixed line ("Sipariş Tarihi: 19.11.2026 21:27", 187 px) must fit inside the 18 px cell padding, next to the 26 px
// selection checkbox when it is present; the product column gives the width back so the table total stays the same.
describe('İadeler order column width', () => {
  const block = css.slice(css.lastIndexOf('25 Nov: the order column'))
  const width = (selector: string) => Number(new RegExp(String.raw`rr-returns ${selector.replaceAll('.', String.raw`\.`)} \{ width: (\d+)px; \}`).exec(block)?.[1])
  it('holds the date line with and without the selection checkbox', () => {
    expect(width('.rr-tbl.rr-tbl col.rr-c-order')).toBeGreaterThanOrEqual(187 + 36)
    expect(width('.rr-tbl.rr-tbl.rr-has-select col.rr-c-order')).toBeGreaterThanOrEqual(187 + 26 + 36)
  })
  it('keeps the table total: order + product stay at 530 px (plain) and 544 px (selection)', () => {
    expect(width('.rr-tbl.rr-tbl col.rr-c-order') + width('.rr-tbl.rr-tbl col.rr-c-prod')).toBe(530)
    expect(width('.rr-tbl.rr-tbl.rr-has-select col.rr-c-order') + width('.rr-tbl.rr-tbl.rr-has-select col.rr-c-prod')).toBe(544)
  })
})
