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

  it('does not warn for an unused optional attribute', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], values: [] }],
      attributeMappings: []
    })

    expect(result).toEqual({ requiredIssues: [], optionalWarnings: [] })
  })
})
