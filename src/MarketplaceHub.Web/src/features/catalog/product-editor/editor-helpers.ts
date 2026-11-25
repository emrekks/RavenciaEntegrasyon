// Pure helpers of the product editor: labels, sorting, the variant matrix and attribute payloads.
import { platformDisplayNames, knownPlatformName, platformDisplayOrder } from '../../../shared/platform-names'
import { type VariantBulkEditField } from '../variant-bulk-edit'
import { buildVariantGenerationDefaults } from '../variant-generation'
import { colourFirstOptions, displayVariantOptionEntriesFromSignature, joinVariantOptionLabels, normalizeVariantOptionValue, parseVariantOptionSignature, variantOptionSetKey } from '../variant-option-matching'
import { rowOptionValue, type CategoryRequirement } from './publication-readiness'
import { MarketplaceConnection, Variant, VariantPlatformStatus, cleanOptionValue, compareLabelsAlphabetically, compareVariantOptionSignatures, formatCatalogOptionLabel, normalizeVariantOptionName, productPublicationMode, variantColorKey } from '../shared'
import { VariantMediaGroup, VariantDraft, SavedListingProfile, ProductAttributePayload } from './editor-types'
import { attributeNumberHelp, parseEditorNumber } from './editor-number'

// The save's slot in the toast column: "Ürün Kaydediliyor" turns into its result in place instead of stacking.
export const productSaveToastKey = 'product-editor-save'
// The server keeps a replaced "Tek Ürün" variant that anything is tied to (owner, 6 Nov 2026).
export const PLACEHOLDER_KEPT_MESSAGE = '“Tek Ürün” varyantı silinmedi: pazaryeri bağlantısı ya da teklifi, siparişi veya stok hareketi bulunduğu için geçmişi korunarak üründe bırakıldı.'
// Channel prices are separate rows per variant and store; this many are written at once.
export const channelOfferWriteConcurrency = 6

// A variant's options as the panel writes them elsewhere ("Beden: L - Renk: Bej"), not its raw signature ("Beden:L_Renk:Bej").
export function pricingVariantLabel(signature: string | null | undefined) {
  const entries = colourFirstOptions(parseVariantOptionSignature(signature ?? ''))
  return entries.length ? joinVariantOptionLabels(entries.map(entry => `${entry.name}: ${entry.value}`)) : signature ?? ''
}

