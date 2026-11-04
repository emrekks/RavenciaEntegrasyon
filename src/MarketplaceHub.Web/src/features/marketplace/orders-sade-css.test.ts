import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way job-categories.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
// The URL is built from a variable so the bundler does not turn it into an asset address.
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./orders-sade.css', testFileUrl), 'utf8')

// The card rules serve phones and tablets alike (tablets lay the cards two to a row in styles/tablet-ops.css).
function cardBlocks() {
  const blocks: string[] = []
  let index = css.indexOf('@media (max-width: 1024px) {')
  while (index >= 0) {
    let depth = 0
    let end = index
    for (; end < css.length; end += 1) {
      if (css[end] === '{') depth += 1
      if (css[end] === '}') { depth -= 1; if (depth === 0) break }
    }
    blocks.push(css.slice(index, end + 1))
    index = css.indexOf('@media (max-width: 1024px) {', end)
  }
  return blocks.join('\n')
}

describe('order cards on phones and tablets (W-F12)', () => {
  it('show the late shipping line that the other card lines hide', () => {
    const phone = cardBlocks()
    expect(phone).toContain('.rr-ordercell > div > small:not(:first-of-type) { display: none; }')
    expect(phone).toMatch(/\.rr-ordercell > div > small\.rr-due\.is-late \{ display: block;/)
    expect(phone.indexOf('small.rr-due.is-late')).toBeGreaterThan(phone.indexOf('small:not(:first-of-type)'))
  })
})

describe('order cards on phones open their detail from a button', () => {
  const page = fs.readFileSync(new URL('./orders/OrdersPage.tsx', testFileUrl), 'utf8')
  it('no longer open the detail sheet from a tap anywhere on the card', () => {
    expect(page).toContain('<tr ref={rowRef} className={rowClasses || undefined}>')
    expect(page).not.toMatch(/<tr ref=\{rowRef\}[^>]*onClick/)
  })
  it('put a Detayları Aç button in the action row, shown on phones only', () => {
    expect(page).toMatch(/className="rr-btn rr-detail-open"[^>]*onClick=\{\(\) => setDetailOpen\(true\)\}>Detayları Aç</)
    expect(page.match(/\{detailOpenButton\}/g)).toHaveLength(2)
    expect(css).toContain(':is(main .rr.rr-orders, .rr-layer.rr-layer) .rr-actcell .rr-btn.rr-detail-open { display: none; }')
    expect(css).toMatch(/@media \(max-width: 760px\) \{\n {2}:is\(main \.rr\.rr-orders, \.rr-layer\.rr-layer\) \.rr-actcell \.rr-btn\.rr-detail-open \{ display: inline-flex;/)
  })
})
