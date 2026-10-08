import { describe, expect, it } from 'vitest'
import { classifyPublicationAttributeIssues } from './publication-attribute-readiness'

const baseInput = {
  selectedAttributes: [{ attributeId: 'local-extra', name: 'Ek Özellik', isRequired: false, values: [{ id: 'soft', label: 'Extra Soft' }], hasCustomValue: false }],
  remoteAttributes: [{ externalId: 'remote-extra', name: 'Ek Özellik', isActive: true, isRequired: false, allowsCustomValue: false }],
  attributeSnapshotId: 'attributes-v2',
  attributeMappings: [{ localId: 'local-extra', externalId: 'remote-extra', snapshotId: 'attributes-v2', status: 'VERIFIED' }],
  valueReferencesByAttribute: {
    'remote-extra': {
      snapshotId: 'values-v2',
      items: [{ externalId: 'remote-soft', isActive: true }],
      mappings: [{ localId: 'soft', externalId: 'remote-soft', snapshotId: 'values-v2', status: 'VERIFIED' }]
    }
  }
}

describe('publication attribute readiness', () => {
  it('shows an unmapped optional selected value as a warning, not a blocker', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      valueReferencesByAttribute: { 'remote-extra': { ...baseInput.valueReferencesByAttribute['remote-extra'], mappings: [] } }
    })

    expect(result.requiredIssues).toEqual([])
    expect(result.optionalWarnings).toEqual([{ attribute: 'Ek Özellik', detail: '“Extra Soft” için güncel Trendyol değer eşlemesi yok.' }])
  })

  it('blocks a required attribute whose selected value has no current verified mapping', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], isRequired: true }],
      valueReferencesByAttribute: { 'remote-extra': { ...baseInput.valueReferencesByAttribute['remote-extra'], mappings: [] } }
    })

    expect(result.requiredIssues).toEqual([{ attribute: 'Ek Özellik', detail: '“Extra Soft” için güncel Trendyol değer eşlemesi yok.' }])
    expect(result.optionalWarnings).toEqual([])
  })

  it('treats stale snapshots and inactive remote values as missing mappings', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      valueReferencesByAttribute: {
        'remote-extra': {
          snapshotId: 'values-v2',
          items: [{ externalId: 'remote-soft', isActive: false }],
          mappings: [{ localId: 'soft', externalId: 'remote-soft', snapshotId: 'values-v1', status: 'VERIFIED' }]
        }
      }
    })

    expect(result.optionalWarnings).toHaveLength(1)
    expect(result.requiredIssues).toEqual([])
  })

  it('flags every unmapped required Trendyol attribute even before a local value is selected', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      remoteAttributes: [...baseInput.remoteAttributes, { externalId: 'remote-size', name: 'Beden', isActive: true, isRequired: true }]
    })

    expect(result.requiredIssues).toEqual([{ attribute: 'Beden', detail: 'Zorunlu Trendyol özelliği için güncel ve doğrulanmış eşleme yok.' }])
  })

  it('uses the selected marketplace name in Hepsiburada publication checks', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      platformName: 'Hepsiburada',
      remoteAttributes: [{ externalId: 'remote-size', name: 'Beden', isActive: true, isRequired: true }]
    })

    expect(result.requiredIssues).toEqual([{ attribute: 'Beden', detail: 'Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.' }])
  })

  it('flags a required Trendyol attribute that is mapped but has no product or variant value', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], values: [], hasCustomValue: false }],
      remoteAttributes: [{ ...baseInput.remoteAttributes[0], isRequired: true }]
    })

    expect(result.requiredIssues).toEqual([{
      attribute: 'Ek Özellik',
      detail: 'Zorunlu Trendyol özelliği için ürün veya varyant değeri seçilmemiş ya da girilmemiş.'
    }])
  })

  it('accepts a populated value for a mapped required Trendyol attribute', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      remoteAttributes: [{ ...baseInput.remoteAttributes[0], isRequired: true }]
    })

    expect(result.requiredIssues).toEqual([])
  })

  it('accepts a custom Renk value without requiring a marketplace color-value mapping', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ attributeId: 'local-color', name: 'Renk', isRequired: true, values: [{ id: 'emerald', label: 'Zümrüt' }], hasCustomValue: false }],
      remoteAttributes: [{ externalId: 'remote-color', name: 'Renk', isActive: true, isRequired: true, allowsCustomValue: false }],
      attributeMappings: [{ localId: 'local-color', externalId: 'remote-color', snapshotId: 'attributes-v2', status: 'VERIFIED' }],
      valueReferencesByAttribute: {
        'remote-color': { snapshotId: 'values-v2', items: [], mappings: [] }
      }
    })

    expect(result.requiredIssues).toEqual([])
  })

  it('still requires an exact value mapping for Web Color values', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ attributeId: 'local-color', name: 'Renk', isRequired: true, values: [{ id: 'emerald', label: 'Zümrüt' }], hasCustomValue: false }],
      remoteAttributes: [{ externalId: 'remote-web-color', name: 'Web Color', isActive: true, isRequired: true, allowsCustomValue: false }],
      attributeMappings: [{ localId: 'local-color', externalId: 'remote-web-color', snapshotId: 'attributes-v2', status: 'VERIFIED' }],
      valueReferencesByAttribute: {
        'remote-web-color': { snapshotId: 'values-v2', items: [], mappings: [] }
      }
    })

    expect(result.requiredIssues).toEqual([{ attribute: 'Renk', detail: '“Zümrüt” için güncel Trendyol değer eşlemesi yok.' }])
  })

  it('does not warn for an unused optional attribute', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], values: [] }],
      attributeMappings: []
    })

    expect(result).toEqual({ requiredIssues: [], optionalWarnings: [] })
  })
})
