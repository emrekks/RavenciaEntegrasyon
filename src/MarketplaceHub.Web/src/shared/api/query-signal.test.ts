import { describe, expect, it } from 'vitest'

// WEB-ALTYAPI-11: a query hands TanStack Query's signal to its request ({ signal }) => hubApi(path, { signal }), so a
// query that left the screen or was replaced is aborted instead of running on. The reads below still take no signal:
// the publication check (loadPublicationAttributeIssues, in PublishPlatformCard and bulk-publish-readiness) reads its
// sources through cached queries of their own that several cards share, so one card's abort must not cancel them
// (W5, scan 2026-11-15). The number may only go down.
const queriesWithoutSignal = 2

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
