import { ApiRequestError, hubApi } from '../../../shared/api'
import { cleanOptionValue, isColorOptionName, isVariantOptionName, isWebColorOptionName, normalizeVariantOptionName, variantColorKey, variantOptionEntries, type Attribute, type MarketplaceConnection, type Product, type Variant } from '../shared'
import { classifyPublicationAttributeIssues, type PublicationAttributeIssue, type PublicationAttributeIssues, type PublicationAttributeSelection, type PublicationMappingReference, type PublicationValueReferenceSet } from '../publication-attribute-readiness'
import { PublicationReadinessSourceError, publicationReadinessFailureDetail, readPublicationReadinessSource } from '../publication-readiness-error'
import { matchingVariantOptionValues, normalizeVariantOptionValue, parseVariantOptionSignature } from '../variant-option-matching'

// The publish step's readiness check, in one place: the editor's cards run it on the form, the product list's bulk
// publish runs it on each selected record (publicationReadinessDraftFromProduct fills the same input the editor's form
// holds right after it opened that record), so both screens list the same gaps for the same product.

export type CategoryRequirement = { attributeId: string; isRequired: boolean; allowsCustomValue: boolean; displayOrder: number; role: 'ATTRIBUTE' | 'OPTION'; isWebColor?: boolean; attribute: Attribute }

export function isOptionAttribute(attribute: Pick<Attribute, 'code' | 'name' | 'roles'>) {
  const normalizedName = normalizeVariantOptionName(attribute.name)
  return attribute.code.trim().toLocaleLowerCase('tr-TR').startsWith('option-')
    || attribute.roles?.some(role => role.toLocaleUpperCase('tr-TR') === 'OPTION') === true
    || isVariantOptionName(normalizedName)
}

export function isOptionRequirement(requirement: CategoryRequirement) {
  return requirement.role === 'OPTION' || isOptionAttribute(requirement.attribute)
}

export function optionSignatureFromOptions(options: Record<string, string>) {
  return Object.entries(options)
    .filter(([, value]) => value.trim())
    .map(([name, value]) => `${name}:${cleanOptionValue(value)}`)
    .join('_')
}

// The parts of a variant row the checks read.
export type ReadinessRow = { optionSignature: string; options: Record<string, string>; attributeValueIds: Record<string, string>; barcode: string }

export function rowOptionValue(row: Pick<ReadinessRow, 'options' | 'optionSignature'>, group: { name: string }) {
  const groupName = normalizeVariantOptionName(group.name)
  const optionRank = (name: string) => {
    const normalized = normalizeVariantOptionName(name)
    if (normalized === groupName) return 3
    if (!isColorOptionName(name) || !isColorOptionName(group.name)) return -1
    return isWebColorOptionName(name) ? 1 : 2
  }
  const options = [
    ...Object.entries(row.options).map(([name, value]) => ({ name, value })),
    ...parseVariantOptionSignature(row.optionSignature)
  ]
  return options
    .filter(option => option.value.trim())
    .sort((left, right) => optionRank(right.name) - optionRank(left.name))
    .find(option => optionRank(option.name) >= 0)?.value ?? ''
}

export function orderMediaUrlsByVariants(variants: Variant[], productMediaUrls: string[], primaryImageUrl: string | null, hasCustomMediaOrder = false) {
  if (hasCustomMediaOrder) return [...productMediaUrls]
  const ordered: string[] = []
  const seen = new Set<string>()
  const seenColors = new Set<string>()
  const add = (url: string | null | undefined) => {
    const normalized = url?.trim()
    if (!normalized) return
    const key = normalized.toLocaleLowerCase('tr-TR')
    if (seen.has(key)) return
    seen.add(key)
    ordered.push(normalized)
  }

  const colorVariants = variants.some(variant => variantColorKey(variant) !== null)
  if (colorVariants) {
    for (const variant of variants) {
      const color = variantColorKey(variant)
      const group = color ?? `variant:${variant.id}`
      if (seenColors.has(group)) continue
      seenColors.add(group)
      for (const url of variant.mediaUrls ?? []) add(url)
    }
    if (!ordered.length) add(productMediaUrls[0])
  } else {
    for (const variant of variants) for (const url of variant.mediaUrls ?? []) add(url)
    for (const url of productMediaUrls) add(url)
  }
  add(primaryImageUrl)
  return ordered
}

