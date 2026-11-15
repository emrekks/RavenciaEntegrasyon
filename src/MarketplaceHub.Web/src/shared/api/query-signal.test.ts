import { describe, expect, it } from 'vitest'

// WEB-ALTYAPI-11: a query hands TanStack Query's signal to its request ({ signal }) => hubApi(path, { signal }), so a
// query that left the screen or was replaced is aborted instead of running on. The reads below still take no signal:
// they go through a helper of their own (product pages, order pages, tariff rows, the publication checks, /me) that
// would need the signal threaded through first. The number may only go down.
const queriesWithoutSignal = 19

const sources = import.meta.glob<string>(['../../**/*.ts', '../../**/*.tsx', '!../../**/*.test.ts', '!../../**/*.test.tsx'], { query: '?raw', import: 'default', eager: true })

describe('query signals', () => {
  it('reads the app sources', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(50)
  })

  it(`leaves no more than ${queriesWithoutSignal} queries without the abort signal`, () => {
    const found = Object.entries(sources).flatMap(([file, text]) =>
      [...text.matchAll(/queryFn:\s*(?:async\s*)?\(\s*\)\s*=>/g)].map(match => `${file}:${text.slice(0, match.index).split('\n').length}`))
    expect(found.length, `queries without a signal:\n${found.join('\n')}`).toBeLessThanOrEqual(queriesWithoutSignal)
  })
})
