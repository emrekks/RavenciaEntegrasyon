import { describe, expect, it } from 'vitest'

// node:fs without @types/node, the way dashboard-sade-css.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./rr-components.css', testFileUrl), 'utf8')

describe('rr-components.css', () => {
  // .rr-opt:has(.rr-opt-count.is-zero) made the browser restyle ~5 000 elements of a list page whenever anything was added
  // to the page (a window, a menu, the bell panel): 250-300 ms per open on a 4x slowed CPU. The row carries is-zero itself.
  it('dims a zero-count choice by its own class, not by :has()', () => {
    expect(css.includes('.rr-opt:has(')).toBe(false)
    expect(css.includes(':is(main .rr.rr, .rr-layer.rr-layer) .rr-opt.rr-opt.is-zero > :not(input) { opacity: .5; }')).toBe(true)
  })
})
