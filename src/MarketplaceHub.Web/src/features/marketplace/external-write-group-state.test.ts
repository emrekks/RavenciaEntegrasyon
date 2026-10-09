import { describe, expect, it } from 'vitest'
import { externalWriteEvidenceState, externalWriteGroupState } from './external-write-group-state'

describe('externalWriteGroupState', () => {
  it('explains missing Shopify permissions instead of asking for another successful connection test', () => {
    expect(externalWriteEvidenceState([
      { code: 'SHIPMENT_WRITE', verifiedForConnection: true, supportLevel: 'SUPPORTED', verifiedAt: '2026-10-09T10:00:00Z' },
      { code: 'RETURN_READ', verifiedForConnection: false, supportLevel: 'NOTSUPPORTED', verifiedAt: '2026-10-09T10:00:00Z', requiredScope: 'read_returns,read_marketplace_returns', evidenceNote: 'Shopify uygulamasında read_returns izni yok.' },
      { code: 'RETURN_WRITE', verifiedForConnection: false, supportLevel: 'NOTSUPPORTED', verifiedAt: '2026-10-09T10:00:00Z', requiredScope: 'write_returns,write_marketplace_returns' },
    ], 'Shopify')).toEqual({
      blocked: true,
      label: 'Shopify izni eksik',
      details: 'Shopify uygulamasında read_returns izni yok. write_returns,write_marketplace_returns',
    })
  })

  it('keeps automatic and manual groups independent', () => {
    const automatic = externalWriteGroupState([
      { enabled: true, requiresExternalWrites: true },
      { enabled: true, requiresExternalWrites: true },
    ], true, false)
    const manual = externalWriteGroupState([
      { enabled: false, requiresExternalWrites: true },
      { enabled: false, requiresExternalWrites: true },
    ], true, false)

    expect(automatic).toMatchObject({ enabled: true, disabled: false, label: 'Açık' })
    expect(manual).toMatchObject({ enabled: false, disabled: false, label: 'Kapalı' })
  })

  it('blocks enabling a group while the connection master is off', () => {
    expect(externalWriteGroupState([
      { enabled: false, requiresExternalWrites: true },
    ], false, false)).toMatchObject({ blocked: true, disabled: true, label: 'Dış yazma kapalı' })
  })

  it('keeps enabled groups switchable off when capability evidence is no longer available', () => {
    expect(externalWriteGroupState([
      { enabled: true, requiresExternalWrites: true },
    ], true, true)).toMatchObject({ enabled: false, disabled: false, label: 'Bağlantı testi gerekli' })
  })

  it('explains missing Shopify write scopes after a successful connection test', () => {
    expect(externalWriteGroupState([
      { enabled: false, requiresExternalWrites: true },
    ], true, true, false, 'Shopify izni eksik')).toMatchObject({ disabled: true, label: 'Shopify izni eksik' })
  })

  it('disables a group when none of its policies are available', () => {
    expect(externalWriteGroupState([], true, false)).toMatchObject({ disabled: true, label: 'Kapalı' })
  })
})
