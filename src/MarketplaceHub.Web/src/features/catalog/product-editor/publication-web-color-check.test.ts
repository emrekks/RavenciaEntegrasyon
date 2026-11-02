import { describe, expect, it } from 'vitest'
import { publicationProductChecks, type CategoryRequirement, type PublicationReadinessDraft, type ReadinessRow } from './publication-readiness'

// Live 2026-11-02, MZ068BT (Tunik): the Trendyol card showed "Web Color" as a required gap while all seven colours had
// verified Web Color mappings and the server sent the 28 variants with them. Automatic Web Color is built from each
// variant's own Renk (ProductPublicationComposer), so the check reads the variant rows the same way.
const renk: CategoryRequirement = {
  attributeId: 'renk', isRequired: true, allowsCustomValue: true, displayOrder: 1, role: 'OPTION', isWebColor: true,
  attribute: { id: 'renk', version: 1, code: 'option-renk', name: 'Renk', dataType: 'LIST', values: [{ id: 'bej', value: 'Bej' }, { id: 'murdum', value: 'Mürdüm' }] }
}
const row = (color: string | null): ReadinessRow => ({ optionSignature: '', options: color ? { Beden: 'L', Renk: color } : { Beden: 'L' }, attributeValueIds: {}, barcode: 'B' })
const draft = (rows: ReadinessRow[], overrides: Partial<PublicationReadinessDraft> = {}): PublicationReadinessDraft => ({
  title: 'Tunik', description: 'Açıklama', brandId: 'b', modelCode: 'MZ068BT', barcode: '', categoryId: 'tunik', automaticBarcodeGeneration: false,
  mediaCount: 2, variantAttributeIds: [], variantRows: rows, attributeSelections: {}, attributeTextValues: {}, webColorAutoEnabled: true, manualWebColorValueId: '',
  ...overrides
})
const webColor = (input: PublicationReadinessDraft) =>
  publicationProductChecks(input, { items: [renk], isLoading: false, isError: false, isSuccess: true }).find(check => check.title === 'Web Color')!

describe('automatic Web Color check', () => {
  it('passes when every variant carries a known Renk, synced option group or not', () => {
    expect(webColor(draft([row('Bej'), row('Mürdüm')])).ok).toBe(true)
    expect(webColor(draft([row('Bej')], { variantAttributeIds: ['renk'] })).ok).toBe(true)
  })

  it('names what the server would refuse', () => {
    const missing = webColor(draft([row('Bej'), row(null)]))
    expect(missing.ok).toBe(false)
    expect(missing.detail).toContain('1 varyantta Renk seçilmemiş')
    const unknown = webColor(draft([row('Bej'), row('Petrol'), row('Petrol')]))
    expect(unknown.ok).toBe(false)
    expect(unknown.detail).toBe('Web Color için katalogda karşılığı olmayan renk: Petrol.')
  })

  it('keeps the old rule for a product without variant rows', () => {
    expect(webColor(draft([])).ok).toBe(false)
    expect(webColor(draft([], { attributeSelections: { renk: ['bej'] } })).ok).toBe(true)
  })
})
