import { describe, expect, it } from 'vitest'

// node:fs without @types/node; the url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./notification-panel.css', testFileUrl), 'utf8')
const rule = (selector: string) => css.split('}').map(part => part.trim()).filter(part => part.startsWith(selector + ' {')).map(part => part.slice(part.indexOf('{') + 1)).join(' ')

describe('read notification records recede', () => {
  it('a read title is lighter and quieter, its text and meta are softer, with colour tokens', () => {
    const title = rule('.rv-notification-history-item.is-read .rv-notification-title')
    expect(title).toMatch(/font-weight: 500/)
    expect(title).toMatch(/color: var\(--rv-color-muted\)/)
    expect(rule('.rv-notification-history-item.is-read .rv-notification-text')).toMatch(/color: var\(--rv-color-subtle\)/)
    expect(rule('.rv-notification-history-item.is-read .rv-notification-meta')).toMatch(/color: var\(--rv-color-subtle\)/)
  })
  it('a read error keeps a faint edge and its action link is not red', () => {
    expect(rule('.rv-notification-history-item.is-read.is-error')).toMatch(/color-mix\(in srgb, var\(--rv-color-danger\) 35%/)
    expect(rule('.rv-notification-history-item.is-read .rv-notification-go')).toMatch(/color: var\(--rv-color-primary-text\)/)
  })
  it('never fades the whole row with opacity (only the logo tile)', () => {
    const rowRules = css.split('}').filter(part => /\.is-read[^{]*\{/.test(part) && !/rv-notification-icon/.test(part.split('{')[0]))
    for (const part of rowRules) expect(part).not.toMatch(/opacity/)
  })
})
