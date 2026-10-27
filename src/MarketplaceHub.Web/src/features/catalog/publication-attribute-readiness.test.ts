import { describe, expect, it } from 'vitest'
import { classifyPublicationAttributeIssues, fixableValueMappings } from './publication-attribute-readiness'

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

const withoutFix = (issues: Array<{ attribute: string; detail: string }>) => issues.map(({ attribute, detail }) => ({ attribute, detail }))

describe('publication attribute readiness', () => {
  it('shows an unmapped optional selected value as a warning, not a blocker', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      valueReferencesByAttribute: { 'remote-extra': { ...baseInput.valueReferencesByAttribute['remote-extra'], mappings: [] } }
    })

    expect(withoutFix(result.requiredIssues)).toEqual([])
    expect(withoutFix(result.optionalWarnings)).toEqual([{ attribute: 'Ek Özellik', detail: '“Extra Soft” için güncel Trendyol değer eşlemesi yok.' }])
  })

  it('lets an unmapped value through where the marketplace sends it as free text, and only there', () => {
    const unmapped = { ...baseInput.valueReferencesByAttribute['remote-extra'], snapshotId: '', items: [], mappings: [] }
    const freeTextField = [{ ...baseInput.remoteAttributes[0], allowsCustomValue: true }]
    const n11 = classifyPublicationAttributeIssues({ ...baseInput, platformName: 'n11', remoteAttributes: freeTextField, valueReferencesByAttribute: { 'remote-extra': unmapped }, sendsUnmappedValuesAsFreeText: true })
    expect(n11.requiredIssues).toEqual([])
    expect(n11.optionalWarnings).toEqual([])

    const listOnly = classifyPublicationAttributeIssues({ ...baseInput, platformName: 'n11', valueReferencesByAttribute: { 'remote-extra': unmapped }, sendsUnmappedValuesAsFreeText: true })
    expect(withoutFix(listOnly.optionalWarnings)).toEqual([{ attribute: 'Ek Özellik', detail: '“Extra Soft” için güncel n11 değer eşlemesi yok.' }])

    const otherMarketplace = classifyPublicationAttributeIssues({ ...baseInput, remoteAttributes: freeTextField, valueReferencesByAttribute: { 'remote-extra': unmapped } })
    expect(withoutFix(otherMarketplace.optionalWarnings)).toEqual([{ attribute: 'Ek Özellik', detail: '“Extra Soft” için güncel Trendyol değer eşlemesi yok.' }])
  })

  it('blocks a required attribute whose selected value has no current verified mapping', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], isRequired: true }],
      valueReferencesByAttribute: { 'remote-extra': { ...baseInput.valueReferencesByAttribute['remote-extra'], mappings: [] } }
    })

    expect(withoutFix(result.requiredIssues)).toEqual([{ attribute: 'Ek Özellik', detail: '“Extra Soft” için güncel Trendyol değer eşlemesi yok.' }])
    expect(withoutFix(result.optionalWarnings)).toEqual([])
  })

  it('does not report a required attribute the connection fills with a default', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [],
      remoteAttributes: [...baseInput.remoteAttributes, { externalId: 'origin', name: 'Menşei', isActive: true, isRequired: true, allowsCustomValue: false }],
      defaultedAttributeIds: ['origin']
    })

    expect(result.requiredIssues).toEqual([])
  })

  it('carries what the publish card needs to map a missing value in place', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], isRequired: true }],
      valueReferencesByAttribute: { 'remote-extra': { ...baseInput.valueReferencesByAttribute['remote-extra'], mappings: [] } }
    })

    expect(result.requiredIssues[0].fix).toEqual({ externalAttributeId: 'remote-extra', attributeName: 'Ek Özellik', localValueId: 'soft', valueLabel: 'Extra Soft' })
    expect(fixableValueMappings({ ...result, optionalWarnings: [result.requiredIssues[0]] })).toHaveLength(1)
    expect(fixableValueMappings(undefined)).toEqual([])
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
    expect(withoutFix(result.requiredIssues)).toEqual([])
  })

  it('flags every unmapped required Trendyol attribute even before a local value is selected', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      remoteAttributes: [...baseInput.remoteAttributes, { externalId: 'remote-size', name: 'Beden', isActive: true, isRequired: true }]
    })

    expect(withoutFix(result.requiredIssues)).toEqual([{ attribute: 'Beden', detail: 'Zorunlu Trendyol özelliği için güncel ve doğrulanmış eşleme yok.' }])
  })

  it('uses the selected marketplace name in Hepsiburada publication checks', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      platformName: 'Hepsiburada',
      remoteAttributes: [{ externalId: 'remote-size', name: 'Beden', isActive: true, isRequired: true }]
    })

    expect(withoutFix(result.requiredIssues)).toEqual([{ attribute: 'Beden', detail: 'Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.' }])
  })

  it('flags a required Trendyol attribute that is mapped but has no product or variant value', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], values: [], hasCustomValue: false }],
      remoteAttributes: [{ ...baseInput.remoteAttributes[0], isRequired: true }]
    })

    expect(withoutFix(result.requiredIssues)).toEqual([{
      attribute: 'Ek Özellik',
      detail: 'Zorunlu Trendyol özelliği için ürün veya varyant değeri seçilmemiş ya da girilmemiş.'
    }])
  })

  it('accepts a populated value for a mapped required Trendyol attribute', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      remoteAttributes: [{ ...baseInput.remoteAttributes[0], isRequired: true }]
    })

    expect(withoutFix(result.requiredIssues)).toEqual([])
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

    expect(withoutFix(result.requiredIssues)).toEqual([])
  })

  it('uses the mapped Renk attribute for the colour but still asks for the Web Color value mapping', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ attributeId: 'local-color', name: 'Renk', isRequired: true, values: [{ id: 'emerald', label: 'Zümrüt' }], hasCustomValue: false }],
      remoteAttributes: [
        { externalId: 'remote-web-color', name: 'Web Color', isActive: true, isRequired: true, allowsCustomValue: false },
        { externalId: 'remote-color', name: 'Renk', isActive: true, isRequired: true, allowsCustomValue: false }
      ],
      attributeMappings: [
        { localId: 'local-color', externalId: 'remote-web-color', snapshotId: 'attributes-v2', status: 'VERIFIED' },
        { localId: 'local-color', externalId: 'remote-color', snapshotId: 'attributes-v2', status: 'VERIFIED' }
      ],
      valueReferencesByAttribute: {
        'remote-web-color': { snapshotId: 'values-v2', items: [], mappings: [] },
        'remote-color': { snapshotId: 'values-v2', items: [], mappings: [] }
      }
    })

    expect(withoutFix(result.requiredIssues)).toEqual([{ attribute: 'Web Color', detail: '“Zümrüt” için güncel Trendyol Web Color değer eşlemesi yok.' }])
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

    expect(withoutFix(result.requiredIssues)).toEqual([{ attribute: 'Renk', detail: '“Zümrüt” için güncel Trendyol değer eşlemesi yok.' }])
  })

  it('says nothing about a panel attribute the marketplace category has no field for', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      platformName: 'Hepsiburada',
      selectedAttributes: [...baseInput.selectedAttributes, { attributeId: 'local-pocket', name: 'Cep', isRequired: true, values: [{ id: 'yes', label: 'Var' }], hasCustomValue: false }]
    })
    expect(result.requiredIssues).toEqual([])
    expect(result.optionalWarnings.map(issue => issue.attribute)).not.toContain('Cep')
  })

  it('warns when the category has a same-named field that is still unmapped', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      remoteAttributes: [...baseInput.remoteAttributes, { externalId: 'remote-pocket', name: 'CEP', isActive: true, isRequired: false, allowsCustomValue: false }],
      selectedAttributes: [...baseInput.selectedAttributes, { attributeId: 'local-pocket', name: 'Cep', isRequired: true, values: [{ id: 'yes', label: 'Var' }], hasCustomValue: false }]
    })
    expect(result.requiredIssues).toEqual([])
    expect(result.optionalWarnings.find(issue => issue.attribute === 'Cep')?.detail).toContain('“CEP” alanı eşlenmemiş')
  })

  it('names the synonym field the category offers (Kumaş Tipi ≈ Materyal) and never blocks for it', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      platformName: 'Hepsiburada',
      remoteAttributes: [...baseInput.remoteAttributes, { externalId: 'materyal', name: 'Materyal', isActive: true, isRequired: false, allowsCustomValue: true }],
      selectedAttributes: [...baseInput.selectedAttributes, { attributeId: 'local-fabric', name: 'Kumaş Tipi', isRequired: true, values: [{ id: 'cotton', label: 'Pamuk' }], hasCustomValue: false }]
    })
    expect(result.requiredIssues).toEqual([])
    expect(result.optionalWarnings.find(issue => issue.attribute === 'Kumaş Tipi')?.detail).toContain('“Materyal”')
  })

  it('does not warn for an unused optional attribute', () => {
    const result = classifyPublicationAttributeIssues({
      ...baseInput,
      selectedAttributes: [{ ...baseInput.selectedAttributes[0], values: [] }],
      attributeMappings: []
    })

    expect(result).toEqual({ requiredIssues: [], optionalWarnings: [] })
  })

  it('blocks a required Web Color that has no value mapping for a selected colour', () => {
    const result = classifyPublicationAttributeIssues({
      selectedAttributes: [{ attributeId: 'local-colour', name: 'Renk', isRequired: false, values: [{ id: 'brown', label: 'Açık Kahve' }], hasCustomValue: false }],
      remoteAttributes: [
        { externalId: 'remote-colour', name: 'Renk', isActive: true, isRequired: true, allowsCustomValue: true },
        { externalId: 'remote-webcolor', name: 'Web Color', isActive: true, isRequired: true, allowsCustomValue: false }
      ],
      attributeSnapshotId: 'attributes-v2',
      attributeMappings: [
        { localId: 'local-colour', externalId: 'remote-colour', snapshotId: 'attributes-v2', status: 'VERIFIED' },
        { localId: 'local-colour', externalId: 'remote-webcolor', snapshotId: 'attributes-v2', status: 'VERIFIED' }
      ],
      valueReferencesByAttribute: { 'remote-webcolor': { snapshotId: 'values-v2', items: [{ externalId: 'remote-brown', isActive: true }], mappings: [] } }
    })

    expect(withoutFix(result.requiredIssues)).toEqual([{ attribute: 'Web Color', detail: '“Açık Kahve” için güncel Trendyol Web Color değer eşlemesi yok.' }])
  })
})
