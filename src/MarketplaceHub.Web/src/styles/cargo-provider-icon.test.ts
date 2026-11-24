import { describe, expect, it } from 'vitest'

// node:fs without @types/node; the url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const read = (path: string) => fs.readFileSync(new URL(path, testFileUrl), 'utf8')
const files = ['./design-system.css', '../features/marketplace/orders-responsive.css', '../features/invoicing/invoices-responsive.css', '../features/invoicing/invoices-sade.css', '../features/invoicing/invoice-detail-ui.css']

describe('the carrier mark (img.cargo-provider-icon) can never be drawn at its natural size', () => {
  it('has a base size limit on the class itself', () => {
    expect(read('./design-system.css')).toMatch(/\.cargo-provider-icon \{[^}]*max-width: \d+px;[^}]*max-height: \d+px/)
  })
  it('is never released from its height limit (max-height: none) and height: auto only goes with a max-height', () => {
    for (const file of files) {
      for (const rule of read(file).split('}').filter(text => /\.cargo-provider-icon/.test(text) && !/cargo-provider-icon-fallback/.test(text.split('{')[0]))) {
        const body = rule.slice(rule.indexOf('{'))
        expect(body, `${file}: ${rule.trim().slice(0, 80)}`).not.toMatch(/max-height:\s*none/)
        if (!/\.cargo-provider-display/.test(rule.split('{')[0]) && /[^-]height:\s*auto/.test(body)) expect(body, `${file}: ${rule.trim().slice(0, 80)}`).toMatch(/max-height:\s*\d+px/)
      }
    }
  })
})
