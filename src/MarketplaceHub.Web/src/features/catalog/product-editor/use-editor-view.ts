// What the editor's sections show, worked out at render time: publish cards, variant code checks, the picture strip
// and its family, option groups, publication checks.
import { useMemo, useState } from 'react'
import { mediaImageKey, mediaStripRefs, mediaUrlsInPreferredOrder, uniqueMediaUrls } from '../product-media-editor'
import { type VariantCodeMenuProps } from './VariantCodeMenu'
import { buildSequentialVariantCodes, buildVariantGenerationDefaults, variantCodeChangeConfirmText, type VariantCodeTarget } from '../variant-generation'
import { filterVariantsByOptions } from '../variant-filtering'
import { formatColorOptionValue, mergeVariantOptionValues } from '../variant-option-matching'
import { publicationAttributeSelectionsFor, publicationProductChecks, rowOptionValue, type PublicationReadinessDraft } from './publication-readiness'
import { assignedColoursByPicture, coloursWithMixedPictures, variantOutgoingRefs, familyPictureOwners, pictureColourLabel } from './family-gallery-save'
import { withLatestVariantLinks } from './publish-card-variants'
import { pinSizeChartLast } from './size-chart-order'
import { mediaPickerLabel, mediaPickerRefs } from './media-picker-order'
import { type ProductMediaOption } from './variant-media-options'
import { isSizeOptionLabel } from './size-chart-measurements'
import { platformCodeIssues, summarizeVariantCodeIssues, variantCodeDuplicates, variantCodeFieldKey } from '../platform-code-rules'
import { useTakenVariantCodes } from '../variant-codes-taken'
import { canSaveAsDraft, isDraftStatus } from '../product-draft'
import { familyItemsShownInStrip } from '../family-media-clear'
import { variantCodeKey } from '../product-editor-state'
import { isProductUpdateConnection, productUpdateCapabilities } from '../platform-update-targets'
import { familyUpdatePlan, familyUpdatePlanWithLinks, familyUpdateReach } from './family-update-plan'
import { skuKey } from './submit-media-step'
import { readVariantMediaAssignmentDraft, variantMediaAssignmentKey } from '../variant-media-assignments'
import { useModelCodeHolder } from '../model-code-holder'
import { sortOptionValues } from './OptionValueChips'
import { MarketplaceConnection, VariantPlatformStatus, isColorOptionName, isProductPublicationConnection, isWebColorOptionName, productPublicationMode, variantOptionEntries } from '../shared'
import { VariantMediaGroup, VariantDraft } from './editor-types'
import { sortVariantsAlphabetically, isProductPublicationChannel, isSizeOptionName, displayVariantOptionSignature, variantSignatureKey, buildVariantMatrix, rowMatchesVariantMediaValue } from './editor-helpers'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import type { EditorDataResult } from './use-editor-data'
import type { EditorSeedResult } from './use-editor-seed'
import type { VariantRowDragResult } from './use-variant-row-drag'
import type { VariantMediaAutoSaveResult } from './use-variant-media-autosave'
import { priceInputNumber } from './card-prices'

