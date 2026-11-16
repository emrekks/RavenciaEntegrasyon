// The product write of Kaydet / Ürünü Oluştur / Taslak (WEB-SAYFALAR-10): the bodies of POST /products and PATCH
// /products/{id} built from the form (pure), and the PATCH at the version the form was filled from, retried once at
// the latest version only when nothing the form shows changed meanwhile.
import { ApiRequestError, hubApi } from '../../../shared/api'
import { sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { sanitizeRichText } from '../../../shared/security/sanitizeHtml'
import { buildVariantAttributeAssignments } from '../variant-attribute-assignments'
import { modelCodeForExistingVariant } from '../product-media-editor'
import { isOptionRequirement, type CategoryRequirement } from './publication-readiness'
import { FAMILY_SHARED_FIELDS } from './family-shared-fields'
import { productDeliveryDays } from './product-delivery-days'
import { editorWarrantyMonths, editorWarrantyUpdate } from './required-fields'
import { parseProductDesi } from '../product-desi'
import { draftProductTitle } from '../product-draft'
import { sameProductSnapshotIgnoringVersion } from '../product-save-version'
import { editorOpeningStock, editorVariantCostUpdate, editorVariantDesiUpdate } from '../product-editor-state'
import { parseEditorNumber } from './editor-number'
import { productAttributePayload } from './editor-helpers'
import type { Product } from '../shared'
import type { ProductAttributePayload, VariantDraft } from './editor-types'
import type { EditorForm } from './editor-form'

const NO_WEB_COLOR = '00000000-0000-0000-0000-000000000000'

// What the save writes for the card's title, status and VAT: a draft takes a dated title and keeps an invalid VAT empty.
export function savedProductFields(form: Pick<EditorForm, 'title' | 'modelCode' | 'status' | 'vatRate' | 'vatIncluded'>, saveDraft: boolean, vatRateIssue: string | undefined, now: Date) {
  return {
    title: saveDraft ? draftProductTitle(form.title, form.modelCode, now) : form.title,
    status: saveDraft ? 'DRAFT' : form.status,
    vatRate: saveDraft && vatRateIssue ? null : parseEditorNumber(form.vatRate || 0),
    vatInclusion: saveDraft && vatRateIssue ? null : form.vatIncluded || 'INCLUDED'
  }
}
export type SavedProductFields = ReturnType<typeof savedProductFields>

// The card's own attributes: every mapped one but the option axes and the variant attributes, the Web Color last.
export function buildGlobalAttributes(input: { requirements: CategoryRequirement[]; webColorRequirement: CategoryRequirement | undefined; variantAttributeIds: string[]; attributeSelections: Record<string, string[]>; attributeTextValues: Record<string, string> }): ProductAttributePayload[] {
  const { requirements, webColorRequirement, variantAttributeIds, attributeSelections, attributeTextValues } = input
  const regular = requirements
    .filter(item => !isOptionRequirement(item) && item.attributeId !== webColorRequirement?.attributeId && !variantAttributeIds.includes(item.attributeId))
    .flatMap((item, index) => productAttributePayload(item, attributeSelections[item.attributeId] ?? [], attributeTextValues[item.attributeId] ?? '', index))
  // The manual Web Color is sent apart (trendyolWebColorValueId) so it never becomes the product's Renk on other marketplaces.
  const colour = webColorRequirement && !variantAttributeIds.includes(webColorRequirement.attributeId)
    ? productAttributePayload(webColorRequirement, attributeSelections[webColorRequirement.attributeId] ?? [], attributeTextValues[webColorRequirement.attributeId] ?? '', regular.length)
    : []
  return [...regular, ...colour]
}

export type OptionRequirementForSubmit = { attributeId: string; name: string; values: CategoryRequirement['attribute']['values'] }

export function optionRequirementsForSubmit(requirements: CategoryRequirement[]): OptionRequirementForSubmit[] {
  return requirements.filter(isOptionRequirement).map(item => ({ attributeId: item.attributeId, name: item.attribute.name, values: item.attribute.values }))
}

// What every variant of the save shares: the model code, the desi (the model's) and the option requirements.
export type VariantPayloadContext = {
  form: Pick<EditorForm, 'modelCode' | 'weight' | 'width' | 'height' | 'length' | 'desi'>
  calculateDesi: boolean
  // The desi the calculator gives (0 when its measurements are not filled).
  calculatedDesi: number
  optionRequirements: OptionRequirementForSubmit[]
}

const variantAttributes = (row: VariantDraft, context: VariantPayloadContext) => buildVariantAttributeAssignments({
  options: row.options,
  selectedValueIds: row.attributeValueIds,
  existing: row.persistedAttributes ?? [],
  optionRequirements: context.optionRequirements
})

const savedDesi = (context: VariantPayloadContext) => context.calculateDesi ? context.calculatedDesi || 1 : parseProductDesi(context.form.desi)

const measurement = (context: VariantPayloadContext, value: string) => context.calculateDesi ? parseEditorNumber(value) || null : null

// A variant the save creates (a new card's every row, an edit's new rows).
export function buildVariantPayload(row: VariantDraft, index: number, context: VariantPayloadContext) {
  const { form } = context
  return { sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode.trim() || null, sortOrder: index, weight: measurement(context, form.weight), width: measurement(context, form.width), height: measurement(context, form.height), length: measurement(context, form.length), desi: savedDesi(context) || null, costPrice: row.costPrice, defaultListPrice: row.listPrice, defaultSalePrice: row.salePrice, options: row.options, attributes: variantAttributes(row, context), onHand: editorOpeningStock(row.stock) }
}

export type ProductPayloadInput = {
  form: EditorForm
  saved: SavedProductFields
  fallbackListPrice: number
  fallbackSalePrice: number
  webColor: { autoEnabled: boolean; manualValueId: string }
  globalAttributes: ProductAttributePayload[]
  rows: VariantDraft[]
  variant: VariantPayloadContext
}

export function buildCreatePayload(input: ProductPayloadInput) {
  const { form, saved, webColor } = input
  return { title: saved.title, status: saved.status, description: sanitizeRichText(form.description), brandId: form.brandId || null, categoryId: form.categoryId || null, defaultListPrice: input.fallbackListPrice, defaultSalePrice: input.fallbackSalePrice, warrantyMonths: editorWarrantyMonths(form.warrantyMonths), deliveryDays: productDeliveryDays(form.deliveryDays), vatRate: saved.vatRate, vatInclusion: saved.vatInclusion, trendyolWebColorValueId: !webColor.autoEnabled && webColor.manualValueId ? webColor.manualValueId : null, attributes: input.globalAttributes, variants: input.rows.map((row, index) => buildVariantPayload(row, index, input.variant)) }
}
export type ProductCreatePayload = ReturnType<typeof buildCreatePayload>

// The saved "Tek Ürün" placeholders replaced by generated rows that the server may remove (it keeps one with history).
export function placeholderIdsToDelete(replacedPlaceholderIds: string[], seeded: Product | undefined, rows: VariantDraft[], removedVariantIds: string[]) {
  const existing = new Set(seeded?.variants.map(variant => variant.id) ?? [])
  return replacedPlaceholderIds.filter(id => existing.has(id) && !rows.some(row => row.key === id) && !removedVariantIds.includes(id))
}

export type ProductUpdatePayloadInput = ProductPayloadInput & {
  // Whether the card's attributes are written: not while a newly picked category's requirements are loading or failed.
  persistAttributes: boolean
  removedVariantIds: string[]
  placeholderIdsToDelete: string[]
  // The model code the edit form opened with: a row keeps its own code unless the model code was changed.
  initialModelCode: string
}

// The edit's PATCH body, made against the product the form was filled from (seeded, WEB-SAYFALAR-1): its variants,
// model codes, desi and colour card versions, not a newer refetch held back by the stale banner.
export function buildUpdatePayload(input: ProductUpdatePayloadInput, seeded: Product) {
  const { form, saved, webColor, rows } = input
  const existingVariantIds = new Set(seeded.variants.map(variant => variant.id))
  return {
    title: saved.title,
    status: saved.status,
    description: sanitizeRichText(form.description),
    brandId: form.brandId || null,
    categoryId: form.categoryId || null,
    defaultListPrice: input.fallbackListPrice,
    defaultSalePrice: input.fallbackSalePrice,
    ...editorWarrantyUpdate(form.warrantyMonths),
    deliveryDays: productDeliveryDays(form.deliveryDays),
    vatRate: saved.vatRate,
    vatInclusion: saved.vatInclusion,
    trendyolWebColorValueId: !webColor.autoEnabled && webColor.manualValueId ? webColor.manualValueId : NO_WEB_COLOR,
    // Temel Ürün Bilgileri are the model's: every save writes them to every colour card (family-shared-fields.ts).
    familySharedFields: [...FAMILY_SHARED_FIELDS],
    // The colour cards' versions as the form was filled (WEB-SAYFALAR-2): a card changed meanwhile whose shared
    // field this save would overwrite refuses it (409 FAMILY_CHANGED) instead.
    loadedFamilyVersions: seeded.familyVersions ?? null,
    ...(input.persistAttributes ? { attributes: input.globalAttributes } : {}),
    variantIdsToDelete: input.removedVariantIds.filter(id => existingVariantIds.has(id) && !rows.some(row => row.key === id)),
    placeholderVariantIdsToDelete: input.placeholderIdsToDelete,
    variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map((row, index) => buildVariantPayload(row, index, input.variant)),
    variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({
      id: row.key,
      sku: row.sku,
      barcode: row.barcode || null,
      modelCode: modelCodeForExistingVariant(form.modelCode, input.initialModelCode, seeded.variants.find(variant => variant.id === row.key)?.modelCode),
      // Only a cost the user changed is sent, with the cost it was loaded with; an unsent (null) cost is left as
      // it is on the server, and a cost changed there meanwhile refuses the save (409).
      ...editorVariantCostUpdate(row),
      // The desi is the model's too (owner, 14.11.2026): every save gives it to every size of every colour.
      ...editorVariantDesiUpdate(savedDesi(input.variant), true, seeded.variants.find(variant => variant.id === row.key)?.desi),
      defaultListPrice: row.listPrice,
      defaultSalePrice: row.salePrice,
      // The version the row was loaded with: a row changed meanwhile elsewhere refuses the save (409) instead of
      // being overwritten with what this form still shows.
      loadedVersion: row.loadedVersion ?? null,
      sortOrder: rows.findIndex(candidate => candidate.key === row.key),
      options: row.options,
      attributes: variantAttributes(row, input.variant)
    }))
  }
}
export type ProductUpdatePayload = ReturnType<typeof buildUpdatePayload>