// The category's requirements as the editor groups them.
export function publicationRequirementSets(allRequirements: CategoryRequirement[]) {
  // A category requirement is usable in the product editor when its mapped local attribute has active values or the
  // marketplace accepts a custom value.
  const mappedRequirements = allRequirements.filter(item => item.attribute.values.length > 0 || item.allowsCustomValue)
  const colorOptionRequirement = mappedRequirements.find(item => !isWebColorOptionName(item.attribute.name) && isColorOptionName(item.attribute.name) && isOptionRequirement(item))
  // Web Color is backed by the local Renk option: the explicit category mapping marker first, else Renk.
  const webColorRequirement = mappedRequirements.find(item => (item.isWebColor === true || isWebColorOptionName(item.attribute.name)) && item.attribute.values.length > 0) ?? colorOptionRequirement
  const optionRequirements = mappedRequirements.filter(item => !isWebColorOptionName(item.attribute.name) && isOptionRequirement(item)).slice(0, 2)
  return { mappedRequirements, colorOptionRequirement, webColorRequirement, optionRequirements }
}

// What the checks read of the editor's form.
export type PublicationReadinessDraft = {
  title: string
  description: string
  brandId: string
  modelCode: string
  barcode: string
  categoryId: string
  automaticBarcodeGeneration: boolean
  mediaCount: number
  variantAttributeIds: string[]
  variantRows: ReadinessRow[]
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  webColorAutoEnabled: boolean
  manualWebColorValueId: string
}
// The category requirements query as the editor sees it.
export type PublicationRequirementsState = { items: CategoryRequirement[]; isLoading: boolean; isError: boolean; isSuccess: boolean }
export type PublicationProductCheck = { title: string; detail: string; ok: boolean; shopifyOk?: boolean; shopifyDetail?: string }

export function sortRequirements(requirements: CategoryRequirement[] | undefined) {
  return (requirements ?? []).slice().sort((a, b) => a.displayOrder - b.displayOrder)
}

