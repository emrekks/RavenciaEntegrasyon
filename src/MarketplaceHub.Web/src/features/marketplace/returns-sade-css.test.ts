import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way orders-sade-css.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./returns-sade.css', testFileUrl), 'utf8')

// The CSS outside every @media block: rules that apply at every width.
function unconditional() {
  let out = ''
  let depth = 0
  let inMedia = false
  for (let index = 0; index < css.length; index += 1) {
    if (!inMedia && depth === 0 && css.startsWith('@media', index)) inMedia = true
    const char = css[index]
    if (char === '{') depth += 1
    if (char === '}') { depth -= 1; if (inMedia && depth === 0) { inMedia = false; continue } }
    if (!inMedia) out += char
  }
  return out
}

describe('return product facts on phones', () => {
  it('never keeps a long barcode or stock code on one line outside the desktop table', () => {
    // Live 1 Nov 2026: "MZ035DYD-B24MZ035DYD-B26" ran 8 px past a 375 px card because a rule for every width kept the
    // facts on one line.
    expect(unconditional()).not.toMatch(/\.rr-pline > div > small:not\(\.rr-card-only\) \{[^}]*white-space: nowrap/)
    expect(css).toMatch(/@media \(max-width: 1024px\) \{\n:is\(main \.rr\.rr, \.rr-layer\.rr-layer\) \.rr-pline > div > small:not\(\.rr-card-only\) \{[^}]*white-space: normal; overflow-wrap: anywhere;/)
  })
})
