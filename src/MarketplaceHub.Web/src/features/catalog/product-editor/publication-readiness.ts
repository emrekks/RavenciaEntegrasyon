import type { QueryClient } from '@tanstack/react-query'
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
  // Variant rows without a picture of their own (the editor's rows, or a saved record's variants); undefined when unknown.
  variantsWithoutMedia?: number
  variantAttributeIds: string[]
  variantRows: ReadinessRow[]
  attributeSelections: Record<string, string[]>
  attributeTextValues: Record<string, string>
  webColorAutoEnabled: boolean
  manualWebColorValueId: string
}
// The category requirements query as the editor sees it.
export type PublicationRequirementsState = { items: CategoryRequirement[]; isLoading: boolean; isError: boolean; isSuccess: boolean }
// `platforms`: the marketplaces the check is about (Web Color: Trendyol only); without it every marketplace reads it.
export type PublicationProductCheck = { title: string; detail: string; ok: boolean; shopifyOk?: boolean; shopifyDetail?: string; platforms?: readonly string[] }

export function sortRequirements(requirements: CategoryRequirement[] | undefined) {
  return (requirements ?? []).slice().sort((a, b) => a.displayOrder - b.displayOrder)
}

// Automatic Web Color, read the way the server builds it (ProductPublicationComposer): each variant's own Renk option
// names a catalog colour, and that colour's Web Color mapping goes with the variant. So the check passes when every
// variant row carries a Renk the catalog knows, whether or not the form's option group was synced (live 2026-11-02:
// MZ068BT showed "Web Color" as missing while all seven colours had verified mappings), and it names what the server
// would refuse: rows without a Renk, and Renk values with no catalog colour (sent without a Web Color, the server stops
// on REQUIRED_ATTRIBUTE_MISSING).
function autoWebColorIssues(draft: PublicationReadinessDraft, colorOptionRequirement: CategoryRequirement | undefined): string[] {
  const syncHint = 'Web Color otomatik aktarımı için Renk seçeneğini eşitleyip değer seçin.'
  if (!colorOptionRequirement) return [syncHint]
  const colorId = colorOptionRequirement.attributeId
  if (!draft.variantRows.length)
    return draft.variantAttributeIds.includes(colorId) || (draft.attributeSelections[colorId]?.length ?? 0) > 0 ? [] : [syncHint]
  const colorOf = (row: ReadinessRow) => rowOptionValue(row, { name: colorOptionRequirement.attribute.name }).trim()
  const knows = (row: ReadinessRow) => Boolean(row.attributeValueIds[colorId])
    || colorOptionRequirement.attribute.values.some(value => normalizeVariantOptionValue(value.value) === normalizeVariantOptionValue(colorOf(row)))
  const withoutColor = draft.variantRows.filter(row => !row.attributeValueIds[colorId] && !colorOf(row))
  if (withoutColor.length) return [`Web Color otomatik aktarımı için ${withoutColor.length} varyantta Renk seçilmemiş.`]
  const unknown = [...new Set(draft.variantRows.filter(row => !knows(row)).map(colorOf))]
  return unknown.length ? [`Web Color için katalogda karşılığı olmayan renk: ${unknown.join(', ')}.`] : []
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
      ? autoWebColorIssues(draft, colorOptionRequirement)
      : !draft.manualWebColorValueId ? ['Manuel Web Color için katalog rengi seçin.'] : webColorRequirement.attribute.values.some(value => value.id === draft.manualWebColorValueId) ? [] : ['Manuel Web Color değeri geçerli değil.']
  // Web Color is a Trendyol field (no other marketplace's API has it): its check is a check of its own that only the
  // Trendyol cards read, so it never blocks Hepsiburada, n11, Pazarama or PTT AVM.
  const catalogValidationIssues = [...categoryValidationIssues, ...optionValidationIssues]
  const catalogValidationDetail = catalogValidationIssues.length ? catalogValidationIssues.join(' ') : 'Kategori özellikleri ve seçenekler yayınlamaya hazır.'
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
    { title: 'Kategori ve Seçenekler', detail: catalogValidationDetail, ok: catalogValidationIssues.length === 0, shopifyOk: true, shopifyDetail: 'Shopify yayını için pazaryeri kategori özellikleri gerekmez.' },
    ...(draft.variantsWithoutMedia ? [{ title: 'Varyant Görselleri', detail: `${draft.variantRows.length} varyanttan ${draft.variantsWithoutMedia} tanesine görsel seçilmedi. Varyant tablosundan veya “Seçeneklere görsel ata” ile görsel atayın.`, ok: false, shopifyOk: true, shopifyDetail: 'Shopify yayını varyant görseli olmadan da yapılır.' }] : []),
    { title: 'Web Color', detail: webColorValidationIssues.length ? webColorValidationIssues.join(' ') : 'Web Color yayınlamaya hazır.', ok: webColorValidationIssues.length === 0, platforms: ['TRENDYOL'] }
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
    variantsWithoutMedia: product.variants.filter(variant => !(variant.mediaUrls ?? []).length).length,
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

// One read of the mapping check, cached in the query client under the check's own prefix: the editor's cards (one
// run per form state, so a few runs while the form settles), the bulk check and its rechecks share it, so each request
// goes once. It sits under ['publication-attribute-readiness'], so every save that invalidates the check marks these
// reads stale too and the next run reads what was saved.
export function publicationReadinessSourceKey(connectionId: string, source: string, ...parts: string[]) {
  return ['publication-attribute-readiness', 'source', connectionId, source, ...parts] as const
}

// The card's mapping check: category mapping, the marketplace's required attributes and the value mappings of what the
// product uses. `cardName` names the marketplace in the messages.
// `categoryName` names the panel category in the "not mapped" gap.
export async function loadPublicationAttributeIssues(client: QueryClient, connection: ReadinessConnection, cardName: string, categoryId: string, selectedAttributes: PublicationAttributeSelection[], categoryName = ''): Promise<PublicationAttributeIssues> {
  const connectionId = encodeURIComponent(connection.id)
  const cached = <T,>(source: string, parts: string[], load: () => Promise<T>) => client.fetchQuery({ queryKey: publicationReadinessSourceKey(connection.id, source, ...parts), queryFn: load, staleTime: 15_000, retry: false })
  const categoryMapping = await readPublicationReadinessSource('Kategori eşlemesi', () => cached('category-mapping', [categoryId], () => hubApi<{ externalId: string; snapshotId: string; status: string } | null>(`/mappings/categories/${categoryId}?connectionId=${connectionId}`)))
  if (!categoryMapping || categoryMapping.status !== 'VERIFIED') {
    return { requiredIssues: [{ attribute: 'Kategori eşlemesi', detail: `${categoryName ? `${categoryName} kategorisinin` : 'Ürün kategorisinin'} ${cardName} karşılığı seçilmemiş; kategoriyi eşleyin.` }], optionalWarnings: [], categoryMapping: 'missing' }
  }

  const categoryReferences = await readPublicationReadinessSource(`${cardName} kategori listesi`, () => cached('reference-categories', [], () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; name: string; isActive: boolean; isLeaf: boolean }> }>(`/reference-data/categories?connectionId=${connectionId}`)))
  const mappedCategory = categoryReferences.items.find(item => item.externalId === categoryMapping.externalId && item.isActive && item.isLeaf)
  if (categoryMapping.snapshotId !== categoryReferences.snapshotId || !mappedCategory) {
    return { requiredIssues: [{ attribute: 'Kategori eşlemesi', detail: `${cardName} kategori eşlemesi güncel değil; kategori eşlemesini yenileyin.` }], optionalWarnings: [], categoryMapping: 'stale' }
  }
  if (connection.platformCode.trim().toUpperCase() === 'PTTAVM') return { requiredIssues: [], optionalWarnings: [], categoryScope: categoryMapping.externalId }

  const categoryScope = categoryMapping.externalId
  const [attributeReferences, attributeMappings] = await Promise.all([
    readPublicationReadinessSource(`${cardName} kategori özellik listesi`, () => cached('reference-attributes', [categoryScope], () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; name: string; isActive: boolean; isRequired?: boolean | null; allowsCustomValue?: boolean | null }> }>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes?connectionId=${connectionId}`))),
    readPublicationReadinessSource('Yerel kategori özellik eşlemeleri', () => cached('attribute-mappings', [categoryScope], () => hubApi<PublicationMappingReference[]>(`/mappings/attributes?connectionId=${connectionId}&scopeExternalId=${encodeURIComponent(categoryScope)}`)))
  ])
  const selectedIds = new Set(selectedAttributes.map(item => item.attributeId))
  const currentMappings = attributeMappings.filter(mapping => mapping.status === 'VERIFIED' && mapping.snapshotId === attributeReferences.snapshotId)
  const selectedExternalIds = [...new Set(currentMappings.filter(mapping => selectedIds.has(mapping.localId)).map(mapping => mapping.externalId))]
  const valueEntries = await Promise.all(selectedExternalIds.map(async externalAttributeId => {
    const valueScope = `${categoryScope}/${externalAttributeId}`
    const selectedAttribute = selectedAttributes.find(item => item.attributeId === currentMappings.find(mapping => mapping.externalId === externalAttributeId)?.localId)
    const attributeLabel = selectedAttribute?.name ?? externalAttributeId
    const mappings = await readPublicationReadinessSource(`“${attributeLabel}” yerel değer eşlemeleri`, () => cached('attribute-value-mappings', [valueScope], () => hubApi<PublicationMappingReference[]>(`/mappings/attribute-values?connectionId=${connectionId}&scopeExternalId=${encodeURIComponent(valueScope)}`)))
    let references: { snapshotId: string; items: Array<{ externalId: string; isActive: boolean }> }
    try {
      references = await readPublicationReadinessSource(`${cardName} “${attributeLabel}” özellik değer listesi`, () => cached('reference-attribute-values', [valueScope], () => hubApi<{ snapshotId: string; items: Array<{ externalId: string; isActive: boolean }> }>(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(externalAttributeId)}/values?connectionId=${connectionId}`)))
    } catch (reason) {
      if (!(reason instanceof ApiRequestError && reason.code === 'REFERENCE_SNAPSHOT_UNAVAILABLE') && !(reason instanceof PublicationReadinessSourceError && reason.apiCode === 'REFERENCE_SNAPSHOT_UNAVAILABLE')) throw reason
      references = { snapshotId: '', items: [] }
    }
    return [externalAttributeId, { snapshotId: references.snapshotId, items: references.items, mappings }] as const
  }))
  const valueReferencesByAttribute: Record<string, PublicationValueReferenceSet> = Object.fromEntries(valueEntries)
  // Connection defaults (e.g. Menşei = TR) cover required attributes the product leaves empty.
  const attributeDefaults = await cached('attribute-defaults', [], () => hubApi<Array<{ externalAttributeId: string }>>(`/connections/${connectionId}/attribute-defaults`)).catch(() => [])
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

