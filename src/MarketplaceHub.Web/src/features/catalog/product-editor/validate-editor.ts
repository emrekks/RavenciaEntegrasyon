// The product editor's checks in one place (WEB-SAYFALAR-10): the required fields the page frames red as the form is
// filled (live), what Taslak Olarak Kaydet refuses (draft), and what Kaydet / Ürünü Oluştur / Yayınla refuse (save).
// The live checks and the save both read this; the required fields themselves are one table (required-fields.ts), in
// the server's order (ProductRequiredFields).
import { isStoredProductMediaUrl, publicProductMediaUrls } from '../product-media-editor'
import { autoWebColorIssues, isOptionRequirement, type CategoryRequirement } from './publication-readiness'
import { isProductDeliveryDays } from './product-delivery-days'
import { missingRequiredEditorFields, type RequiredEditorField } from './required-fields'
import { parseProductDesi, productDesiTooLarge, productDesiTooLargeMessage } from '../product-desi'
import { productMediaUrlIssue } from '../product-media-url'
import { productCopyIdentifierConflicts } from '../product-copy-identifiers'
import { draftBlockingIssues, unreadableNumberIssues } from '../product-draft'
import { editorVatRateIssue } from '../product-editor-state'
import { MAX_VARIANTS } from './editor-helpers'
import { channelFieldKey, readMoneyField } from './seeded-fields'
import { editorCount } from './editor-number'
import type { ChannelPricingDraft } from './marketplace-channel-pricing'
import type { VariantDraft } from './editor-types'
import type { EditorForm } from './editor-form'

export type EditorValidationInput = {
  form: EditorForm
  automaticBarcodes: boolean
  // A card with more than one variant row: each row holds its own codes.
  hasVariants: boolean
}

export type EditorSaveValidationInput = EditorValidationInput & {
  rows: VariantDraft[]
  editing: boolean
  // The desi the edit form opened with (0: none of the card's variants had one).
  initialDesi: number
  cardPriceIssue: string | null
  variantAttributeIds: string[]
  requirements: CategoryRequirement[]
  colorOptionRequirement: CategoryRequirement | undefined
  webColorRequirement: CategoryRequirement | undefined
  webColor: { autoEnabled: boolean; manualValueId: string }
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  mediaUrls: string[]
  mediaFileCount: number
  // The copy source's variants (a copy may not keep its codes), or null on a new card or an edit.
  copySourceVariants: ReadonlyArray<{ sku: string; barcode: string | null }> | null
  // The marketplaces picked for publishing, with their name and the card's price there.
  selectedChannels: Array<{ id: string; name: string; draft: ChannelPricingDraft }>
}

export type EditorValidationMode =
  | { kind: 'live' }
  | { kind: 'draft' }
  | { kind: 'save'; requireCompleteCatalog: boolean; requirePublicationReadiness: boolean }

export type EditorValidation = {
  // The required fields left empty (prices of 0 included), in the page's and the server's order.
  missingRequired: RequiredEditorField[]
  // KDV Oranı: required on every card but a draft; checked apart from the eleven.
  vatRateIssue: string | undefined
  // What this save refuses, as sentences (the toast joins them): none for the live checks.
  issues: string[]
}

export function validateEditor(input: EditorValidationInput, mode: { kind: 'live' }): EditorValidation
export function validateEditor(input: EditorSaveValidationInput, mode: EditorValidationMode): EditorValidation
export function validateEditor(input: EditorValidationInput | EditorSaveValidationInput, mode: EditorValidationMode): EditorValidation {
  const missingRequired = missingRequiredEditorFields(input.form, { automaticBarcodes: input.automaticBarcodes, hasVariants: input.hasVariants })
  const vatRateIssue = editorVatRateIssue(input.form.vatRate)
  if (mode.kind === 'live') return { missingRequired, vatRateIssue, issues: [] }
  const full = input as EditorSaveValidationInput
  return { missingRequired, vatRateIssue, issues: mode.kind === 'draft' ? draftIssues(full) : saveIssues(full, mode.requireCompleteCatalog, mode.requirePublicationReadiness) }
}

const warrantyIssue = (form: EditorForm) => form.warrantyMonths.trim() && (!Number.isInteger(Number(form.warrantyMonths)) || Number(form.warrantyMonths) < 0 || Number(form.warrantyMonths) > 240) ? 'Garanti süresi 0-240 ay arasında tam sayı olmalıdır.' : null

