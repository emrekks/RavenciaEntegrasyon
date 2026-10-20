import { beforeEach, describe, expect, it } from 'vitest'
import { ApiRequestError } from './api/client'
import { failedWithHttpAnswer, idempotencyKeyForAttempt, newIdempotencyKey, resetAnsweredIdempotencyKeys, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from './idempotency'
import { httpsUrlProblem, safeExternalUrl } from './safe-url'

describe('versioned key attempts (G4)', () => {
  const versioned = versionedIdempotencyKey('return-approve', 'claim-1', 7)
  beforeEach(() => resetAnsweredIdempotencyKeys())

  async function attempt(outcome: 'ok' | 'network' | number) {
    const used: string[] = []
    await sendWithVersionedIdempotencyKey(versioned, async key => {
      used.push(key)
      if (outcome === 'network') throw new TypeError('Failed to fetch')
      if (typeof outcome === 'number') throw new ApiRequestError('Sunucu hatası', outcome, 'MARKETPLACE_UNAVAILABLE')
      return 'done'
    }).catch(() => undefined)
    return used[0]
  }

  it('reuses the versioned key for a resend after a network error with no answer', async () => {
    expect(await attempt('network')).toBe(versioned)
    expect(await attempt('network')).toBe(versioned)
  })

  it('switches to a fresh key after a 5xx answer so the next click is not refused with 409 UNKNOWN', async () => {
    expect(await attempt(503)).toBe(versioned)
    const retry = await attempt('ok')
    expect(retry).not.toBe(versioned)
    expect(retry).toMatch(/^return-approve:[0-9a-f-]{36}$/)
  })

  it('switches to a fresh key after a 4xx answer so the stored refusal is not replayed', async () => {
    expect(await attempt(422)).toBe(versioned)
    const second = await attempt(429)
    expect(second).not.toBe(versioned)
    // Every answered key is replaced again.
    expect(await attempt('ok')).not.toBe(second)
  })

  it('keeps the key that succeeded, so a repeat on the same version replays instead of acting twice', async () => {
    expect(await attempt('ok')).toBe(versioned)
    expect(idempotencyKeyForAttempt(versioned)).toBe(versioned)
    await attempt(500)
    const fresh = await attempt('ok')
    expect(idempotencyKeyForAttempt(versioned)).toBe(fresh)
  })

  it('tells an HTTP answer from a network error', () => {
    expect(failedWithHttpAnswer(new ApiRequestError('x', 500))).toBe(true)
    expect(failedWithHttpAnswer(new TypeError('Failed to fetch'))).toBe(false)
    expect(failedWithHttpAnswer('boom')).toBe(false)
  })
})

describe('idempotency keys', () => {
  it('gives a double click on the same version and request the same key', () => {
    const body = JSON.stringify({ action: 'PICKING', payloadJson: '{}' })
    expect(versionedIdempotencyKey('shipment/actions', 'p1', 4, body)).toBe(versionedIdempotencyKey('shipment/actions', 'p1', 4, body))
  })

  it('changes the key once the record moved on or the request differs', () => {
    const body = JSON.stringify({ action: 'PICKING' })
    const key = versionedIdempotencyKey('shipment/actions', 'p1', 4, body)
    expect(versionedIdempotencyKey('shipment/actions', 'p1', 5, body)).not.toBe(key)
    expect(versionedIdempotencyKey('shipment/actions', 'p1', 4, JSON.stringify({ action: 'UNPACK' }))).not.toBe(key)
    expect(versionedIdempotencyKey('shipment/actions', 'p2', 4, body)).not.toBe(key)
  })

  it('stays well inside the 256-character limit', () => {
    expect(versionedIdempotencyKey('return-stock-disposition', crypto.randomUUID(), 12, 'x'.repeat(5000)).length).toBeLessThan(120)
  })

  it('makes a new key for every attempt otherwise', () => {
    expect(newIdempotencyKey('sync')).not.toBe(newIdempotencyKey('sync'))
    expect(newIdempotencyKey('sync')).toMatch(/^sync:[0-9a-f-]{36}$/)
  })
})

describe('addresses', () => {
  it('links only web addresses from records', () => {
    expect(safeExternalUrl('https://www.trendyol.com/x')).toBe('https://www.trendyol.com/x')
    expect(safeExternalUrl('http://example.com')).toBe('http://example.com')
    expect(safeExternalUrl('javascript:alert(1)')).toBeNull()
    expect(safeExternalUrl('data:text/html,hi')).toBeNull()
    expect(safeExternalUrl('//evil.example')).toBeNull()
    expect(safeExternalUrl(null)).toBeNull()
  })

  it('accepts only https addresses for content that will be published', () => {
    expect(httpsUrlProblem('https://cdn.example.com/a.jpg')).toBeNull()
    expect(httpsUrlProblem('http://cdn.example.com/a.jpg')).toBe('Adres https:// ile başlamalı.')
    expect(httpsUrlProblem('https://')).toBe('Geçerli bir https adresi girin.')
    expect(httpsUrlProblem('')).toBe('Adres girin.')
  })
})