// The card's product checks a marketplace needs (Shopify takes fewer; a check naming its marketplaces only those).
export function missingPublicationChecks(productChecks: PublicationProductCheck[], platformCode: string) {
  const platform = platformCode.trim().toUpperCase()
  const isShopify = platform === 'SHOPIFY'
  return productChecks
    .filter(check => !check.platforms || check.platforms.includes(platform))
    .filter(check => !(isShopify ? check.shopifyOk ?? check.ok : check.ok))
    .map(check => ({ ...check, detail: isShopify ? check.shopifyDetail ?? check.detail : check.detail }))
}

// The Shopify write permissions the server wants verified before it queues a publish (CatalogService
// HasShopifyPublicationEvidenceAsync, SHOPIFY_PUBLICATION_SCOPES_REQUIRED). The editor's card and the bulk check both
// read them through this key and turn them into gaps with `shopifyCapabilityIssues`, so the two agree.
export type ConnectionCapability = { code: string; supportLevel: string; verifiedForConnection: boolean }
export const requiredShopifyCapabilities = ['PRODUCT_WRITE', 'INVENTORY_WRITE', 'PUBLICATION_READ', 'PUBLICATION_WRITE'] as const
export function connectionCapabilitiesQueryKey(connectionId: string) {
  return ['capabilities', connectionId] as const
}
export function loadConnectionCapabilities(connectionId: string) {
  return hubApi<ConnectionCapability[]>(`/connections/${connectionId}/capabilities`)
}
export function missingShopifyCapabilities(capabilities: ConnectionCapability[] | undefined) {
  if (!capabilities) return []
  return requiredShopifyCapabilities.filter(code => !capabilities.some(capability => capability.code === code && capability.supportLevel.toUpperCase() === 'SUPPORTED' && capability.verifiedForConnection))
}
// A Shopify card's connection gaps: the permissions not verified, or that they could not be read. Other platforms: none.
export function shopifyCapabilityIssues(platformCode: string, capabilities: ConnectionCapability[] | undefined, readFailed: boolean): Array<{ attribute: string; detail: string }> {
  if (platformCode.trim().toUpperCase() !== 'SHOPIFY') return []
  const missing = missingShopifyCapabilities(capabilities)
  return [
    ...(missing.length ? [{ attribute: 'Shopify API izinleri', detail: `Ürün göndermeden önce Entegrasyonlar bölümünde Shopify erişimini yeniden yetkilendirin ve ${missing.join(', ')} izinlerini doğrulayın.` }] : []),
    ...(readFailed ? [{ attribute: 'Shopify API izin kontrolü', detail: 'Shopify bağlantı izinleri okunamadı. Entegrasyonlar bölümünde bağlantıyı test edip izin kanıtlarını yenileyin.' }] : [])
  ]
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
