import { describe, expect, it } from 'vitest'
import { productPlatformDisplayLabel, productPlatformDisplayState } from './product-platform-status'

describe('product platform list status', () => {
  it('shows imported and fully matched variants as linked without claiming publication', () => {
    const state = productPlatformDisplayState(['LINKED'], false, 35)

    expect(state).toBe('linked')
    expect(productPlatformDisplayLabel('Ravencia Canlı', ['LINKED'], 35, 35, state))
      .toBe('Ravencia Canlı Bağlantısı Var (35/35 Varyant)')
  })

  it('shows half-approved cards as partly live awaiting approval, not processing', () => {
    const state = productPlatformDisplayState(['APPROVAL_PARTIAL_PENDING', 'APPROVAL_PARTIAL_PENDING'], false, 12)

    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Pazarama', ['APPROVAL_PARTIAL_PENDING'], 12, 24, state))
      .toBe('Pazarama: Kısmen Yayında · Kısmi Onay Bekliyor (12/24)')
    expect(productPlatformDisplayState(['APPROVAL_PENDING', 'APPROVAL_PARTIAL_PENDING'], false, 3)).toBe('processing')
  })

  it('shows sends still waiting for the marketplace as processing, not inactive', () => {
    expect(productPlatformDisplayState(['CREATE_RESULT_UNKNOWN'], false, 0)).toBe('processing')
    expect(productPlatformDisplayState(['SHOPIFY_SUBMITTED'], false, 0)).toBe('processing')
  })

  it('shows partial marketplace matching as linked with its coverage', () => {
    const state = productPlatformDisplayState(['PARTIAL_LINKED'], false, 2)

    expect(state).toBe('linked')
    expect(productPlatformDisplayLabel('Trendyol', ['PARTIAL_LINKED'], 2, 4, state))
      .toBe('Trendyol Bağlantısı Var (2/4 Varyant)')
  })

  it('recognizes explicit mapping status when counts are unavailable', () => {
    const state = productPlatformDisplayState(['MAPPED'], false)

    expect(state).toBe('linked')
    expect(productPlatformDisplayLabel('Trendyol', ['MAPPED'], 0, 0, state))
      .toBe('Trendyol Bağlantısı Var')
  })

  it('shows Hepsiburada imports as linked after the matched variant data refreshes', () => {
    const state = productPlatformDisplayState(['IMPORT_ACCEPTED'], false, 2)

    expect(state).toBe('linked')
    expect(productPlatformDisplayLabel('Hepsiburada', ['IMPORT_ACCEPTED'], 2, 2, state))
      .toBe('Hepsiburada Bağlantısı Var (2/2 Varyant)')
  })

  it('shows Hepsiburada import processing states as in progress', () => {
    expect(productPlatformDisplayState(['IMPORT_SUBMITTED'], false)).toBe('processing')
    expect(productPlatformDisplayState(['IMPORT_IN_PROGRESS'], false)).toBe('processing')
  })

  it('shows a confirmed live listing as active', () => {
    const state = productPlatformDisplayState(['LIVE'], false)

    expect(state).toBe('active')
    expect(productPlatformDisplayLabel('Trendyol', ['LIVE'], 35, 35, state))
      .toBe('Trendyol: Ürün Yayında (35/35)')
  })

  it('is green only when every variant of the row is live there', () => {
    // RYP006 on Trendyol, 10 Nov 2026: the card is LIVE but the Mürdüm colour added after the send has no listing and no
    // link, 18 of 21.
    const state = productPlatformDisplayState(['LIVE'], false, 18, 21)
    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Trendyol', ['LIVE'], 18, 21, state)).toBe('Trendyol: Kısmen Yayında (18/21)')
    // A family of several LIVE colour cards with one colour short.
    expect(productPlatformDisplayState(['LIVE', 'LIVE', 'LIVE'], false, 30, 36)).toBe('partial')
    // Every variant there stays green, on every marketplace.
    expect(productPlatformDisplayState(['LIVE', 'LIVE'], false, 21, 21)).toBe('active')
    expect(productPlatformDisplayLabel('Shopify', ['LIVE'], 21, 21, 'active')).toBe('Shopify: Ürün Yayında (21/21)')
    // LIVE beside LINKED with every variant matched stays green (MZ028IH); one short is partly live.
    expect(productPlatformDisplayState(['LIVE', 'LINKED'], false, 10, 10)).toBe('active')
    expect(productPlatformDisplayState(['LIVE', 'LINKED'], false, 9, 10)).toBe('partial')
  })

  it('shows partial publication only when the marketplace reports live variants', () => {
    const state = productPlatformDisplayState(['LIVE', 'UNKNOWN'], false)

    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Trendyol', ['LIVE', 'UNKNOWN'], 1, 2, state))
      .toBe('Trendyol: Kısmen Yayında (1/2)')
  })

  it('does not let a rejected sibling hide a confirmed live listing', () => {
    const statuses = ['LIVE', 'CREATE_REJECTED']
    const state = productPlatformDisplayState(statuses, false)

    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Trendyol', statuses, 35, 70, state))
      .toBe('Trendyol: Kısmen Yayında (35/70)')
  })

  it('keeps a split-content product visibly partially live when only some variants are approved', () => {
    const state = productPlatformDisplayState(['PARTIAL_LIVE'], false)

    expect(state).toBe('partial')
    expect(productPlatformDisplayLabel('Trendyol', ['PARTIAL_LIVE'], 17, 35, state))
      .toBe('Trendyol: Kısmen Yayında (17/35)')
  })

  it('keeps unmatched variants neutral until publication is confirmed', () => {
    expect(productPlatformDisplayState(['UNLINKED'], false)).toBe('inactive')
    expect(productPlatformDisplayState(['BATCH_IN_PROGRESS'], false)).toBe('processing')
    expect(productPlatformDisplayState(['CREATE_REJECTED'], false)).toBe('error')
  })

  it('shows a platform where only some variants failed as partly working', () => {
    expect(productPlatformDisplayState(['LINKED', 'UPDATE_REJECTED'], false, 2)).toBe('partial')
    expect(productPlatformDisplayState(['UPDATE_REJECTED', 'CREATE_REJECTED'], false, 2)).toBe('error')
  })

  it('says Bulunamadı when every published card is gone from the marketplace', () => {
    const state = productPlatformDisplayState(['NOT_FOUND'], false, 0)

    expect(state).toBe('missing')
    expect(productPlatformDisplayLabel('Trendyol', ['NOT_FOUND'], 0, 3, state)).toBe('Trendyol: Pazaryerinde Bulunamadı')
    // A family card never sent there does not make a gone one partly live.
    expect(productPlatformDisplayState(['NOT_FOUND', 'UNLINKED'], false, 0)).toBe('missing')
  })

  it('shows a family with some cards gone and others live as partly live', () => {
    expect(productPlatformDisplayState(['NOT_FOUND', 'LIVE'], false, 3)).toBe('partial')
    expect(productPlatformDisplayState(['NOT_FOUND', 'LINKED'], false, 3)).toBe('partial')
    expect(productPlatformDisplayState(['NOT_FOUND', 'QUEUED'], false, 0)).toBe('processing')
  })
})
