import { describe, expect, it } from 'vitest'

// node:fs without @types/node; the url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const read = (path: string) => fs.readFileSync(new URL(path, testFileUrl), 'utf8')

describe('Fatura ve Adres Bilgileri window', () => {
  it('is a large window and its address list is frameless inside the card', () => {
    const tsx = read('./orders-page/OrderInvoiceModals.tsx')
    expect(tsx).toMatch(/title="Fatura ve Adres Bilgileri"[^>]*size="lg"/)
    const css = read('../orders-sade.css')
    expect(css).toMatch(/\.rr-two-cards \.rr-kv \{ border: 0; border-radius: 0; \}/)
    expect(css).toMatch(/fit-content\(104px\) minmax\(0, 1fr\)/)
    expect(css).toMatch(/dd \{ display: block; white-space: normal; overflow-wrap: anywhere; \}/)
  })
})
