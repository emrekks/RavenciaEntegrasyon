import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ calls: [] as Array<{ path: string; init?: RequestInit }>, answers: [] as Array<() => unknown> }))
vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string, init?: RequestInit) => { api.calls.push({ path, init }); return api.answers.shift()?.() }) }
})

import { ApiRequestError } from '../../../shared/api'
import { EMPTY_EDITOR_FORM } from './editor-form'
import { buildCreatePayload, buildUpdatePayload, placeholderIdsToDelete, saveProductUpdate, savedProductFields, STALE_PRODUCT_MESSAGE, CHANGED_DURING_RETRY_MESSAGE, type ProductPayloadInput } from './submit-product-payload'
import type { Product } from '../shared'
import type { VariantDraft } from './editor-types'

const row = (key: string, extra: Partial<VariantDraft> = {}): VariantDraft => ({ key, optionSignature: `Renk:${key}`, options: { Renk: key }, attributeValueIds: {}, sku: `SKU-${key}`, barcode: `B-${key}`, stock: 2, salePrice: 90, listPrice: 100, costPrice: null, mediaRefs: [], ...extra })
const form = { ...EMPTY_EDITOR_FORM, title: 'Elbise', description: '<p>Açıklama<script>x</script></p>', brandId: 'b1', categoryId: 'c1', modelCode: ' M1 ', desi: '2,5', warrantyMonths: '24', deliveryDays: '3', vatRate: '20' }
const input = (rows: VariantDraft[], extra: Partial<ProductPayloadInput> = {}): ProductPayloadInput => ({
  form,
  saved: savedProductFields(form, false, undefined, new Date('2026-11-16T10:00:00Z')),
  fallbackListPrice: 100,
  fallbackSalePrice: 90,
  webColor: { autoEnabled: true, manualValueId: '' },
  globalAttributes: [],
  rows,
  variant: { form, calculateDesi: false, calculatedDesi: 0, optionRequirements: [] },
  ...extra
})
const seeded = (variants: Array<Partial<Product['variants'][number]> & { id: string }>, extra: Partial<Product> = {}) => ({ id: 'p1', version: 4, familyVersions: { p1: 4, p2: 7 }, variants, ...extra }) as unknown as Product

beforeEach(() => { api.calls.length = 0; api.answers.length = 0 })

describe('savedProductFields', () => {
  it('keeps the form’s title, status and VAT on a normal save', () => {
    expect(savedProductFields(form, false, undefined, new Date())).toEqual({ title: 'Elbise', status: 'ACTIVE', vatRate: 20, vatInclusion: 'INCLUDED' })
  })
  it('saves a draft as DRAFT and leaves an invalid VAT empty', () => {
    const fields = savedProductFields({ ...form, vatRate: '' }, true, 'KDV Oranı zorunludur.', new Date())
    expect(fields).toMatchObject({ status: 'DRAFT', vatRate: null, vatInclusion: null })
  })
})

describe('buildCreatePayload', () => {
  it('sends every row as a new variant with the model’s code and desi, the description sanitised', () => {
    const payload = buildCreatePayload(input([row('a', { stock: 3 }), row('b')]))
    expect(payload).toMatchObject({ title: 'Elbise', status: 'ACTIVE', brandId: 'b1', categoryId: 'c1', warrantyMonths: 24, deliveryDays: 3, vatRate: 20, trendyolWebColorValueId: null, attributes: [] })
    expect(payload.description).not.toContain('script')
    expect(payload.variants.map(variant => [variant.sku, variant.modelCode, variant.sortOrder, variant.desi, variant.onHand, variant.weight])).toEqual([['SKU-a', 'M1', 0, 2.5, 3, null], ['SKU-b', 'M1', 1, 2.5, 2, null]])
  })
  it('names a manual Web Color only when the automatic one is off', () => {
    expect(buildCreatePayload(input([row('a')], { webColor: { autoEnabled: false, manualValueId: 'wc1' } })).trendyolWebColorValueId).toBe('wc1')
  })
  it('sends the calculator’s measurements and desi when it was used', () => {
    const measured = { ...form, weight: '1,5', width: '30', height: '20', length: '10' }
    const payload = buildCreatePayload(input([row('a')], { form: measured, variant: { form: measured, calculateDesi: true, calculatedDesi: 2, optionRequirements: [] } }))
    expect(payload.variants[0]).toMatchObject({ weight: 1.5, width: 30, height: 20, length: 10, desi: 2 })
  })
  it('rounds the calculated desi to two decimals', () => {
    const measured = { ...form, width: '11', height: '13', length: '10' }
    const payload = buildCreatePayload(input([row('a')], { form: measured, variant: { form: measured, calculateDesi: true, calculatedDesi: 11 * 13 * 10 / 3000, optionRequirements: [] } }))
    expect(payload.variants[0].desi).toBe(0.48)
  })
  // ORTA-3 (review 23 Nov): a measure the rule cannot read ("12.5") made the calculator's desi 0 and the save stored a
  // silent desi 1. Without a calculated desi the Desi field as typed goes, never 1.
  it('never saves a silent desi 1 when the calculator gives none', () => {
    const measured = { ...form, width: '12.5', height: '20', length: '10' }
    const payload = buildCreatePayload(input([row('a')], { form: measured, variant: { form: measured, calculateDesi: true, calculatedDesi: 0, optionRequirements: [] } }))
    expect(payload.variants[0].desi).toBe(2.5)
    expect(payload.variants[0].width).toBeNull()
  })
})