const rowMediaUrls = (input: EditorSaveValidationInput) => [...input.mediaUrls, ...input.rows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4)))]

const invalidMediaUrlIssue = (urls: string[]) => {
  const invalid = publicProductMediaUrls(urls).map(productMediaUrlIssue).find((issue): issue is string => issue !== null)
  return invalid ? `Görsel bağlantısı geçersiz: ${invalid} Ürün kaydedilmeden önce düzeltin veya kaldırın.` : null
}

// A draft is stopped only by values the server refuses (clashing codes, malformed prices or image links).
function draftIssues(input: EditorSaveValidationInput) {
  const issues = draftBlockingIssues(input.rows, input.form)
  const media = invalidMediaUrlIssue(rowMediaUrls(input))
  if (media) issues.push(media)
  const warranty = warrantyIssue(input.form)
  if (warranty) issues.push(warranty)
  return issues
}

function saveIssues(input: EditorSaveValidationInput, requireCompleteCatalog: boolean, requirePublicationReadiness: boolean) {
  const { form, rows, variantAttributeIds, webColor, webColorRequirement, attributeSelections, attributeTextValues } = input
  const issues: string[] = []; const requirementList = input.requirements
  if (requireCompleteCatalog && (variantAttributeIds.length > 2 || variantAttributeIds.some(id => { const requirement = requirementList.find(item => item.attributeId === id); return !requirement || !isOptionRequirement(requirement) }))) issues.push('Varyant için en fazla 2 seçenek başlığı kullanılabilir.')
  if (requireCompleteCatalog && !webColor.autoEnabled && (!webColorRequirement || !webColor.manualValueId)) issues.push('Manuel Web Color aktarımı için gönderilecek panel renk değerini seçin.')
  if (!webColor.autoEnabled && webColorRequirement && webColor.manualValueId && !webColorRequirement.attribute.values.some(value => value.id === webColor.manualValueId)) issues.push('Manuel Web Color için seçilen değer geçerli değil.')
  // Automatic Web Color reads each row's own Renk, as the publish checks and the server do (publication-readiness).
  if (requireCompleteCatalog && webColor.autoEnabled && webColorRequirement) issues.push(...autoWebColorIssues({ variantRows: rows, variantAttributeIds, attributeSelections }, input.colorOptionRequirement))
  if (!form.title.trim()) issues.push('Ürün adı zorunludur.')
  if (input.cardPriceIssue) issues.push(input.cardPriceIssue)
  if (input.automaticBarcodes && !form.modelCode.trim()) issues.push('Otomatik barkod oluşturmak için model kodu zorunludur.')
  if (requireCompleteCatalog && !form.description.trim()) issues.push('Açıklama zorunludur.')
  if (requireCompleteCatalog) {
    for (const requirement of requirementList) {
      const selectedCount = attributeSelections[requirement.attributeId]?.length ?? 0
      if (!variantAttributeIds.includes(requirement.attributeId) && requirement.attribute.dataType === 'SINGLE_SELECT' && selectedCount > 1) issues.push(`${requirement.attribute.name} yalnız bir ürün değeri kabul eder.`)
      if (isOptionRequirement(requirement) || !requirement.isRequired) continue
      if (variantAttributeIds.includes(requirement.attributeId)) {
        if (rows.some(row => !row.attributeValueIds[requirement.attributeId])) issues.push(`${requirement.attribute.name} tüm varyantlarda seçilmelidir.`)
      } else if (!(attributeSelections[requirement.attributeId]?.length) && !(attributeTextValues[requirement.attributeId] ?? '').trim()) issues.push(`${requirement.attribute.name} zorunludur.`)
    }
  }
  if (rows.length > MAX_VARIANTS) issues.push(`En fazla ${MAX_VARIANTS} varyant oluşturulabilir.`)
  const publicationMediaRefs = rowMediaUrls(input)
  const media = invalidMediaUrlIssue(publicationMediaRefs)
  if (media) issues.push(media)
  const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')); if (skus.some(value => !value)) issues.push('Tüm varyantlarda stok kodu zorunludur.'); if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
  const signatures = rows.map(row => row.optionSignature); if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
  const barcodes = rows.map(row => row.barcode.trim()).filter(Boolean); if (new Set(barcodes.map(value => value.toLocaleUpperCase('tr-TR'))).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
  if (input.copySourceVariants) {
    const conflicts = productCopyIdentifierConflicts(input.copySourceVariants, rows)
    if (conflicts.barcodes) issues.push('Kaynak ürünün barkodları zaten kayıtlı. Yeni ürünü oluşturmak için çakışan barkodları değiştirin.')
    if (conflicts.skus) issues.push('Kaynak ürünün stok kodları zaten kayıtlı. Yeni ürünü oluşturmak için çakışan stok kodlarını değiştirin.')
  }
  issues.push(...unreadableNumberIssues(rows))
  if (!Number.isFinite(editorCount(form.safetyStock || 0)) || !Number.isFinite(editorCount(form.initialStock || 0))) issues.push('Stok ve güvenlik stoğu 1.250 gibi sıfır veya pozitif bir tam sayı olmalıdır.')
  if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
  // Owner, 10 Nov 2026 ("0 olmayacak onlarda"): a variant's list and sale price are above 0, like the card's.
  if (rows.some(row => !(row.salePrice > 0) || !(row.listPrice > 0))) issues.push('Her varyantta liste ve satış fiyatı 0\'dan büyük olmalı.')
  // A card none of whose variants has a desi opens with the field empty (no guessed 1) and may be saved so.
  if (!(input.editing && input.initialDesi === 0 && !form.desi.trim()) && (!form.desi.trim() || parseProductDesi(form.desi) <= 0)) issues.push('Desi sıfırdan büyük olmalıdır.')
  if (productDesiTooLarge(form.desi)) issues.push(productDesiTooLargeMessage)
  const warranty = warrantyIssue(form)
  if (warranty) issues.push(warranty)
  if (!isProductDeliveryDays(form.deliveryDays)) issues.push('Teslimat süresi 1-30 gün arasında tam sayı olmalıdır.')
  if (requireCompleteCatalog && requirePublicationReadiness && input.selectedChannels.length) {
    if (!form.brandId) issues.push('Trendyol yayını için marka zorunludur.'); if (!form.modelCode.trim() || form.modelCode.trim().length > 40) issues.push('Trendyol yayını için en fazla 40 karakterlik model kodu zorunludur.'); if (form.title.trim().length > 100) issues.push('Trendyol ürün başlığı en fazla 100 karakter olabilir.')
    const publicMediaUrls = publicProductMediaUrls(publicationMediaRefs)
    const hasLocalPublicationMedia = input.mediaFileCount > 0 || publicationMediaRefs.some(isStoredProductMediaUrl)
    if (!publicMediaUrls.length && !hasLocalPublicationMedia) issues.push('Trendyol yayını için en az bir ürün görseli ekleyin.')
    if (publicMediaUrls.some(url => !url.startsWith('https://'))) issues.push('Tüm görsel adresleri HTTPS olmalıdır.')
    if (rows.some(row => !row.barcode.trim() || !/^[a-zA-Z0-9._-]+$/.test(row.barcode.trim()))) issues.push('Trendyol yayını için her varyantta geçerli ve benzersiz barkod zorunludur.'); if (rows.some(row => row.salePrice <= 0)) issues.push('Trendyol yayını için satış fiyatı sıfırdan büyük olmalıdır.')
    for (const channel of input.selectedChannels) {
      const listPrice = readMoneyField(channelFieldKey(channel.id, 'listPrice'), channel.draft.listPrice); const salePrice = readMoneyField(channelFieldKey(channel.id, 'salePrice'), channel.draft.salePrice)
      if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || salePrice <= 0 || listPrice < salePrice) issues.push(`${channel.name} için liste fiyatı satış fiyatından küçük olamaz; satış fiyatı sıfırdan büyük olmalıdır.`)
    }
  }
  return issues
}

// What empty required fields do to a press (owner, 10–11 Nov 2026): Taslak Olarak Kaydet ignores them; Kaydet / Ürünü
// Oluştur on a new card or a draft keeps the card as a draft; every other save (a card on sale or closed, publishing, a
// platform update) stops until they are filled.
export function requiredFieldsOutcome(missing: readonly RequiredEditorField[], options: { explicitDraft: boolean; plainSave: boolean; draftAllowed: boolean }) {
  const counted = options.explicitDraft ? [] : [...missing]
  const autoDraft = counted.length > 0 && options.plainSave && options.draftAllowed
  return { missing: counted, autoDraft, stop: counted.length > 0 && !autoDraft }
}
