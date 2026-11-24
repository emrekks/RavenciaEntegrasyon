import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way bottom-nav.test.tsx reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const read = (path: string) => fs.readFileSync(new URL(path, import.meta.url), 'utf8')

// A page that draws .rv-view-tabs must load view-tabs.css itself: the sheet is a lazy chunk, so a page opened by address (a reload, a
// deep link) otherwise showed the tabs as filled boxes, with no underline and no gap after the number, until another page had loaded it.
describe('the view tabs sheet', () => {
  it('is imported by the product editor, whose wizard step tabs are view tabs', () => {
    expect(read('../features/catalog/product-editor/ProductEditorPages.tsx')).toMatch(/import '\.\.\/\.\.\/\.\.\/shared\/view-tabs\.css'/)
  })
})