describe('buildUpdatePayload', () => {
  it('is made against the seeded product: its family versions, its variants and their loaded values', () => {
    const product = seeded([{ id: 'a', modelCode: 'M1', desi: 1 }, { id: 'gone', modelCode: 'M1', desi: 1 }])
    const payload = buildUpdatePayload({ ...input([row('a', { costPrice: 40, loadedCostPrice: 25, loadedVersion: 3 }), row('new')]), persistAttributes: false, removedVariantIds: ['gone', 'unknown'], placeholderIdsToDelete: [], initialModelCode: 'M1' }, product)
    expect(payload.loadedFamilyVersions).toEqual({ p1: 4, p2: 7 })
    expect(payload).not.toHaveProperty('attributes')
    expect(payload.trendyolWebColorValueId).toBe('00000000-0000-0000-0000-000000000000')
    expect(payload.variantIdsToDelete).toEqual(['gone'])
    expect(payload.variantsToCreate.map(variant => [variant.sku, variant.sortOrder])).toEqual([['SKU-new', 0]])
    expect(payload.variantUpdates).toEqual([expect.objectContaining({ id: 'a', costPrice: 40, loadedCostPrice: 25, desi: 2.5, loadedDesi: 1, loadedVersion: 3, sortOrder: 0 })])
    expect(payload.warrantyMonths).toBe(24)
  })
  it('writes the attributes when the category’s requirements are loaded, and an emptied warranty as a clear', () => {
    const payload = buildUpdatePayload({ ...input([row('a')], { form: { ...form, warrantyMonths: '' } }), persistAttributes: true, removedVariantIds: [], placeholderIdsToDelete: ['ph'], initialModelCode: 'M1' }, seeded([{ id: 'a', modelCode: 'M1', desi: 2.5 }]))
    expect(payload).toMatchObject({ attributes: [], warrantyMonths: null, clearWarrantyMonths: true, placeholderVariantIdsToDelete: ['ph'] })
    expect(payload.variantUpdates[0]).not.toHaveProperty('desi')
  })
})

describe('placeholderIdsToDelete', () => {
  it('keeps only saved placeholders that left the table and were not removed by hand', () => {
    expect(placeholderIdsToDelete(['ph', 'kept', 'removed', 'unsaved'], seeded([{ id: 'ph' }, { id: 'kept' }, { id: 'removed' }]), [row('kept')], ['removed'])).toEqual(['ph'])
  })
})

describe('saveProductUpdate', () => {
  const product = seeded([{ id: 'a' }], { title: 'Elbise' } as Partial<Product>)
  const payload = buildUpdatePayload({ ...input([row('a')]), persistAttributes: false, removedVariantIds: [], placeholderIdsToDelete: [], initialModelCode: 'M1' }, product)

  it('names the seeded version in If-Match', async () => {
    api.answers.push(() => ({ ...product, version: 5 }))
    const result = await saveProductUpdate(product, payload)
    expect(result.versionRetried).toBe(false)
    expect(api.calls.map(call => [call.init?.method ?? 'GET', call.path, new Headers(call.init?.headers).get('If-Match')])).toEqual([['PATCH', '/products/p1', '"v4"']])
  })
  it('retries once at the latest version when only the version moved', async () => {
    api.answers.push(() => { throw new ApiRequestError('Sürüm değişti.', 412) }, () => ({ ...product, version: 6 }), () => ({ ...product, version: 7 }))
    const result = await saveProductUpdate(product, payload)
    expect(result.versionRetried).toBe(true)
    expect(api.calls.map(call => [call.init?.method ?? 'GET', new Headers(call.init?.headers).get('If-Match')])).toEqual([['PATCH', '"v4"'], ['GET', null], ['PATCH', '"v6"']])
  })
  it('stops when the latest product differs from the seeded one', async () => {
    api.answers.push(() => { throw new ApiRequestError('Sürüm değişti.', 412) }, () => ({ ...product, version: 6, title: 'Başka' }))
    await expect(saveProductUpdate(product, payload)).rejects.toThrow(STALE_PRODUCT_MESSAGE)
    expect(api.calls).toHaveLength(2)
  })
  it('stops when the retry is refused again', async () => {
    api.answers.push(() => { throw new ApiRequestError('Sürüm değişti.', 412) }, () => ({ ...product, version: 6 }), () => { throw new ApiRequestError('Sürüm değişti.', 412) })
    await expect(saveProductUpdate(product, payload)).rejects.toThrow(CHANGED_DURING_RETRY_MESSAGE)
  })
  it('passes any other refusal on unchanged (409 FAMILY_CHANGED)', async () => {
    const refusal = new ApiRequestError('Modelin başka bir rengi değişti.', 409, 'FAMILY_CHANGED')
    api.answers.push(() => { throw refusal })
    await expect(saveProductUpdate(product, payload)).rejects.toBe(refusal)
  })
})