export function seedVariantMediaRefs(variants: Variant[]) {
  const mediaByColor = new Map<string, string[]>()
  for (const variant of variants) {
    const color = variantColorKey(variant)
    if (!color) continue
    const urls = mediaByColor.get(color) ?? []
    for (const url of variant.mediaUrls ?? []) if (url && !urls.some(item => item.localeCompare(url, undefined, { sensitivity: 'accent' }) === 0)) urls.push(url)
    mediaByColor.set(color, urls)
  }
  return variants.map(variant => {
    const color = variantColorKey(variant)
    const urls = color ? (mediaByColor.get(color) ?? variant.mediaUrls ?? []) : (variant.mediaUrls ?? [])
    return [...new Set(urls.filter(Boolean).map(url => `url|${url}`))]
  })
}
export function sortVariantsAlphabetically<T extends { optionSignature?: string | null; sku: string }>(variants: T[]) {
  return [...variants].sort((left, right) => {
    const leftLabel = (left.optionSignature || left.sku).trim()
    const rightLabel = (right.optionSignature || right.sku).trim()
    return compareVariantOptionSignatures(leftLabel, rightLabel)
      || compareLabelsAlphabetically(left.sku, right.sku)
  })
}
export const isProductPublicationChannel = (item: MarketplaceConnection) => productPublicationMode(item) !== null
export function isSizeOptionName(name: string) {
  return ['BEDEN', 'BEDENLER', 'SIZE', 'SIZES', 'BOYUT', 'BOYUTLAR', 'NUMARA', 'NUMARALAR', 'SHOESIZE', 'AYAKKABINUMARASI'].includes(normalizeVariantOptionName(name))
}
// A variant reads by its values only ("2XL - Mürdüm"; owner, 10 Nov 2026); `withNames` adds the option titles
// ("Beden: 2XL - Renk: Mürdüm") for the hover title.
export function displayVariantOptionSignature(signature: string, savedOptions: Record<string, string> = {}, withNames = false) {
  const options = displayVariantOptionEntriesFromSignature(signature, savedOptions)
  if (!options.length) return signature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' ? signature : '—'
  return joinVariantOptionLabels(options.map(({ name, value }) => {
    const shown = isSizeOptionName(name) ? value.toLocaleUpperCase('tr-TR') : formatCatalogOptionLabel(value)
    return withNames ? `${formatCatalogOptionLabel(name)}: ${shown}` : shown
  }))
}
// A saved row and a generated combination of the same options are one row (variantOptionSetKey), whatever their spelling.
export const variantSignatureKey = variantOptionSetKey
// On touch screens a window does not focus its first field by itself: that would open the keyboard before the person
// has chosen what to change.
export const touchScreen = typeof window !== 'undefined' && window.matchMedia('(pointer: coarse)').matches
// The element that scrolls the page around a node: its nearest scrolling ancestor, or the document.
export function scrollingParent(node: Element) {
  for (let current = node.parentElement; current; current = current.parentElement) {
    const overflow = getComputedStyle(current).overflowY
    if ((overflow === 'auto' || overflow === 'scroll') && current.scrollHeight > current.clientHeight) return current
  }
  return document.scrollingElement ?? document.documentElement
}
// The listing profile a publish writes: a saved profile as it is (switched on), a new one without overrides.
export function republishListingProfileBody(saved: SavedListingProfile | null | undefined) {
  return { titleOverride: saved?.titleOverride ?? null, descriptionOverride: saved?.descriptionOverride ?? null, externalCategoryId: saved?.externalCategoryId ?? null, externalBrandId: saved?.externalBrandId ?? null, deliveryTimeDays: saved?.deliveryTimeDays ?? null, enabled: true }
}
export const variantBulkEditLabels: Record<VariantBulkEditField, string> = { stock: 'Stok', salePrice: 'Fiyat', costPrice: 'Maliyet', listPrice: 'Liste Fiyatı' }
export function marketplacePlatformName(platform: VariantPlatformStatus) {
  const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
  return knownPlatformName(code) ?? platform.platform
}
export const pricingPlatformOrder: readonly string[] = platformDisplayOrder
export const pricingPlatformRank = (platformCode: string) => { const index = pricingPlatformOrder.indexOf(platformCode.trim().toUpperCase()); return index < 0 ? pricingPlatformOrder.length : index }
// API, inventory and publication safeguards retain the actual 1000-line limit.
// The product workspace intentionally does not display an arbitrary UI quota.
export const MAX_VARIANTS = 1000
export function buildVariantMatrix(requirements: CategoryRequirement[], variantAttributeIds: string[], selectedValueIds: Record<string, string[]>, fallbackListPrice: number, fallbackSalePrice: number, fallbackCostPrice: number | null, automaticBarcodes = false, modelCode = '') {
  const axes = variantAttributeIds.map(attributeId => {
    const requirement = requirements.find(item => item.attributeId === attributeId)
    const selected = new Set(selectedValueIds[attributeId] ?? [])
    return { requirement, values: requirement?.attribute.values.filter(value => selected.has(value.id)) ?? [] }
  }).filter(axis => axis.requirement && axis.values.length)
  if (!axes.length) return [] as VariantDraft[]
  const count = axes.reduce((total, axis) => total * axis.values.length, 1)
  if (count > MAX_VARIANTS) throw new Error(`En fazla ${MAX_VARIANTS} varyant oluşturabilirsiniz. Seçili kombinasyon sayısı: ${count}.`)
  const combinations = axes.reduce<Array<{ options: Record<string, string>; attributeValueIds: Record<string, string> }>>((carry, axis) => {
    if (!carry.length) return axis.values.map(value => ({ options: { [axis.requirement!.attribute.name]: value.value }, attributeValueIds: { [axis.requirement!.attributeId]: value.id } }))
    return carry.flatMap(entry => axis.values.map(value => ({ options: { ...entry.options, [axis.requirement!.attribute.name]: value.value }, attributeValueIds: { ...entry.attributeValueIds, [axis.requirement!.attributeId]: value.id } })))
  }, [])
  return combinations.map((entry, index) => ({
    key: crypto.randomUUID(),
    optionSignature: Object.entries(entry.options).map(([name, value]) => `${name}:${cleanOptionValue(value)}`).join('_'),
    options: entry.options,
    attributeValueIds: entry.attributeValueIds,
    // A generated row starts with no stock (buildVariantGenerationDefaults), never the card's Stok field.
    ...buildVariantGenerationDefaults({ modelCode, sequence: index + 1, automaticBarcodes, fallbackSalePrice, fallbackListPrice }),
    costPrice: fallbackCostPrice,
    mediaRefs: []
  }))
}
export function productAttributePayload(requirement: CategoryRequirement, selectedIds: string[], typedValue: string, sortOrder: number): ProductAttributePayload[] {
  if (selectedIds.length) return selectedIds.map((valueId, index) => ({ attributeId: requirement.attributeId, valueId, textValue: null, numberValue: null, booleanValue: null, sortOrder: sortOrder * 100 + index }))
  const typed = typedValue.trim()
  if (!typed) return []
  if (requirement.attribute.dataType === 'NUMBER') {
    // D-1: a Turkish text box read by the measure rule ("12,5" is 12.5, "1.250" is 1250, "12.5" could mean two things).
    const value = parseEditorNumber(typed)
    if (!Number.isFinite(value)) throw new Error(`${requirement.attribute.name} sayısal olmalıdır; ${attributeNumberHelp}`)
    return [{ attributeId: requirement.attributeId, valueId: null, textValue: null, numberValue: value, booleanValue: null, sortOrder }]
  }
  if (requirement.attribute.dataType === 'BOOLEAN') {
    const normalized = typed.toLocaleLowerCase('tr-TR')
    if (!['true', 'false', 'evet', 'hayır', 'hayir', '1', '0'].includes(normalized)) throw new Error(`${requirement.attribute.name} için evet veya hayır seçin.`)
    return [{ attributeId: requirement.attributeId, valueId: null, textValue: null, numberValue: null, booleanValue: ['true', 'evet', '1'].includes(normalized), sortOrder }]
  }
  return [{ attributeId: requirement.attributeId, valueId: null, textValue: typed, numberValue: null, booleanValue: null, sortOrder }]
}
// Platform codes are stored upper-case; show the brand names the way they are written.
export function publishPlatformName(code: string) {
  const names: Record<string, string> = platformDisplayNames
  const normalized = code.trim().toLocaleUpperCase('tr-TR')
  return names[normalized] ?? code.charAt(0).toLocaleUpperCase('tr-TR') + code.slice(1).toLocaleLowerCase('tr-TR')
}
// Variant option helpers: pure functions of a draft row, shared by the variant media and filter memos.
export function rowMatchesVariantMediaValue(row: VariantDraft, group: VariantMediaGroup, value: { id: string; value: string }) {
  return Boolean((group.attributeId && row.attributeValueIds[group.attributeId] === value.id) || normalizeVariantOptionValue(rowOptionValue(row, group)) === normalizeVariantOptionValue(value.value))
}
