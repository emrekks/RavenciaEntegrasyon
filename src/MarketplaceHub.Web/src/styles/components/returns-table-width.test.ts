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
