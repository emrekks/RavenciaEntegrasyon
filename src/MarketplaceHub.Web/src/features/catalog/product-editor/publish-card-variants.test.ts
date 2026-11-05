import { describe, expect, it } from 'vitest'
import type { VariantPlatformStatus } from '../shared'
import { publishCardPlan, publishCardVariantLinks, publishCardVariantPresence } from './publish-card-variants'

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

describe('publish card plan', () => {
  const matched = (connectionId: string) => status(connectionId, { isLinked: true, isMatched: true })
  const coloured = (color: string, ...platformStatuses: VariantPlatformStatus[]) => ({ color, platformStatuses })

  it('leaves a card whose every variant is there as it is', () => {
    expect(publishCardPlan([coloured('Mavi', matched('c1')), coloured('Mavi', matched('c1'))], 'c1')).toEqual({ send: false, reason: 'matched' })
  })

  it('sends a card none of whose variants is there whole', () => {
    expect(publishCardPlan([coloured('Mavi', status('c1')), coloured('Mavi')], 'c1')).toEqual({ send: true, variants: 2, addsToListing: false, hasMissingVariant: false })
  })

  it('sends only the variants a matched card lacks, on every marketplace', () => {
    // MZ021Y-CD on Trendyol: three sizes matched, three new sizes of the same colour added since.
    const rows = [
      coloured('Mavi', matched('c1')), coloured('Mavi', matched('c1')), coloured('Mavi', matched('c1')),
      coloured('Mavi'), coloured('Mavi', status('c1')), coloured('mavi')
    ]
    expect(publishCardPlan(rows, 'c1')).toEqual({ send: true, variants: 3, addsToListing: true, hasMissingVariant: false })
  })

  it('counts a variant the marketplace lost among those sent again', () => {
    const rows = [coloured('Mavi', matched('c1')), coloured('Mavi', status('c1', { isMissing: true }))]
    expect(publishCardPlan(rows, 'c1')).toEqual({ send: true, variants: 1, addsToListing: true, hasMissingVariant: true })
  })

  it('does not send variants of another colour saved into a matched card', () => {
    // Live 2026-11-05: Bordo, Haki, Kahverengi … sizes were saved into the Mavi card; sent with it they would open under
    // the Mavi card's title and images.
    const plan = publishCardPlan([coloured('Mavi', matched('c1')), coloured('Mavi'), coloured('Bordo'), coloured('Haki'), coloured('Bordo')], 'c1')
    expect(plan).toEqual({
      send: false,
      reason: 'colour-mismatch',
      message: 'Kart pazaryerinde Mavi rengiyle eşleşmiş; eklenen 4 varyanttan 3 tanesi başka renkte (Bordo, Haki). Bu kartla gönderilirlerse bu kartın adı ve görselleriyle açılırlar; kart gönderilmedi. Bu varyantları kendi renk kartlarına ekleyin.'
    })
  })

  it('sends the new variants of a card without a colour option', () => {
    expect(publishCardPlan([{ platformStatuses: [matched('c1')] }, { platformStatuses: [] }], 'c1')).toEqual({ send: true, variants: 1, addsToListing: true, hasMissingVariant: false })
  })
})
