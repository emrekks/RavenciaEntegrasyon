import { describe, expect, it } from 'vitest'

// node:fs without @types/node; the url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./quick-edit-matrix.css', testFileUrl), 'utf8')

describe('Hızlı Stok / Hızlı Fiyat apply row', () => {
  const block = css.slice(css.indexOf('@media (min-width: 761px) {', css.indexOf('.rr-qe-apply {')))
  const rules = block.slice(0, block.indexOf('\n}\n'))
  it('from 761 px the row does not wrap: Uygula sits right after the fields, the shrinking scope note goes last', () => {
    expect(rules).toMatch(/\.rr-qe-applyrow \{ flex-wrap: nowrap; \}/)
    expect(rules).toMatch(/\.rr-qe-apply \{ order: 1; \}/)
    expect(rules).toMatch(/\.rr-qe-scope \{ order: 2; flex: 1 1 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; \}/)
  })
  it('the stock selector can give up to 170 px before the note is cut', () => {
    expect(rules).toMatch(/\.rr-qe-f200 \{ flex: 0 1 200px; min-width: 170px; \}/)
  })
})