export function publicationProductChecks(draft: PublicationReadinessDraft, requirements: PublicationRequirementsState): PublicationProductCheck[] {
  const allRequirements = requirements.items
  const { mappedRequirements, colorOptionRequirement, webColorRequirement, optionRequirements } = publicationRequirementSets(allRequirements)
  const { variantRows, variantAttributeIds, attributeSelections, attributeTextValues } = draft
  const hasBarcodeData = variantRows.length ? variantRows.every(row => row.barcode.trim()) : draft.automaticBarcodeGeneration ? Boolean(draft.modelCode.trim()) : Boolean(draft.barcode.trim())
  const hasBasicProductData = Boolean(draft.title.trim() && draft.description.trim() && draft.brandId && draft.modelCode.trim() && hasBarcodeData)
  const mediaCount = draft.mediaCount
  const hasProductMedia = mediaCount > 0
  const hasVariantData = variantAttributeIds.length === 0 || variantRows.length > 0
  const rowHasRequirementValue = (row: ReadinessRow, requirement: CategoryRequirement) => Boolean(row.attributeValueIds[requirement.attributeId]) || Boolean(rowOptionValue(row, { name: requirement.attribute.name }).trim())
  const categoryValidationIssues = !draft.categoryId
    ? ['Kategori seçilmedi.']
    : requirements.isLoading
      ? ['Kategori özellikleri kontrol ediliyor.']
      : requirements.isError
        ? ['Kategori özellikleri alınamadı.']
        : allRequirements.filter(requirement => !isOptionRequirement(requirement) && requirement.isRequired && !mappedRequirements.some(item => item.attributeId === requirement.attributeId)).map(requirement => `${requirement.attribute.name} kategori özelliği eşleştirilmemiş.`).concat(mappedRequirements.filter(requirement => !isOptionRequirement(requirement) && requirement.isRequired && requirement.attributeId !== webColorRequirement?.attributeId).flatMap(requirement => {
          if (variantAttributeIds.includes(requirement.attributeId)) return variantRows.length && variantRows.every(row => rowHasRequirementValue(row, requirement)) ? [] : [`${requirement.attribute.name} tüm varyantlarda seçilmelidir.`]
          return (attributeSelections[requirement.attributeId]?.length ?? 0) || Boolean((attributeTextValues[requirement.attributeId] ?? '').trim()) ? [] : [`${requirement.attribute.name} zorunludur.`]
        }))
  const optionValidationIssues = !draft.categoryId || !requirements.isSuccess ? [] : allRequirements.filter(requirement => isOptionRequirement(requirement) && !isWebColorOptionName(requirement.attribute.name) && !mappedRequirements.some(item => item.attributeId === requirement.attributeId)).map(requirement => `${requirement.attribute.name} seçenek eşleştirmesi eksik.`).concat(optionRequirements.flatMap(requirement => {
    const selected = attributeSelections[requirement.attributeId]?.length ?? 0
    const represented = variantRows.some(row => rowHasRequirementValue(row, requirement))
    if (!selected && !represented) return [`${requirement.attribute.name} için seçenek değeri seçilmemiş.`]
    if (variantAttributeIds.includes(requirement.attributeId) && (!variantRows.length || variantRows.some(row => !rowHasRequirementValue(row, requirement)))) return [`${requirement.attribute.name} seçenekleri varyant satırlarına aktarılmamış.`]
    return []
  }))
  const webColorValidationIssues = !draft.categoryId || !requirements.isSuccess ? [] : !webColorRequirement
    ? ['Web Color için kategori özelliği eşleştirilmemiş.']
    : draft.webColorAutoEnabled
      ? (!colorOptionRequirement || (!variantAttributeIds.includes(colorOptionRequirement.attributeId) && !(attributeSelections[colorOptionRequirement.attributeId]?.length))) ? ['Web Color otomatik aktarımı için Renk seçeneğini eşitleyip değer seçin.'] : []
      : !draft.manualWebColorValueId ? ['Manuel Web Color için katalog rengi seçin.'] : webColorRequirement.attribute.values.some(value => value.id === draft.manualWebColorValueId) ? [] : ['Manuel Web Color değeri geçerli değil.']
  const catalogValidationIssues = [...categoryValidationIssues, ...optionValidationIssues, ...webColorValidationIssues]
  const catalogValidationDetail = catalogValidationIssues.length ? catalogValidationIssues.join(' ') : 'Kategori özellikleri, seçenekler ve Web Color yayınlamaya hazır.'
  const basicProductMissingParts = [
    !draft.title.trim() && 'ürün adı',
    !draft.description.trim() && 'açıklama',
    !draft.brandId && 'marka',
    !draft.modelCode.trim() && 'model kodu',
    !hasBarcodeData && 'barkod'
  ].filter((part): part is string => Boolean(part))
  return [
    { title: 'Temel Ürün Verileri', detail: hasBasicProductData ? 'İsim, açıklama, marka, model ve barkod bilgileri eksiksiz.' : `Eksik alanlar: ${basicProductMissingParts.join(', ')}.`, ok: hasBasicProductData, shopifyOk: Boolean(draft.title.trim() && draft.description.trim()), shopifyDetail: 'Shopify ürünü için ad ve açıklama gereklidir.' },
    { title: 'Görsel Kalitesi', detail: hasProductMedia ? `${mediaCount} adet ürün görseli eklendi.${mediaCount > 8 ? ' Trendyol için sıralamadaki ilk 8 görsel kullanılır.' : ''}` : 'En az bir yüksek çözünürlüklü görsel ekleyin.', ok: hasProductMedia, shopifyOk: hasProductMedia, shopifyDetail: 'Shopify yayını için en az bir geçerli HTTPS görseli gerekir.' },
    { title: 'Varyant Bilgileri', detail: hasVariantData ? 'Varyant yapısı yayınlanmaya hazır.' : 'Seçilen seçenekler için varyant satırlarını oluşturun.', ok: hasVariantData, shopifyOk: hasVariantData, shopifyDetail: 'Shopify yayını için en az bir stok kodlu varyant oluşturun.' },
    { title: 'Kategori ve Web Color', detail: catalogValidationDetail, ok: catalogValidationIssues.length === 0, shopifyOk: true, shopifyDetail: 'Shopify yayını için pazaryeri kategori eşlemesi veya Web Color gerekmez.' }
  ]
}

