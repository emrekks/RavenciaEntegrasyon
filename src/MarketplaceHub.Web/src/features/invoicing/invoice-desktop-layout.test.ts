import { describe, expect, it } from 'vitest'

// node:fs without @types/node; the url goes through a plain variable (Vite rewrites new URL(.., import.meta.url)).
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const read = (path: string) => fs.readFileSync(new URL(path, testFileUrl), 'utf8')

const bodies = (source: string) => {
  // A rule is "selector { body }" with the at-rules (media) around it.
  const result: { media: string[]; selector: string; body: string }[] = []
  const media: string[] = []
  const text = source.replace(/\/\*[\s\S]*?\*\//g, '')
  let index = 0
  while (index < text.length) {
    const open = text.indexOf('{', index)
    const close = text.indexOf('}', index)
    if (open === -1 && close === -1) break
    if (close !== -1 && (open === -1 || close < open)) { media.pop(); index = close + 1; continue }
    const head = text.slice(index, open).trim()
    if (head.startsWith('@')) { media.push(head); index = open + 1; continue }
    const end = text.indexOf('}', open)
    result.push({ media: [...media], selector: head, body: text.slice(open + 1, end).trim() })
    index = end + 1
  }
  return result
}

describe('Faturalar desktop table keeps its 23 Nov cells', () => {
  const sources = ['./invoices-responsive.css', '../marketplace/orders-responsive.css'].map(path => [path, bodies(read(path))] as const)

  it('the package number line and the card-only carrier / tracking rules apply to phones and tablets only', () => {
    for (const [path, list] of sources) {
      for (const rule of list.filter(item => /invoice-package-no|cargo-provider-(icon|display)/.test(item.selector) && !/invoice-detail/.test(item.selector))) {
        const hidesOnly = /^display:\s*none;?$/.test(rule.body)
        if (hidesOnly) continue
        expect(rule.media.join(' '), `${path}: ${rule.selector.slice(0, 90)}`).toMatch(/max-width:\s*(760|1024)px/)
      }
    }
  })

  it('the package number line is hidden outside the phone card', () => {
    const base = bodies(read('./invoices-responsive.css')).find(rule => rule.selector === '.invoice-reference-order small.invoice-package-no')
    expect(base?.media).toEqual([])
    expect(base?.body).toMatch(/display:\s*none/)
  })

  it('the desktop column grid (from 1025 px) is fixed: fluid order column, then 136 / 140 / 160 / 180 / 112 / 124 px', () => {
    const rule = bodies(read('./invoices-sade.css')).find(item => /invoice-reference-head, \.invoice-reference-row\) \{?$|invoice-reference-head, \.invoice-reference-row\)$/.test(item.selector) && /136px 140px 160px 180px 112px 124px/.test(item.body))
    expect(rule?.media.join(' ')).toMatch(/min-width:\s*1025px/)
  })
})
