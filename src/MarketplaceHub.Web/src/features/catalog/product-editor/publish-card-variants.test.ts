import { describe, expect, it } from 'vitest'
import type { VariantPlatformStatus } from '../shared'
import { publishCardVariantLinks, publishCardVariantPresence } from './publish-card-variants'

const status = (connectionId: string, extra: Partial<VariantPlatformStatus> = {}): VariantPlatformStatus => ({ platform: 'PTT AVM', platformCode: 'PTTAVM', isLinked: false, connectionId, ...extra })
const row = (...platformStatuses: VariantPlatformStatus[]) => ({ platformStatuses })

describe('publish card variant presence', () => {
  it('counts present and missing variants of the card connection only', () => {
    const rows = [
      row(status('c1', { isLinked: true, isMatched: true }), status('c2', { isMissing: true })),
      row(status('c1', { isMissing: true })),
      row(status('c1')),
      row()
    ]
    expect(publishCardVariantLinks(rows, 'c1')).toEqual({ total: 4, linked: 1, missing: 1 })
    expect(publishCardVariantLinks(rows, 'c2')).toEqual({ total: 4, linked: 0, missing: 1 })
  })

  it('reads a card with every variant present as live, even without links (a family send)', () => {
    // MZ053TCD on PTT AVM: 24 LIVE variants, 9 links; the server marks the LIVE ones matched too.
    const rows = Array.from({ length: 24 }, () => row(status('c1', { isLinked: true, isMatched: true })))
    expect(publishCardVariantPresence(publishCardVariantLinks(rows, 'c1'))).toEqual({ allLinked: true, someLinked: false, noneFound: false })
  })

  it('reads some missing variants as partly live and none left as Bulunamadı', () => {
    expect(publishCardVariantPresence({ total: 3, linked: 2, missing: 1 })).toEqual({ allLinked: false, someLinked: true, noneFound: false })
    expect(publishCardVariantPresence({ total: 3, linked: 0, missing: 3 })).toEqual({ allLinked: false, someLinked: false, noneFound: true })
    expect(publishCardVariantPresence({ total: 3, linked: 0, missing: 1 })).toEqual({ allLinked: false, someLinked: false, noneFound: true })
  })

  it('never reads a variant without a link as missing', () => {
    expect(publishCardVariantPresence(publishCardVariantLinks([row(status('c1')), row()], 'c1'))).toEqual({ allLinked: false, someLinked: false, noneFound: false })
    expect(publishCardVariantPresence(undefined)).toEqual({ allLinked: false, someLinked: false, noneFound: false })
  })
})