export const STALE_PRODUCT_MESSAGE = 'Ürün siz düzenlerken panelde başka bir işlemle güncellendi, bu yüzden değişiklikleriniz kaydedilmedi. Sayfayı yenileyip tekrar deneyin.'
export const CHANGED_DURING_RETRY_MESSAGE = 'Ürün kaydedilirken yeniden değişti. Güncel veriyi korumak için sayfayı yenileyip tekrar deneyin.'

// PATCH at the seeded version (If-Match). A 412 reads the product again: when only its version moved (nothing the form
// shows), the write is sent once more at the latest version; else the save stops and nothing is overwritten.
export async function saveProductUpdate(seeded: Product, payload: ProductUpdatePayload): Promise<{ product: Product; versionRetried: boolean }> {
  const updateAtVersion = (version: number) => hubApi<Product>(`/products/${seeded.id}`, {
    method: 'PATCH',
    headers: { 'If-Match': `"v${version}"` },
    body: JSON.stringify(payload)
  })
  try {
    return { product: await updateAtVersion(seeded.version), versionRetried: false }
  } catch (reason) {
    if (!(reason instanceof ApiRequestError) || reason.status !== 412) throw reason
    const latest = await hubApi<Product>(`/products/${seeded.id}`)
    if (!sameProductSnapshotIgnoringVersion(seeded, latest)) throw new Error(STALE_PRODUCT_MESSAGE)
    try {
      return { product: await updateAtVersion(latest.version), versionRetried: true }
    } catch (retryReason) {
      if (retryReason instanceof ApiRequestError && retryReason.status === 412) throw new Error(CHANGED_DURING_RETRY_MESSAGE)
      throw retryReason
    }
  }
}

// POST /products under the form's one create key: a resend after a lost answer is replayed, not created twice.
export function saveProductCreate(createKey: Parameters<typeof sendWithVersionedIdempotencyKey>[0], payload: ProductCreatePayload) {
  return sendWithVersionedIdempotencyKey(createKey, key => hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify(payload) }))
}