export function publicationAttributeSelectionsFor(draft: PublicationReadinessDraft, allRequirements: CategoryRequirement[]): PublicationAttributeSelection[] {
  const { webColorRequirement } = publicationRequirementSets(allRequirements)
  return allRequirements.map(requirement => {
    const valueIds = new Set(draft.attributeSelections[requirement.attributeId] ?? [])
    let hasCustomValue = Boolean((draft.attributeTextValues[requirement.attributeId] ?? '').trim())
    for (const row of draft.variantRows) {
      const optionValue = rowOptionValue(row, { name: requirement.attribute.name }).trim()
      const matchedValue = optionValue
        ? requirement.attribute.values.find(value => normalizeVariantOptionValue(value.value) === normalizeVariantOptionValue(optionValue))
        : undefined
      const rowValueId = row.attributeValueIds[requirement.attributeId] ?? matchedValue?.id
      if (rowValueId) valueIds.add(rowValueId)
      else if (optionValue && isOptionRequirement(requirement)) hasCustomValue = true
    }
    const usesManualWebColor = requirement.attributeId === webColorRequirement?.attributeId && !draft.webColorAutoEnabled
    if (usesManualWebColor) {
      valueIds.clear()
      if (draft.manualWebColorValueId) valueIds.add(draft.manualWebColorValueId)
      hasCustomValue = false
    }
    return {
      attributeId: requirement.attributeId,
      name: requirement.attribute.name,
      isRequired: requirement.isRequired,
      values: [...valueIds].map(id => ({ id, label: requirement.attribute.values.find(value => value.id === id)?.value ?? '' })),
      hasCustomValue
    }
  })
}

// The editor's form right after it opened a saved record and its category requirements came in: the seed, the inferred
// option selections and the saved Web Color choice (ProductEditorPages seedForm and the two effects after it).
export function publicationReadinessDraftFromProduct(product: Product, allRequirements: CategoryRequirement[]): PublicationReadinessDraft {
  const primary = product.variants[0]
  const { webColorRequirement, optionRequirements } = publicationRequirementSets(allRequirements)
  const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
  for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
  let variantRows: ReadinessRow[] = product.variants.map(variant => {
    const options = Object.fromEntries(variantOptionEntries(variant).map(option => [option.name, option.value]))
    const attributeValueIds = Object.fromEntries((variant.attributes ?? []).flatMap(attribute => attribute.valueId ? [[attribute.attributeId, attribute.valueId] as const] : []))
    return { optionSignature: optionSignatureFromOptions(options) || (variant.optionSignature && variant.optionSignature !== '-' ? variant.optionSignature : 'Tek Ürün'), options, attributeValueIds, barcode: variant.barcode ?? '' }
  })
  let variantAttributeIds: string[] = []
  if (allRequirements.length) {
    const inferred: Record<string, string[]> = {}
    for (const requirement of optionRequirements) {
      const valuesInVariants = product.variants.flatMap(variant => variantOptionEntries(variant)
        .filter(option => normalizeVariantOptionName(option.name) === normalizeVariantOptionName(requirement.attribute.name))
        .map(option => option.value))
      const ids = matchingVariantOptionValues(requirement.attribute.values, valuesInVariants).map(value => value.id)
      if (ids.length) inferred[requirement.attributeId] = ids
    }
    Object.assign(selected, inferred)
    variantAttributeIds = Object.keys(inferred)
    variantRows = product.variants.map((source, index) => {
      const options = Object.fromEntries(variantOptionEntries(source).map(option => [option.name, option.value]))
      const attributeValueIds = Object.fromEntries(optionRequirements.flatMap(requirement => {
        const option = variantOptionEntries(source).find(item => normalizeVariantOptionName(item.name) === normalizeVariantOptionName(requirement.attribute.name))
        const value = matchingVariantOptionValues(requirement.attribute.values, option ? [option.value] : [])[0]
        return value ? [[requirement.attributeId, value.id]] : []
      }))
      return { ...variantRows[index], optionSignature: optionSignatureFromOptions(options) || (source.optionSignature && source.optionSignature !== '-' ? source.optionSignature : variantRows[index].optionSignature), options, attributeValueIds }
    })
  }
  const savedManualColor = webColorRequirement ? product.trendyolWebColorValueId ?? '' : ''
  return {
    title: product.title,
    description: product.description ?? '',
    brandId: product.brandId ?? '',
    modelCode: primary?.modelCode ?? product.modelCode ?? '',
    barcode: primary?.barcode ?? '',
    categoryId: product.categoryId ?? '',
    automaticBarcodeGeneration: false,
    // The form keeps the URLs as one text and splits it the same way.
    mediaCount: orderMediaUrlsByVariants(product.variants, product.mediaUrls ?? [], product.primaryImageUrl, product.hasCustomMediaOrder).join('\n').split(/\r?\n|[;|]/).map(item => item.trim()).filter(Boolean).length,
    variantAttributeIds,
    variantRows,
    attributeSelections: selected,
    attributeTextValues: typed,
    webColorAutoEnabled: !savedManualColor,
    manualWebColorValueId: savedManualColor
  }
}

