import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way quick-edit-scroll.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./bottom-nav.css', testFileUrl), 'utf8')
const app = fs.readFileSync(new URL('./App.tsx', testFileUrl), 'utf8')

describe('phone bottom navigation', () => {
  it('is drawn only up to 768 px, where the hamburger goes away and the page keeps room under its content', () => {
    expect(css).toMatch(/^\.rv-bottom-nav \{ display: none; \}/m)
    const phone = css.slice(css.indexOf('@media (max-width: 768px)'))
    expect(phone).toContain('.rv-mobile-menu-toggle { display: none; }')
    expect(phone).toContain('> main { padding-bottom: var(--rv-bottom-nav-space); }')
    expect(phone).toMatch(/min-height: 48px/)
  })
  it('offers Ana Sayfa, Siparişler, Ürünler, Faturalar and the full menu', () => {
    const nav = app.slice(app.indexOf('className="rv-bottom-nav"'), app.indexOf('</nav>', app.indexOf('className="rv-bottom-nav"')))
    for (const label of ['Ana Sayfa', 'Siparişler', 'Ürünler', 'Faturalar', 'Menü']) expect(nav).toContain(label)
    expect(nav).toContain('setMobileMenuOpen(true)')
  })
})
