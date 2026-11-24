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

  it('the card-only carrier rules apply to phones and tablets only', () => {
    for (const [path, list] of sources) {
      for (const rule of list.filter(item => /cargo-provider-(icon|display)/.test(item.selector) && !/invoice-detail/.test(item.selector))) {
        const hidesOnly = /^display:\s*none;?$/.test(rule.body)
        if (hidesOnly) continue
        expect(rule.media.join(' '), `${path}: ${rule.selector.slice(0, 90)}`).toMatch(/max-width:\s*(760|1024)px/)
      }
    }
  })

  it('the package number line is shown from 761 px up (hidden only on the base rule)', () => {
    const base = bodies(read('./invoices-responsive.css')).find(rule => rule.selector === '.invoice-reference-order small.invoice-package-no')
    expect(base?.media).toEqual([])
    expect(base?.body).toMatch(/display:\s*none/)
  })

  it('the desktop column grid (from 1025 px): order column capped at 240 px, the other columns share the rest by weight', () => {
    const rule = bodies(read('./invoices-sade.css')).find(item => item.body.includes('minmax(170px, 240px) minmax(124px, 1fr) minmax(160px, .8fr)'))
    expect(rule?.media.join(' ')).toMatch(/min-width:\s*1025px/)
  })

  it('the head select-all box is the row boxes size (16 px) with 8 px before the title', () => {
    const css = read('./invoices-sade.css')
    expect(css).toMatch(/invoice-reference-head .invoice-reference-select-all { width: 16px; height: 16px; flex: 0 0 16px; margin-right: 8px; }/)
  })
})
