// Filling the form from the saved product (edit) or its source (copy), and keeping it filled from the right version.
// Owns: a refetch during the editor's own save or picture assignment save is not a refill; a newer version over
// unsaved edits raises the stale banner instead (WEB-SAYFALAR-1, If-Match from seededProduct); the editor's own
// writes move the seeded product on (adoptOwnProductWrite); Web Color and option inference run after the seed.
import { useEffect, useEffectEvent, useLayoutEffect, useState } from 'react'
import { uniqueMediaUrls } from '../product-media-editor'
import { matchingVariantOptionValues } from '../variant-option-matching'
import { optionSignatureFromOptions, orderMediaUrlsByVariants } from './publication-readiness'
import { familySizeChartUrl } from './family-gallery-save'
import { takePendingCreateMedia } from './pending-create-media'
import { editorStatusFor } from '../product-draft'
import { withoutCopyRemovedMedia, withoutCopyRemovedMediaRefs } from '../product-copy-media-removals'
import { editorCustomChannelPrices, panelPrices, priceFieldText } from '../quick-price-edit'
import { trNumberInput } from '../../../shared/tr-number'
import { editorDataStamp, editorSeedKey, editorOpenedDesi, editorVat } from '../product-editor-state'
import { baselineWithWebColor } from './editor-reducer'
import { copiedChannelPricing } from './marketplace-channel-pricing'
import { Product, normalizeVariantOptionName, variantOptionEntries } from '../shared'
import { PendingProductMediaFile } from './editor-types'
import { seedVariantMediaRefs, sortVariantsAlphabetically } from './editor-helpers'
import { editorWarrantyText } from './required-fields'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import type { EditorDataResult } from './use-editor-data'

