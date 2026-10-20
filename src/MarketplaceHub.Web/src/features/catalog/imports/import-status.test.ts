import { describe, expect, it } from 'vitest'
import { importSessionPollInterval, importSessionPollMs } from './import-status'

describe('import detail polling (W32)', () => {
  it('polls while the worker moves the import on', () => {
    for (const status of ['FETCHING', 'MATCHING', 'READY_TO_APPLY', 'APPLYING']) expect(importSessionPollInterval(status)).toBe(importSessionPollMs)
  })

  it('stops once the import is finished', () => {
    for (const status of ['COMPLETED', 'PARTIALLY_COMPLETED', 'FAILED', 'CANCELLED']) expect(importSessionPollInterval(status)).toBe(false)
  })

  it('does not poll while the import waits for the person or before it loaded', () => {
    expect(importSessionPollInterval('CREATED')).toBe(false)
    expect(importSessionPollInterval('REVIEW_REQUIRED')).toBe(false)
    expect(importSessionPollInterval(undefined)).toBe(false)
  })
})
