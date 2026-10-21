import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way job-categories.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
// The URL is built from a variable so the bundler does not turn it into an asset address.
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./orders-sade.css', testFileUrl), 'utf8')

function phoneBlocks() {
  const blocks: string[] = []
  let index = css.indexOf('@media (max-width: 760px) {')
  while (index >= 0) {
    let depth = 0
    let end = index
    for (; end < css.length; end += 1) {
      if (css[end] === '{') depth += 1
      if (css[end] === '}') { depth -= 1; if (depth === 0) break }
    }
    blocks.push(css.slice(index, end + 1))
    index = css.indexOf('@media (max-width: 760px) {', end)
  }
  return blocks.join('\n')
}

describe('order cards on phones (W-F12)', () => {
  it('show the late shipping line that the other card lines hide', () => {
    const phone = phoneBlocks()
    expect(phone).toContain('.rr-ordercell > div > small:not(:first-of-type) { display: none; }')
    expect(phone).toMatch(/\.rr-ordercell > div > small\.rr-due\.is-late \{ display: block;/)
    expect(phone.indexOf('small.rr-due.is-late')).toBeGreaterThan(phone.indexOf('small:not(:first-of-type)'))
  })
})
