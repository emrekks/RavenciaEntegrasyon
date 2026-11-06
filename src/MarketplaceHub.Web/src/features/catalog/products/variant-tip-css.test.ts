import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way ShopifyInvoiceStatusDialog.test.tsx reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const read = (path: string) => fs.readFileSync(new URL(path, import.meta.url), 'utf8')

describe('variant tip on the product list', () => {
  it('is not scaled by the press motion, which would move its fixed tooltip while held', () => {
    const rule = read('../../../styles/motion.css').split('\n').find(line => line.includes('[role="button"]') && line.includes(':active'))
    expect(rule).toBeDefined()
    expect(rule).toContain('.rr-vhover')
  })

  it('opens on hover only over the "N seçenek" text, not the whole cell', () => {
    const css = read('../products-sade.css')
    expect(css).toContain('.rr-vhover:has(> .rr-varcell:hover) > .rr-vtip')
    expect(css).not.toMatch(/\.rr-vhover:hover\s*>\s*\.rr-vtip/)
  })
})
