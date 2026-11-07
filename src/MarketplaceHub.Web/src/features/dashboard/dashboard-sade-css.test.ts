import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way orders-sade-css.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
// The URL is built from a variable so the bundler does not turn it into an asset address.
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./dashboard-sade.css', testFileUrl), 'utf8')

function mediaBlock(query: string) {
  const start = css.indexOf(`@media ${query} {`)
  if (start < 0) return ''
  let depth = 0
  let end = start
  for (; end < css.length; end += 1) {
    if (css[end] === '{') depth += 1
    if (css[end] === '}') { depth -= 1; if (depth === 0) break }
  }
  return css.slice(start, end + 1)
}

describe('Entegrasyon Durumu table on desktop', () => {
  it('never scrolls up and down inside its panel', () => {
    expect(css).toMatch(/\.rr-mxwrap \{[^}]*overflow-x: auto; overflow-y: hidden;/)
  })

  // At 1025-1279 px the table was wider than its panel and slid sideways under an invisible scrollbar when the strip
  // under its last row was clicked; it now fits by tighter cells and values that may take a second line.
  it('fits its panel between the card layout and 1280 px', () => {
    const block = mediaBlock('(min-width: 1025px) and (max-width: 1279px)')
    expect(block).toContain('.rr-mxt td { padding-left: 5px; padding-right: 5px; }')
    expect(block).toContain('.rr-mxt .rr-mx { white-space: normal; }')
    expect(block).toContain('.rr-mxt .rr-mx-nm small { max-width: 130px; }')
  })
})