export function useEditorView(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult) {
  const { editProductId, publishOnly, duplicateProductId, setNotice, form, attributeSelections, attributeTextValues, variantAttributeIds, variantRows, channelPricing, automaticBarcodeGeneration, wizardStep, setWizardStep, variantMediaModal, setVariantMobileGroup, pendingVariantCodeGeneration, variantBulkEditFilterSelections, mediaFiles, pendingFamilyMediaRemovals, familyMediaOrder, mediaStripOrder, sizeChartUrl, chartKey, removedVariantIds, webColorAutoEnabled, manualWebColorValueId, productAtSave, variantCodeConflicts, variantPlatformPricing, showFeedback, productToEdit, connections, pttAvmManualWrites, requirements, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, mediaUrls, sizeChartPickRequested, allRequirements, mappedRequirements, optionRequirements } = c
  // Taslak Olarak Kaydet shows on a new card and on a draft; a draft has no store to update yet.
  const draftSaveAvailable = canSaveAsDraft(Boolean(editProductId), productToEdit.data?.status)
  const editingDraftProduct = Boolean(editProductId) && isDraftStatus(productToEdit.data?.status)
  const [marketplacePricingOpen, setMarketplacePricingOpen] = useState(false)
  const publicationChannels = (connections.data?.items ?? []).filter(isProductPublicationChannel)
  // The cards read each variant's link from the latest product, not from the statuses the form was filled with.
  const publishLinkRows = withLatestVariantLinks(variantRows, editProductId ? productToEdit.data?.variants : undefined)
  const platformCards =publicationChannels.map(connection => ({
    code: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    name: connection.displayName,
    initial: connection.displayName.trim().charAt(0).toLocaleUpperCase('tr-TR') || 'R',
    tone: connection.platformCode.trim().toLocaleLowerCase('tr-TR'),
    connection,
    mode: productPublicationMode(connection)!
  }))
  // Every active connection's barcode and stock code rules: a product goes to all of them with one pair of codes.
  const codeRulePlatforms = useMemo(() => [...new Set((connections.data?.items ?? []).filter(connection => connection.status === 'ACTIVE' || connection.status === 'VERIFIED').map(connection => connection.platformCode))], [connections.data])
  const variantCodeIssues = useMemo(() => new Map(variantRows.map(row => [row.key, platformCodeIssues(codeRulePlatforms, row.barcode, row.sku)])), [codeRulePlatforms, variantRows])
  // Codes two rows of the table share, and codes another product holds, both found as they are typed (owner,
  // 11.11.2026: "aynı diye kırmızı yanmadı?"); a refused save's own list still names what the server found.
  const variantCodeDuplicateRows = useMemo(() => variantCodeDuplicates(variantRows.map(row => ({ key: row.key, barcode: row.barcode, sku: row.sku, label: displayVariantOptionSignature(row.optionSignature, row.options) }))), [variantRows])
  const ownVariantIds = useMemo(() => [...(productToEdit.data?.variants.map(variant => variant.id) ?? []), ...removedVariantIds], [productToEdit.data?.variants, removedVariantIds])
  const liveTakenCodes = useTakenVariantCodes(variantRows, ownVariantIds)
  const heldVariantCodes = useMemo(() => ({ ...liveTakenCodes, ...variantCodeConflicts }), [liveTakenCodes, variantCodeConflicts])
  // Rows with a red code field, missing codes and broken platform rules counted apart.
  const variantCodeIssueSummary = useMemo(() => summarizeVariantCodeIssues(variantRows, variantCodeIssues, (field, code) => Boolean(heldVariantCodes[variantCodeKey(field, code)]), (rowKey, field) => variantCodeDuplicateRows.has(variantCodeFieldKey(rowKey, field))), [heldVariantCodes, variantCodeDuplicateRows, variantCodeIssues, variantRows])
  // A stock code twice (in the table or on another card) breaks the catalog's unique index: even a draft waits for it.
  const heldSkuRows = variantRows.filter(row => row.sku.trim() && heldVariantCodes[variantCodeKey('sku', row.sku)]).length
  const draftCodeSaveBlock = (variantCodeIssueSummary?.duplicateSkuRows ?? 0) > 0 || heldSkuRows > 0
  // Kaydet, Hızlı Güncelle and Yayınlamaya Devam Et wait while a code field is red; they say why and lead to it.
  const codeRuleSaveBlock = variantCodeIssueSummary?.title ?? ''
  // "Yeni Ürün" only: the card that already holds the typed model code stops the create (and a new draft).
  const modelCodeHolder = useModelCodeHolder(form.modelCode, form.brandId, !editProductId)
  const modelCodeCreateBlock = !editProductId && modelCodeHolder ? modelCodeHolder.message : ''
  function goToFirstVariantCodeIssue() {
    if (!publishOnly && wizardStep !== 1) setWizardStep(1)
    setVariantMobileGroup('codes')
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const field = document.querySelector<HTMLInputElement>('.variant-table-row input.has-platform-code-issue')
      if (!field) return
      field.scrollIntoView({ block: 'center', behavior: 'smooth' })
      field.focus({ preventScroll: true })
    }))
  }
  function blockedByVariantCodeRules() {
    if (!codeRuleSaveBlock) return false
    const message = `${codeRuleSaveBlock}. Kırmızı alanları düzeltmeden kaydedilemez.`
    setNotice(message); showFeedback('Varyant Kodları Hatalı', message, 'error')
    goToFirstVariantCodeIssue()
    return true
  }
  const variantPricingPlatforms = useMemo<VariantPlatformStatus[]>(() => (connections.data?.items ?? []).filter(isProductPublicationConnection).map(connection => {
    const pricing = channelPricing[connection.id]
    return {
      platform: connection.displayName,
      platformCode: connection.platformCode,
      isLinked: true,
      status: connection.status,
      connectionId: connection.id,
      listPrice: priceInputNumber(pricing?.listPrice ?? fallbackListPrice),
      salePrice: priceInputNumber(pricing?.salePrice ?? fallbackSalePrice),
      currency: form.currency
    }
  }), [channelPricing, connections.data?.items, fallbackListPrice, fallbackSalePrice, form.currency])
  const productUpdateTargets = (connections.data?.items ?? []).filter(isProductUpdateConnection).map(connection => ({
    connection,
    capabilities: productUpdateCapabilities(connection.platformCode),
    manualWritesEnabled: pttAvmManualWrites.get(connection.id)
  }))
  // Which colours of the model family "Platformlarda Güncelle" reaches on each store, shown before it is sent.
  const updateFamilyProduct = productToEdit.data
  const platformUpdateFamilyReach = updateFamilyProduct
    ? (connection: MarketplaceConnection) => familyUpdateReach(familyUpdatePlan({ openedProductId: updateFamilyProduct.id, familyProductIds: updateFamilyProduct.familyProductIds, variants: updateFamilyProduct.variants, connectionId: connection.id, platformCode: connection.platformCode }))
    : undefined
  // The cards of the family the summary above Gönder compares on a store: the ones the update reaches there.
  // Same function and the same link rows as Gönder (editor-marketplace-sends.ts): the editor's rows with the latest links
  // of the loaded product laid over them; Gönder adds the saved product's links, which only a save can give.
  const previewLinkRowsBySku = updateFamilyProduct ? new Map(withLatestVariantLinks(variantRows, updateFamilyProduct.variants).map(row => [skuKey(row.sku), row])) : undefined
  const platformUpdatePreviewCards = updateFamilyProduct
    ? (connection: MarketplaceConnection) => familyUpdatePlanWithLinks({ openedProductId: updateFamilyProduct.id, familyProductIds: updateFamilyProduct.familyProductIds, variants: updateFamilyProduct.variants, linkRowsBySku: previewLinkRowsBySku, connectionId: connection.id, platformCode: connection.platformCode }).send.map(card => card.productId)
    : undefined
  const assignedMediaUrls = [...new Set(variantRows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4))))]
  const stripProduct = productAtSave ?? productToEdit.data
  const familyMediaUrls = stripProduct?.familyMediaUrls ?? []
  const familyMediaItems = stripProduct?.familyMediaItems ?? familyMediaUrls.map(url => ({ url, mediaIds: [], sourceProductTitles: [] }))
  // The card's own saved pictures the draft no longer holds ("Tümünü Temizle", a removed tile) go on the next save; the
  // family list still carries them until then, so they leave the gallery now instead of after Kaydet.
  const draftMediaKeys = new Set(mediaUrls.map(mediaImageKey))
  // The Beden values in the panel's order (the card's option values), then any a variant row holds besides.
  const rowSizes = [...new Set(variantRows.flatMap(row => Object.entries(row.options).filter(([name, value]) => isSizeOptionLabel(name) && value.trim()).map(([, value]) => value.trim())))]
  const panelSizes = (stripProduct?.options ?? []).find(option => isSizeOptionLabel(option.label))?.values.map(value => value.label.trim()) ?? []
  const sizeChartSizes = [...panelSizes.filter(size => rowSizes.includes(size)), ...rowSizes.filter(size => !panelSizes.includes(size))]
  const sizeChartDisabledReason = !editProductId ? 'Önce ürünü kaydedin.' : !sizeChartSizes.length ? 'Ürünün Beden seçeneği yok.' : null
  const pendingRemovalMediaKeys = new Set(editProductId ? [...(stripProduct?.mediaUrls ?? []).map(mediaImageKey).filter(key => !draftMediaKeys.has(key)), ...pendingFamilyMediaRemovals.map(item => mediaImageKey(item.url))] : [])
  // The default strip is each colour's product-level pictures (familyMediaUrls); the items cover every family picture
  // a custom order can show, variant pictures included.
  const defaultFamilyOrder = stripProduct?.familyOrderedMediaUrls?.length
    ? stripProduct.familyOrderedMediaUrls
    : [...(stripProduct?.familyMediaUrls ?? familyMediaItems.map(item => item.url)), ...mediaUrls]
  // A picture the draft added by link (a size chart put in with "Kullan") shows even before the family lists know it.
  const visibleMediaUrls = pinSizeChartLast(mediaUrlsInPreferredOrder(uniqueMediaUrls([...defaultFamilyOrder, ...mediaUrls]), familyMediaOrder.length ? familyMediaOrder : defaultFamilyOrder)
    .filter(url => !pendingRemovalMediaKeys.has(mediaImageKey(url))), chartKey)
  const mediaStrip = pinSizeChartLast(mediaStripRefs(visibleMediaUrls, mediaFiles.map(item => item.id), mediaStripOrder), chartKey)
  // Any saved picture of the model can be the size chart: the card's own uploads, pictures imported from a marketplace and
  // the other colours' pictures (owner, 15 Nov 2026: "kendi eklemediğim şeylerden de beden tablosu seçebileyim"). Kaydet
  // then writes it to every colour card and marks it there (family-gallery-save.ts). A file not uploaded yet cannot be.
  const sizeChartPickable = Boolean(editProductId) && mediaStrip.some(ref => ref.startsWith('url|'))
  const pickingSizeChart = sizeChartPickRequested && sizeChartPickable
  const familyOnlyMediaItems = familyItemsShownInStrip(visibleMediaUrls, familyMediaItems, draftMediaKeys)
  // Each tile names the colours its picture is saved to (Kaydet writes it to those colour cards), by the save's own rule.
  const familyGalleries = editProductId ? stripProduct?.familyGalleries ?? null : null
  const variantCardIds = new Map((stripProduct?.variants ?? []).map(variant => [variant.id, variant.productId || stripProduct?.id || '']))
  const rowSize = (row: VariantDraft) => Object.entries(row.options).find(([name, value]) => isSizeOptionLabel(name) && value.trim())?.[1].trim() ?? ''
  const rowColour = (row: VariantDraft) => Object.entries(row.options).find(([name, value]) => isColorOptionName(name) && value.trim())?.[1].trim() ?? ''
  const rowCardId = (row: VariantDraft) => variantCardIds.get(row.key) ?? `new:${rowColour(row).toLocaleLowerCase('tr-TR')}`
  const cardColours = new Map<string, string>()
  for (const row of variantRows) if (rowColour(row) && !cardColours.has(rowCardId(row))) cardColours.set(rowCardId(row), rowColour(row))
  const heldPictureKeys = new Set((familyGalleries ?? []).flatMap(card => [...card.mediaUrls, ...card.variantMediaUrls]).map(mediaImageKey))
  const pictureOwners = familyGalleries && cardColours.size > 1 ? familyPictureOwners({
    strip: mediaStrip,
    cards: [...familyGalleries, ...[...cardColours.keys()].filter(id => id.startsWith('new:')).map(productId => ({ productId, mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null }))],
    rows: variantRows.map(row => ({ productId: rowCardId(row), refs: row.mediaRefs })),
    newRefs: mediaStrip.filter(ref => ref.startsWith('file|') || !heldPictureKeys.has(mediaImageKey(ref.slice(4)))),
    chart: { url: sizeChartUrl, initialUrl: sizeChartUrl }
  }) : null
  const pictureColourCaption = (ref: string) => pictureOwners ? pictureColourLabel(pictureOwners.get(ref.startsWith('file|') ? ref : mediaImageKey(ref.slice(4))) ?? new Set(), cardColours) : ''
  // What each variant row goes to the marketplaces with (the server's rule): its icon shows that count.
  const assignedColours = assignedColoursByPicture(variantRows.map(row => ({ colour: rowColour(row), refs: row.mediaRefs })))
  const chartRef = sizeChartUrl ? mediaStrip.find(ref => ref.startsWith('url|') && mediaImageKey(ref.slice(4)) === mediaImageKey(sizeChartUrl)) ?? `url|${sizeChartUrl}` : null
  const outgoingByRow = new Map(variantRows.map(row => [row.key, variantOutgoingRefs({ rowRefs: row.mediaRefs, rowCardId: rowCardId(row), rowColour: rowColour(row), strip: mediaStrip, owners: pictureOwners, assignedColours, chartRef })]))
  // Trendyol keeps one picture list per colour (one content per colour): the colours whose sizes would go differently.
  const trendyolMixedColours = (connections.data?.items ?? []).some(connection => connection.platformCode === 'TRENDYOL')
    ? coloursWithMixedPictures(variantRows.map(row => ({ colour: rowColour(row), refs: outgoingByRow.get(row.key)?.refs ?? [] })))
    : []
  const hasMediaTiles = mediaUrls.length > 0 || mediaFiles.length > 0 || familyOnlyMediaItems.length > 0
  const mediaUploadHint = "Adet sınırı yok · JPEG, PNG veya WebP · WebP ve 6 MB üstü JPEG'e çevrilir"
  // In the strip's order (Görseller), new uploads where they stand and the size chart last, so Görsel 01, 02… are the
  // same pictures in the same places as on the page (media-picker-order.ts).
  const mediaChoices: ProductMediaOption[] = mediaPickerRefs(mediaStrip, [...mediaUrls, ...familyMediaUrls.filter(url => !pendingRemovalMediaKeys.has(mediaImageKey(url))), ...assignedMediaUrls], chartKey)
    .flatMap((ref): Omit<ProductMediaOption, 'label'>[] => {
      if (!ref.startsWith('file|')) return [{ value: ref, url: ref.slice(4) }]
      const item = mediaFiles.find(file => file.id === ref.slice(5))
      return item ? [{ value: ref, file: item.file }] : []
    })
    .map((option, index) => ({ ...option, label: mediaPickerLabel(index, Boolean(option.file)) }))
  const bulkMediaGroups = useMemo<VariantMediaGroup[]>(() => {
    const groups: VariantMediaGroup[] = []
    const groupsByName = new Map<string, VariantMediaGroup>()
    const hasPanelColorSource = (productToEdit.data?.options ?? []).some(option => isColorOptionName(option.label) && !isWebColorOptionName(option.label))
      || optionRequirements.some(item => isColorOptionName(item.attribute.name) && !isWebColorOptionName(item.attribute.name))
      || variantRows.some(row => variantOptionEntries(row).some(option => isColorOptionName(option.name) && !isWebColorOptionName(option.name)))
    const addGroup = (source: VariantMediaGroup) => {
      // Persisted options and stale attribute selections can still list values whose
      // variants were deleted; only offer values that some variant row still uses.
      const group = variantRows.length
        ? { ...source, values: source.values.filter(value => variantRows.some(row => rowMatchesVariantMediaValue(row, source, value))) }
        : source
      if (hasPanelColorSource && isWebColorOptionName(group.name)) return
      const canonicalName = isColorOptionName(group.name) ? 'Renk' : group.name.trim()
      const name = canonicalName.toLocaleLowerCase('tr-TR')
      if (!group.values.length) return
      const formatValues = (values: VariantMediaGroup['values']) => isColorOptionName(canonicalName)
        ? values.map(value => ({ ...value, value: formatColorOptionValue(value.value) }))
        : values
      const existing = groupsByName.get(name)
      if (existing) {
        existing.values = sortOptionValues(canonicalName, formatValues(mergeVariantOptionValues(existing.values, group.values)))
        existing.attributeId ??= group.attributeId
        return
      }
      const mergedGroup = { ...group, name: canonicalName, values: sortOptionValues(canonicalName, formatValues(group.values)) }
      groupsByName.set(name, mergedGroup)
      groups.push(mergedGroup)
    }
    // Existing products expose their persisted option groups separately from
    // category requirements. Prefer these IDs/values so imported variants such
    // as “Beden: Tek Ebat” can be targeted even when the current category no
    // longer exposes the original option requirement.
    // When every variant was cleared the persisted options are stale; the option
    // selections above on the page are the source of truth then.
    const persistedOptions = variantRows.length || !removedVariantIds.length ? productToEdit.data?.options ?? [] : []
    for (const option of persistedOptions) {
      addGroup({ id: `product-option:${option.id}`, name: option.label, values: option.values.map(value => ({ id: value.id, value: value.label })) })
    }
    for (const item of optionRequirements) {
      const selectedIds = attributeSelections[item.attributeId] ?? []
      const values = item.attribute.values
        .filter(value => selectedIds.includes(value.id) || variantRows.some(row => row.attributeValueIds[item.attributeId] === value.id || rowOptionValue(row, { name: item.attribute.name }).trim().toLocaleLowerCase('tr-TR') === value.value.trim().toLocaleLowerCase('tr-TR')))
        .map(value => ({ id: value.id, value: value.value }))
      addGroup({ id: `category-attribute:${item.attributeId}`, name: item.attribute.name, attributeId: item.attributeId, values })
    }
    const signatureValues = new Map<string, { name: string; values: Map<string, string> }>()
    for (const row of variantRows) {
      for (const option of variantOptionEntries(row)) {
        const groupKey = option.name.trim().toLocaleLowerCase('tr-TR')
        const group = signatureValues.get(groupKey) ?? { name: option.name.trim(), values: new Map<string, string>() }
        group.values.set(option.value.trim().toLocaleLowerCase('tr-TR'), option.value.trim())
        signatureValues.set(groupKey, group)
      }
    }
    for (const group of signatureValues.values()) {
      addGroup({ id: `variant-signature:${group.name.toLocaleLowerCase('tr-TR')}`, name: group.name, values: [...group.values].map(([id, value]) => ({ id, value })) })
    }
    return groups
  }, [attributeSelections, optionRequirements, productToEdit.data?.options, removedVariantIds.length, variantRows])
  const variantFilterGroups = useMemo(() => bulkMediaGroups.filter(group => (isColorOptionName(group.name) || isSizeOptionName(group.name)) && variantRows.some(row => rowOptionValue(row, group).trim())), [bulkMediaGroups, variantRows])
  const variantBulkEditFilterGroups = variantFilterGroups.filter(group => isColorOptionName(group.name) || isSizeOptionName(group.name))
  const variantBulkEditMatchingRows = filterVariantsByOptions(variantRows, variantBulkEditFilterGroups, variantBulkEditFilterSelections, rowMatchesVariantMediaValue)
  const hasVariantBulkEditFilters = Object.values(variantBulkEditFilterSelections).some(valueIds => valueIds.length > 0)
  const barcodeRowCount = variantRows.filter(row => row.barcode.trim()).length
  const emptySkuBarcodeRowCount = variantRows.filter(row => row.barcode.trim() && !row.sku.trim()).length
  const emptyBarcodeRowCount = variantRows.length - barcodeRowCount
  const skuRowCount = variantRows.filter(row => row.sku.trim()).length
  const emptySkuRowCount = variantRows.length - skuRowCount
  const pendingVariantCodeConfirm = pendingVariantCodeGeneration ? variantCodeChangeConfirmText(pendingVariantCodeGeneration) : null
  const variantCodeGenerationPreview = (target: VariantCodeTarget): VariantCodeMenuProps['generatePreview'] => {
    const codes = buildSequentialVariantCodes(form.modelCode, variantRows.length)
    const ends = (list: string[]): [string, string] | null => list.length ? [list[0], list[list.length - 1]] : null
    return { all: ends(codes), missing: ends(codes.filter((_, index) => !variantRows[index]?.[target].trim())) }
  }
  const selectedBulkMediaGroup = variantMediaModal?.mode === 'bulk' ? bulkMediaGroups.find(group => group.id === variantMediaModal.groupId) : undefined
  const selectedBulkMediaValueId = variantMediaModal?.mode === 'bulk' ? variantMediaModal.valueId : undefined
  const selectedBulkMediaValue = selectedBulkMediaGroup?.values.find(value => value.id === selectedBulkMediaValueId)
  const selectedBulkMediaMatchCount = selectedBulkMediaGroup && selectedBulkMediaValue ? variantRows.filter(row => rowMatchesVariantMediaValue(row, selectedBulkMediaGroup, selectedBulkMediaValue)).length : 0
  const selectedBulkMediaGroupMatchCount = selectedBulkMediaGroup ? new Set(variantRows.filter(row => selectedBulkMediaGroup.values.some(value => rowMatchesVariantMediaValue(row, selectedBulkMediaGroup, value))).map(row => row.key)).size : 0
  const selectedBulkMediaAssignmentKey = variantMediaModal?.mode === 'bulk' ? variantMediaAssignmentKey(variantMediaModal.groupId, variantMediaModal.valueId) : ''
  const selectedVariantMediaRefs = variantMediaModal?.mode === 'bulk'
    ? readVariantMediaAssignmentDraft(variantMediaModal.draftsBySelection, selectedBulkMediaAssignmentKey)
    : variantMediaModal?.draftRefs ?? []
  const variantsWithoutMediaCount = variantRows.filter(row => !row.mediaRefs.length).length
  const publicationDraft: PublicationReadinessDraft = { variantsWithoutMedia: variantsWithoutMediaCount, title: form.title, description: form.description, brandId: form.brandId, modelCode: form.modelCode, barcode: form.barcode, categoryId: form.categoryId, automaticBarcodeGeneration, mediaCount: mediaUrls.length + mediaFiles.length, variantAttributeIds, variantRows, attributeSelections, attributeTextValues, webColorAutoEnabled, manualWebColorValueId }
  const productChecks = publicationProductChecks(publicationDraft, { items: allRequirements, isLoading: requirements.isLoading, isError: requirements.isError, isSuccess: requirements.isSuccess })
  const publicationAttributeSelections = useMemo(() => publicationAttributeSelectionsFor({ title: '', description: '', brandId: '', modelCode: '', barcode: '', categoryId: '', automaticBarcodeGeneration: false, mediaCount: 0, variantAttributeIds: [], variantRows, attributeSelections, attributeTextValues, webColorAutoEnabled, manualWebColorValueId }, allRequirements), [allRequirements, attributeSelections, attributeTextValues, manualWebColorValueId, variantRows, webColorAutoEnabled])
  const canAddVariantCombinations = useMemo(() => {
    if (!variantAttributeIds.length || (automaticBarcodeGeneration && !form.modelCode.trim())) return false
    try {
      const generated = buildVariantMatrix(mappedRequirements, variantAttributeIds, attributeSelections, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, automaticBarcodeGeneration, form.modelCode)
      const existing = new Map(variantRows.map(row => [variantSignatureKey(row.optionSignature), row]))
      const missingCombinations = generated.some(row => !existing.has(variantSignatureKey(row.optionSignature)))
      // Saved rows keep their codes, so on a saved product only a missing combination makes "Ürünleri Ekle" do something.
      const automaticDefaultsOutOfDate = automaticBarcodeGeneration && !editProductId && sortVariantsAlphabetically(variantRows).some((row, index) => {
        const expected = buildVariantGenerationDefaults({ modelCode: form.modelCode, sequence: index + 1, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice })
        return row.sku !== expected.sku || row.barcode !== expected.barcode
      })
      return missingCombinations || automaticDefaultsOutOfDate
    } catch { return false }
  }, [attributeSelections, automaticBarcodeGeneration, editProductId, fallbackCostPrice, fallbackListPrice, fallbackSalePrice, form.modelCode, mappedRequirements, variantAttributeIds, variantRows])
  const selectedVariantPlatformRow = variantPlatformPricing ? variantRows.find(row => row.key === variantPlatformPricing.rowKey) : undefined
  const showVariantStockTotal = Boolean(editProductId || (duplicateProductId && variantRows.length))
  return { draftSaveAvailable, editingDraftProduct, marketplacePricingOpen, setMarketplacePricingOpen, publicationChannels, publishLinkRows, platformCards, codeRulePlatforms, variantCodeIssues, variantCodeDuplicateRows, ownVariantIds, liveTakenCodes, heldVariantCodes, variantCodeIssueSummary, heldSkuRows, draftCodeSaveBlock, codeRuleSaveBlock, modelCodeHolder, modelCodeCreateBlock, goToFirstVariantCodeIssue, blockedByVariantCodeRules, variantPricingPlatforms, productUpdateTargets, updateFamilyProduct, platformUpdateFamilyReach, platformUpdatePreviewCards, assignedMediaUrls, stripProduct, familyMediaUrls, familyMediaItems, draftMediaKeys, rowSizes, panelSizes, sizeChartSizes, sizeChartDisabledReason, pendingRemovalMediaKeys, defaultFamilyOrder, visibleMediaUrls, mediaStrip, sizeChartPickable, pickingSizeChart, familyOnlyMediaItems, familyGalleries, variantCardIds, rowSize, rowColour, rowCardId, cardColours, heldPictureKeys, pictureOwners, pictureColourCaption, assignedColours, chartRef, outgoingByRow, trendyolMixedColours, hasMediaTiles, mediaUploadHint, mediaChoices, bulkMediaGroups, variantFilterGroups, variantBulkEditFilterGroups, variantBulkEditMatchingRows, hasVariantBulkEditFilters, barcodeRowCount, emptySkuBarcodeRowCount, emptyBarcodeRowCount, skuRowCount, emptySkuRowCount, pendingVariantCodeConfirm, variantCodeGenerationPreview, selectedBulkMediaGroup, selectedBulkMediaValueId, selectedBulkMediaValue, selectedBulkMediaMatchCount, selectedBulkMediaGroupMatchCount, selectedBulkMediaAssignmentKey, selectedVariantMediaRefs, variantsWithoutMediaCount, publicationDraft, productChecks, publicationAttributeSelections, canAddVariantCombinations, selectedVariantPlatformRow, showVariantStockTotal }
}
export type EditorViewResult = ReturnType<typeof useEditorView>
