import { describe, expect, it } from 'vitest'
import { newIdempotencyKey, versionedIdempotencyKey } from './idempotency'
import { httpsUrlProblem, safeExternalUrl } from './safe-url'

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
