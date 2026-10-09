import { describe, expect, it } from 'vitest'
import { externalWriteGroupState } from './external-write-group-state'

describe('externalWriteGroupState', () => {
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