export function useEditorSeed(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult) {
  const { editProductId, duplicateProductId, copyRemovedMedia, setForm, setAttributeSelections, setAttributeTextValues, setVariantAttributeIds, setVariantRows, setChannelPricing, setSeededProductId, initializedEditProductKey, initializedEditOptionsKey, initializedEditWebColorKey, setMediaFiles, setPendingFamilyMediaRemovals, setFamilyMediaOrder, setFamilyMediaOrderDirty, setSizeChartUrl, initialSizeChartUrl, sizeChartBeforeFamilyRemoval, setSizeChartPicking, setRemovedVariantIds, setRemovedVariantRows, setReplacedPlaceholderIds, requestBaseline, adjustBaseline, setWebColorAutoEnabled, setManualWebColorValueId, webColorSeed, webColorBaselineRequest, requestWebColorBaseline, setStaleProductVersion, allowReseedUntil, editorSaveInFlight, seededProduct, mediaFilesKeptOnReseed, initialCustomChannelIds, channelPricingBase, setFieldErrors, initialEditMediaUrl, initialEditModelCode, initialEditDesi, initialEditVariantMediaRefs, mediaAutoSaveInFlight, editorDirty, editorDirtyRef, restoreAfterSnapshot, productToEdit, seedProduct, requirements, allRequirements, webColorRequirement, optionRequirements } = c
  const seedForm = useEffectEvent(() => {
    const product = seedProduct
    if (!product) return
    const mode = editProductId ? 'edit' : 'copy'
    const productKey = editorSeedKey(mode, product.id, product.version, editorDataStamp(product.variants))
    if (initializedEditProductKey.current === productKey) return
    // A newer version (another tab, an order, the importer) must not wipe edits that are not saved yet: the form is
    // kept and a banner offers to reload it. The editor's own saves allow the next reseed.
    const alreadySeededThisProduct = initializedEditProductKey.current?.startsWith(`${mode}:${product.id}:`) ?? false
    // A refetch that lands while this editor is saving carries the editor's own save: it is neither another change
    // nor a reason to refill half way. The save asks for the reseed when it is done (live 2026-11-02: a refetch during
    // the media step took the new version, so the refetch after the save matched it, the form was never refilled, its
    // 21 new rows kept their temporary keys and the next Kaydet sent them as new variants again: SKU conflict).
    // A picture assignment saved on its own (saveVariantMediaAssignments) is the editor's own write as well.
    if (editProductId && alreadySeededThisProduct && (editorSaveInFlight.current || mediaAutoSaveInFlight.current)) return
    if (editProductId && alreadySeededThisProduct && editorDirty && Date.now() > allowReseedUntil.current) { setStaleProductVersion(product.version); return }
    allowReseedUntil.current = 0
    setStaleProductVersion(null)
    initializedEditProductKey.current = productKey
    if (mode === 'edit') { setSeededProductId(product.id); seededProduct.current = product }
    const primary = product.variants[0]
    const initialModelCode = primary?.modelCode ?? product.modelCode ?? ''
    const sortedVariants = sortVariantsAlphabetically(product.variants)
    const removedMediaKeys = mode === 'copy' ? copyRemovedMedia.current : []
    const savedMediaUrls = withoutCopyRemovedMedia(orderMediaUrlsByVariants(product.variants, product.mediaUrls ?? [], product.primaryImageUrl, product.hasCustomMediaOrder, product.id), removedMediaKeys)
    const savedFamilyMediaUrls = withoutCopyRemovedMedia(product.familyMediaUrls ?? product.familyMediaItems?.map(item => item.url) ?? [], removedMediaKeys)
    const initialListPrice = product.defaultListPrice ?? primary?.listPrice ?? primary?.salePrice ?? 0
    const initialSalePrice = product.defaultSalePrice ?? primary?.salePrice ?? 0
    initialEditModelCode.current = initialModelCode.trim()
    const openedDesi = editorOpenedDesi(product.variants)
    initialEditDesi.current = openedDesi ?? 0
    setForm({ title: product.title, description: product.description ?? '', brandId: product.brandId ?? '', categoryId: product.categoryId ?? '', baseSku: mode === 'copy' ? '' : primary?.sku ?? '', barcode: mode === 'copy' ? '' : primary?.barcode ?? '', modelCode: initialModelCode, weight: trNumberInput(primary?.weight), width: trNumberInput(primary?.width), length: trNumberInput(primary?.length), height: trNumberInput(primary?.height), desi: openedDesi == null ? '' : trNumberInput(openedDesi), warrantyMonths: editorWarrantyText(product.warrantyMonths), deliveryDays: String(product.deliveryDays ?? 1), listPrice: priceFieldText(initialListPrice), salePrice: priceFieldText(initialSalePrice), costPrice: priceFieldText(primary?.costPrice), currency: primary?.currency ?? 'TRY', ...editorVat(product, primary), initialStock: String(primary?.onHand ?? 0), safetyStock: String(primary?.safetyStock ?? 0), mediaUrls: savedMediaUrls.join('\n'), status: editorStatusFor(product.status) })
    // An existing product opens with each marketplace's stored choice (else what its offer prices say): a marketplace
    // with its own price has its switch on and shows that price; a card with variants is on as soon as one variant's
    // offer there keeps its own price, and shows the first such variant's price.
    if (editProductId) {
      const customPrices = editorCustomChannelPrices(sortedVariants, product)
      setChannelPricing(customPrices)
      channelPricingBase.current = customPrices
      initialCustomChannelIds.current = new Set(Object.keys(customPrices))
    }
    if (!editProductId && duplicateProductId) setChannelPricing(copiedChannelPricing(primary?.platformStatuses ?? [], initialListPrice, initialSalePrice))
    initialEditMediaUrl.current = savedMediaUrls.join('\n')
    // A copy starts without a size chart; an edit opens with the one the card has.
    const seededSizeChartUrl = editProductId ? familySizeChartUrl(product.familyGalleries, product.sizeChartUrl ?? null) : null
    initialSizeChartUrl.current = seededSizeChartUrl
    setSizeChartUrl(seededSizeChartUrl)
    setSizeChartPicking(false)
    setFamilyMediaOrder(withoutCopyRemovedMedia(uniqueMediaUrls(product.familyOrderedMediaUrls?.length
      ? product.familyOrderedMediaUrls
      : [...savedFamilyMediaUrls, ...savedMediaUrls]), removedMediaKeys))
    setFamilyMediaOrderDirty(false)
    setPendingFamilyMediaRemovals([])
    sizeChartBeforeFamilyRemoval.current = null
    setMediaFiles(mediaFilesKeptOnReseed.current ?? [])
    mediaFilesKeptOnReseed.current = null
    const leftByCreate = editProductId ? takePendingCreateMedia<PendingProductMediaFile>(product.id) : undefined
    if (leftByCreate) restoreAfterSnapshot.current = leftByCreate
    const seededMediaRefs = seedVariantMediaRefs(product.variants).map(references => withoutCopyRemovedMediaRefs(references, removedMediaKeys))
    const mediaRefsByVariantId = new Map(product.variants.map((variant, index) => [variant.id, seededMediaRefs[index] ?? []]))
    initialEditVariantMediaRefs.current = Object.fromEntries(mediaRefsByVariantId)
    setVariantRows(sortedVariants.map(variant => {
      const options = Object.fromEntries(variantOptionEntries(variant).map(option => [option.name, option.value]))
      const persistedAttributes = variant.attributes ?? []
      const attributeValueIds = Object.fromEntries(persistedAttributes.flatMap(attribute => attribute.valueId ? [[attribute.attributeId, attribute.valueId] as const] : []))
      return { key: variant.id, optionSignature: optionSignatureFromOptions(options) || (variant.optionSignature && variant.optionSignature !== '-' ? variant.optionSignature : 'Tek Ürün'), options, attributeValueIds, persistedAttributes, sku: mode === 'copy' ? '' : variant.sku, barcode: mode === 'copy' ? '' : variant.barcode ?? '', stock: variant.onHand, ...(editProductId ? { loadedStock: variant.onHand, loadedVersion: variant.version } : {}), salePrice: panelPrices(variant, product).salePrice ?? 0, listPrice: panelPrices(variant, product).listPrice ?? 0, costPrice: variant.costPrice ?? null, ...(editProductId ? { loadedCostPrice: variant.costPrice ?? null } : {}), mediaRefs: mediaRefsByVariantId.get(variant.id) ?? [], ...(editProductId ? { platformStatuses: variant.platformStatuses ?? [] } : {}) }
    }))
    const selected: Record<string, string[]> = {}; const typed: Record<string, string> = {}
    for (const attribute of product.attributes ?? []) { if (attribute.valueId) selected[attribute.attributeId] = [...(selected[attribute.attributeId] ?? []), attribute.valueId]; else if (attribute.textValue != null) typed[attribute.attributeId] = attribute.textValue; else if (attribute.numberValue != null) typed[attribute.attributeId] = String(attribute.numberValue); else if (attribute.booleanValue != null) typed[attribute.attributeId] = attribute.booleanValue ? 'evet' : 'hayır' }
    setAttributeSelections(selected); setAttributeTextValues(typed); setVariantAttributeIds([])
    setRemovedVariantIds([]); setRemovedVariantRows([]); setReplacedPlaceholderIds([])
    setFieldErrors({})
    requestBaseline()
  })
  const [reseedRequest, setReseedRequest] = useState(0)
  const seedStamp = seedProduct ? editorDataStamp(seedProduct.variants) : ''
  useLayoutEffect(() => seedForm(), [editProductId, seedProduct?.id, seedProduct?.version, seedStamp, reseedRequest])
  // The editor's own writes outside Kaydet (a saved picture removed, picture assignments, marketplace prices) change
  // the saved product too. A clean form refills from the product they leave as usual; a form with other unsaved edits
  // keeps them and stands on that product from then on, so Kaydet names its version and no banner calls the editor's
  // own write another operation's change.
  function adoptOwnProductWrite(product: Product | undefined) {
    if (!product || !editProductId || product.id !== editProductId) return
    seededProduct.current = product
    if (!editorDirtyRef.current) { setReseedRequest(value => value + 1); return }
    standOnOwnWrite(editorSeedKey('edit', product.id, product.version, editorDataStamp(product.variants)))
    setStaleProductVersion(null)
  }
  // A form with other unsaved edits stands on the version the editor's own write left, and that version is not a seed:
  // the Web Color and option inference below do not run for it, since both refill the user's fields and retake the
  // baseline, which made the unsaved edits read as saved (W1, scan 2026-11-15). The write moves the baseline itself,
  // for the fields it saved.
  function standOnOwnWrite(productKey: string) {
    initializedEditProductKey.current = productKey
    initializedEditWebColorKey.current = productKey
    initializedEditOptionsKey.current = productKey
  }
  // The banner's "reload": drops the unsaved edits and fills the form from the latest saved product.
  function reloadEditedProduct() {
    allowReseedUntil.current = Date.now() + 10_000
    setStaleProductVersion(null)
    void productToEdit.refetch().finally(() => setReseedRequest(value => value + 1))
  }

  // Layout effects: the saved Web Color choice and the variant options are in before the first paint, so the publish
  // cards never show a check made without them.
  useLayoutEffect(() => {
    const product = seedProduct
    if ((!editProductId && !duplicateProductId) || !product || !webColorRequirement || requirements.isLoading) return
    const productKey = editorSeedKey(editProductId ? 'edit' : 'copy', product.id, product.version, editorDataStamp(product.variants))
    if (initializedEditProductKey.current !== productKey || initializedEditWebColorKey.current === productKey) return
    initializedEditWebColorKey.current = productKey
    const savedManualColor = product.trendyolWebColorValueId ?? ''
    setWebColorAutoEnabled(!savedManualColor)
    setManualWebColorValueId(savedManualColor)
    webColorSeed.current = { autoEnabled: !savedManualColor, manualValueId: savedManualColor }
    requestWebColorBaseline()
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [duplicateProductId, editProductId, requirements.isLoading, seedProduct, webColorRequirement])
  // Runs after the render that holds the seeded Web Color and after any baseline taken before it: the baseline takes the
  // seeded Web Color as its own, so filling it is never an unsaved change while a real change still is.
  useEffect(() => {
    const seeded = webColorSeed.current
    if (!webColorBaselineRequest || !seeded) return
    webColorSeed.current = null
    adjustBaseline(current => baselineWithWebColor(current, seeded))
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [webColorBaselineRequest])

  useLayoutEffect(() => {
    const product = seedProduct
    if ((!editProductId && !duplicateProductId) || !product || !allRequirements.length || requirements.isLoading) return
    const productKey = editorSeedKey(editProductId ? 'edit' : 'copy', product.id, product.version, editorDataStamp(product.variants))
    if (initializedEditProductKey.current !== productKey || initializedEditOptionsKey.current === productKey) return
    initializedEditOptionsKey.current = productKey
    const inferred: Record<string, string[]> = {}
    for (const requirement of optionRequirements) {
      const valuesInVariants = product.variants.flatMap(variant => variantOptionEntries(variant)
        .filter(option => normalizeVariantOptionName(option.name) === normalizeVariantOptionName(requirement.attribute.name))
        .map(option => option.value))
      const ids = matchingVariantOptionValues(requirement.attribute.values, valuesInVariants).map(value => value.id)
      if (ids.length) inferred[requirement.attributeId] = ids
    }
    setAttributeSelections(current => ({ ...current, ...inferred }))
    setVariantAttributeIds(Object.keys(inferred))
    setVariantRows(rows => rows.map(row => {
      const source = product.variants.find(variant => variant.id === row.key)
      if (!source) return row
      const options = Object.fromEntries(variantOptionEntries(source).map(option => [option.name, option.value]))
      const attributeValueIds = Object.fromEntries(optionRequirements.flatMap(requirement => {
        const option = variantOptionEntries(source).find(item => normalizeVariantOptionName(item.name) === normalizeVariantOptionName(requirement.attribute.name))
        const value = matchingVariantOptionValues(requirement.attribute.values, option ? [option.value] : [])[0]
        return value ? [[requirement.attributeId, value.id]] : []
      }))
      return { ...row, optionSignature: optionSignatureFromOptions(options) || (source.optionSignature && source.optionSignature !== '-' ? source.optionSignature : row.optionSignature), options, attributeValueIds }
    }))
    requestBaseline()
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [allRequirements, duplicateProductId, editProductId, optionRequirements, requirements.isLoading, seedProduct])

  return { reseedRequest, setReseedRequest, seedStamp, adoptOwnProductWrite, standOnOwnWrite, reloadEditedProduct }
}
export type EditorSeedResult = ReturnType<typeof useEditorSeed>
