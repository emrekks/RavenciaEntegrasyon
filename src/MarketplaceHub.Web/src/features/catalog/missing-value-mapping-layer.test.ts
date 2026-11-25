import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way bottom-nav.test.ts read files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./missing-value-mapping-dialog.css', testFileUrl), 'utf8')
const tokens = fs.readFileSync(new URL('../../styles/tokens.css', testFileUrl), 'utf8')

// Scan 25 Nov 2026 (F-4): the "Şimdi eşle" window sat at z-index 1200, above the toasts (1100), which it hid.
describe('missing value mapping window layer', () => {
  it('sits between the windows and the toasts', () => {
    const backdrop = css.slice(css.indexOf('.missing-value-mapping-backdrop {'), css.indexOf('}', css.indexOf('.missing-value-mapping-backdrop {')))
    expect(backdrop).toContain('z-index: calc(var(--rv-z-modal) + 50);')
    const modal = Number(/--rv-z-modal: (\d+);/.exec(tokens)?.[1])
    const toast = Number(/--rv-z-toast: (\d+);/.exec(tokens)?.[1])
    expect(modal + 50).toBeGreaterThan(modal)
    expect(modal + 50).toBeLessThan(toast)
  })
})