type ReadinessConnection = Pick<MarketplaceConnection, 'id' | 'platformCode'>

export function publicationReadinessQueryKey(connectionId: string, categoryId: string, selectedAttributes: PublicationAttributeSelection[]) {
  return ['publication-attribute-readiness', connectionId, categoryId, selectedAttributes] as const
}

// The card's mapping check: category mapping, the marketplace's required attributes and the value mappings of what the
// product uses. `cardName` names the marketplace in the messages.
// `categoryName` names the panel category in the "not mapped" gap.
export async function loadPublicationAttributeIssues(connection: ReadinessConnection, cardName: string, categoryId: string, selectedAttributes: PublicationAttributeSelection[], categoryName = ''): Promise<PublicationAttributeIssues> {
  const connectionId = encodeURIComponent(connection.id)
  const categoryMapping = await readPublicationReadinessSource('Kategori eşlemesi', () => hubApi<{ externalId: string; snapshotId: string; status: string } | null>(`/mappings/categories/${categoryId}?connectionId=${connectionId}`))
  if (!categoryMapping || categoryMapping.status !== 'VERIFIED') {
    return { requiredIssues: [{ attribute: 'Kategori eşlemesi', detail: `${categoryName ? `${categoryName} kategorisinin` : 'Ürün kategorisinin'} ${cardName} karşılığı seçilmemiş; kategoriyi eşleyin.` }], optionalWarnings: [], categoryMapping: 'missing' }
  }

  const categoryReferences = await readPublicationReadinessSource(`${cardName} kategori listesi`, () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; name: string; isActive: boolean; isLeaf: boolean }> }>(`/reference-data/categories?connectionId=${connectionId}`))
  const mappedCategory = categoryReferences.items.find(item => item.externalId === categoryMapping.externalId && item.isActive && item.isLeaf)
  if (categoryMapping.snapshotId !== categoryReferences.snapshotId || !mappedCategory) {
    return { requiredIssues: [{ attribute: 'Kategori eşlemesi', detail: `${cardName} kategori eşlemesi güncel değil; kategori eşlemesini yenileyin.` }], optionalWarnings: [], categoryMapping: 'stale' }
  }
  if (connection.platformCode.trim().toUpperCase() === 'PTTAVM') return { requiredIssues: [], optionalWarnings: [], categoryScope: categoryMapping.externalId }

  const categoryScope = categoryMapping.externalId
  const [attributeReferences, attributeMappings] = await Promise.all([
    readPublicationReadinessSource(`${cardName} kategori özellik listesi`, () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; name: string; isActive: boolean; isRequired?: boolean | null; allowsCustomValue?: boolean | null }> }>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${connectionId}`)),
    readPublicationReadinessSource('Yerel kategori özellik eşlemeleri', () => hubApi<PublicationMappingReference[]>(`/mappings/attributes?connectionId=${connectionId}&scopeExternalId=${encodeURIComponent(categoryScope)}`))
  ])
  const selectedIds = new Set(selectedAttributes.map(item => item.attributeId))
  const currentMappings = attributeMappings.filter(mapping => mapping.status === 'VERIFIED' && mapping.snapshotId === attributeReferences.snapshotId)
  const selectedExternalIds = [...new Set(currentMappings.filter(mapping => selectedIds.has(mapping.localId)).map(mapping => mapping.externalId))]
  const valueEntries = await Promise.all(selectedExternalIds.map(async externalAttributeId => {
    const valueScope = `${categoryScope}/${externalAttributeId}`
    const selectedAttribute = selectedAttributes.find(item => item.attributeId === currentMappings.find(mapping => mapping.externalId === externalAttributeId)?.localId)
    const attributeLabel = selectedAttribute?.name ?? externalAttributeId
    const mappings = await readPublicationReadinessSource(`“${attributeLabel}” yerel değer eşlemeleri`, () => hubApi<PublicationMappingReference[]>(`/mappings/attribute-values?connectionId=${connectionId}&scopeExternalId=${encodeURIComponent(valueScope)}`))
    let references: { snapshotId: string; items: Array<{ externalId: string; isActive: boolean }> }
    try {
      references = await readPublicationReadinessSource(`${cardName} “${attributeLabel}” özellik değer listesi`, () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; isActive: boolean }> }>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${connectionId}`))
    } catch (reason) {
      if (!(reason instanceof ApiRequestError && reason.code === 'REFERENCE_SNAPSHOT_UNAVAILABLE') && !(reason instanceof PublicationReadinessSourceError && reason.apiCode === 'REFERENCE_SNAPSHOT_UNAVAILABLE')) throw reason
      references = { snapshotId: '', items: [] }
    }
    return [externalAttributeId, { snapshotId: references.snapshotId, items: references.items, mappings }] as const
  }))
  const valueReferencesByAttribute: Record<string, PublicationValueReferenceSet> = Object.fromEntries(valueEntries)
  // Connection defaults (e.g. Menşei = TR) cover required attributes the product leaves empty.
  const attributeDefaults = await hubApi<Array<{ externalAttributeId: string }>>(`/connections/${connectionId}/attribute-defaults`).catch(() => [])
  // n11 fills its brand attribute (id 1) from the product brand, so it needs no mapping.
  const brandAttributeIds = connection.platformCode.trim().toUpperCase() === 'N11' ? ['1'] : []
  const result = classifyPublicationAttributeIssues({
    defaultedAttributeIds: [...attributeDefaults.map(item => item.externalAttributeId), ...brandAttributeIds],
    // n11 sends an unmapped value of a free-text field (Boy: Regular) as its own text (N11ProductPublicationComposer).
    sendsUnmappedValuesAsFreeText: connection.platformCode.trim().toUpperCase() === 'N11',
    selectedAttributes,
    remoteAttributes: attributeReferences.items,
    attributeSnapshotId: attributeReferences.snapshotId,
    attributeMappings,
    platformName: cardName,
    valueReferencesByAttribute
  })
  return { ...result, categoryScope }
}

// The card's product checks a marketplace needs (Shopify takes fewer).
export function missingPublicationChecks(productChecks: PublicationProductCheck[], isShopify: boolean) {
  return productChecks
    .filter(check => !(isShopify ? check.shopifyOk ?? check.ok : check.ok))
    .map(check => ({ ...check, detail: isShopify ? check.shopifyDetail ?? check.detail : check.detail }))
}

// What keeps a card from publishing, in the card's order: the product checks, the connection's own gaps (the editor's
// Shopify permission checks), the mapping check's required gaps, or that the check itself failed.
export function publicationBlockingIssues(missingChecks: Array<{ title: string; detail: string }>, connectionIssues: Array<{ attribute: string; detail: string }>, requiredMappingIssues: PublicationAttributeIssue[], mappingError: unknown, cardName: string): Array<{ attribute: string; detail: string }> {
  return [
    ...missingChecks.map(check => ({ attribute: check.title, detail: check.detail })),
    ...connectionIssues,
    ...requiredMappingIssues,
    ...(mappingError ? [{ attribute: `${cardName} özellik kontrolü`, detail: publicationReadinessFailureDetail(cardName, mappingError) }] : [])
  ]
}
