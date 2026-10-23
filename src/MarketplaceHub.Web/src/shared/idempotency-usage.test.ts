import { describe, expect, it } from 'vitest'

// A versioned Idempotency-Key (one per record version) must go through sendWithVersionedIdempotencyKey: sent raw, a
// retry after any HTTP answer reuses the key and gets the stored refusal back, or 409 IDEMPOTENCY_RESULT_UNKNOWN after a
// 5xx (G4, 20 Oct; six calls were still raw on 24 Oct). Every source file is read as text and checked.
const sources = import.meta.glob(['../**/*.ts', '../**/*.tsx', '!../**/*.test.ts', '!../**/*.test.tsx'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>
const raw = /'Idempotency-Key':\s*(versionedIdempotencyKey\(|`[^`]*:v\$\{)/

describe('versioned idempotency keys', () => {
  it('are never written straight into a request header', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(50)
    const offenders = Object.entries(sources).filter(([, text]) => raw.test(text)).map(([file]) => file)
    expect(offenders).toEqual([])
  })
})
