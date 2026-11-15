// Kaydet, Ürünü Oluştur, Taslak, Yayınla and Platformlarda Güncelle: the product write and the steps after it.
// Owns: Kaydet awaits a running picture assignment save, names the seeded version in If-Match, marks the save in
// flight so a refetch meanwhile is no refill, and asks for the reseed after its own refetch.
import { type FormEvent } from 'react'
import { ApiRequestError, hubApi } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey } from '../../../shared/idempotency'
import { sanitizeRichText } from '../../../shared/security/sanitizeHtml'
import { appendNotification, productNotificationSubject } from '../../../shared/notifications'
import { followJobOutcomes, outcomeTarget, type FollowedJob } from '../../../shared/job-outcome'
import { buildVariantAttributeAssignments } from '../variant-attribute-assignments'
import { mediaImageKey, mediaRefsEqual, modelCodeForExistingVariant, publicProductMediaUrls } from '../product-media-editor'
import { isVariantDeleteRefusal, restoreRemovedVariantRows, VARIANT_DELETE_REFUSED_HINT } from '../variant-row-removal'
import { isOptionRequirement } from './publication-readiness'
import { mapWithConcurrency, mediaFilesLeftAfterFailedSave, mediaSaveProgressMessage, newColoursWithoutPictures, savedMediaOrder, storedProductMediaUrl, variantMediaReconcilePlan } from './product-media-save'
import { FAMILY_SHARED_FIELDS } from './family-shared-fields'
import { familyGalleryPlan, familyUploadTargets, type ColourRowPictures, type FamilyCardPictures } from './family-gallery-save'
import { keepPendingCreateMedia, pendingVariantMediaRefs } from './pending-create-media'
import { productDeliveryDays } from './product-delivery-days'
import { editorOfferPriceReason, platformUpdateWritesOffers } from './platform-update-fields'
import { withLatestVariantLinks } from './publish-card-variants'
import { editorWarrantyMonths, editorWarrantyUpdate, requiredEditorFieldLabels } from './required-fields'
import { parseProductDesi } from '../product-desi'
import { pinSizeChartLast } from './size-chart-order'
import { productMediaUrlIssue } from '../product-media-url'
import { canSaveAsDraft, draftBlockingIssues, draftMissingSummary, draftProductTitle, isDraftStatus, variantCodeRulesStopSave } from '../product-draft'
import { removePendingFamilyMedia } from '../family-media-clear'
import { sameProductSnapshotIgnoringVersion } from '../product-save-version'
import { channelTakesListPrice, editorChannelOfferPrices, editorOfferUnchanged, offerHasSalePrice, panelFollowChoiceVariantIds, unpricedOfferWarning } from '../quick-price-edit'
import { editorFieldErrors, editorOpeningStock, editorStockUpdates, editorVariantCostUpdate, editorVariantDesiUpdate, editorVatRateIssue, rekeySavedVariantRows, variantCodeConflictsFrom } from '../product-editor-state'
import { applyVariantStockSets, setVariantOnHand } from '../variant-stock-set'
import { chosenGapSkipped, productPublicationTargets, productSaveFeedback, type MarketplacePublishOutcome, type MarketplaceUpdateOutcome, type ProductSaveFeedback } from '../product-publication-submit'
import { variantOwnerProductId } from '../variant-media-assignments'
import { Product, isColorOptionName } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { VariantDraft } from './editor-types'
import { productSaveToastKey, PLACEHOLDER_KEPT_MESSAGE, channelOfferWriteConcurrency, productAttributePayload, publishPlatformName } from './editor-helpers'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import type { EditorDataResult } from './use-editor-data'
import type { EditorSeedResult } from './use-editor-seed'
import type { VariantRowDragResult } from './use-variant-row-drag'
import type { VariantMediaAutoSaveResult } from './use-variant-media-autosave'
import type { EditorViewResult } from './use-editor-view'
import type { AttributeActionsResult } from './use-attribute-actions'
import type { VariantActionsResult } from './use-variant-actions'
import type { VariantCodeActionsResult } from './use-variant-code-actions'
import type { EditorMediaActionsResult } from './use-editor-media-actions'
import type { PlatformUpdateResult } from './use-platform-update'
import type { ChannelPricingResult } from './use-channel-pricing'
import type { EditorChecksResult } from './use-editor-checks'
import { sendToMarketplaces } from './editor-marketplace-sends'

export function useEditorSubmit(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult & VariantCodeActionsResult & EditorMediaActionsResult & PlatformUpdateResult & ChannelPricingResult & EditorChecksResult) {
  const { editProductId, publishOnly, client, navigate, setError, setNotice, operationToasts, setFeedback, setSubmitting, calculateDesi, form, attributeSelections, attributeTextValues, variantAttributeIds, setVariantRows, selectedChannelIds, blockedPublishCards, channelPricing, variantChannelPricing, wizardStep, setWizardStep, pendingPlatformUpdate, mediaFiles, setMediaFiles, pendingFamilyMediaRemovals, setPendingFamilyMediaRemovals, familyMediaOrderDirty, setFamilyMediaOrderDirty, sizeChartUrl, initialSizeChartUrl, sizeChartBeforeFamilyRemoval, chartKey, removedVariantIds, setRemovedVariantIds, removedVariantRows, setRemovedVariantRows, replacedPlaceholderIds, setReplacedPlaceholderIds, setSnapshotRequest, webColorAutoEnabled, manualWebColorValueId, setStaleProductVersion, allowReseedUntil, editorSaveInFlight, seededProduct, setProductAtSave, setVariantCodeConflicts, mediaFilesKeptOnReseed, initialCustomChannelIds, setFieldErrors, productCreateKey, leavingAfterSave, initialEditModelCode, initialEditVariantMediaRefs, showFeedback, mediaAutoSave, editorDirty, productToEdit, connections, requirements, fallbackListPrice, fallbackSalePrice, missingRequiredFields, desi, mediaUrls, mappedRequirements, webColorRequirement, setReseedRequest, draftCodeSaveBlock, codeRuleSaveBlock, modelCodeHolder, blockedByVariantCodeRules, draftMediaKeys, pendingRemovalMediaKeys, mediaStrip, trendyolMixedColours, channelPriceDraft, channelPriceEdited, rowsForSubmit, validate, stopOnRequiredFields } = c
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const platformUpdate = pendingPlatformUpdate.current
    pendingPlatformUpdate.current = null
    const submitter = (event.nativeEvent as SubmitEvent).submitter
    const submitData = new FormData(event.currentTarget)
    // Taslak Olarak Kaydet (product-draft.ts): saves the card as it is, on a new card or a draft only, and sends nothing.
    const explicitDraft = !platformUpdate && submitter?.getAttribute('data-submit-intent') === 'draft' && canSaveAsDraft(Boolean(editProductId), productToEdit.data?.status)
    const editingDraft = Boolean(editProductId) && isDraftStatus(productToEdit.data?.status)
    // Required fields (required-fields.ts; owner, 10 Nov 2026). With one empty, Kaydet / Ürünü Oluştur on a new card or a
    // draft keeps the card as a draft (never sent; the toast names what is missing); publishing, Yayınlamaya Devam Et and
    // a platform update stop with each field framed red. A card on sale or closed is not turned into a draft (the server
    // moves the whole colour family with a status change): its save stops the same way until the fields are filled.
    const plainSave = !platformUpdate && !explicitDraft && (submitter?.getAttribute('data-submit-intent') === 'save'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'save')
      || submitData.get('intent') === 'save'
      || (!editProductId && (submitter?.getAttribute('data-submit-intent') === 'create' || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'create'))))
    const missingRequired = explicitDraft ? [] : missingRequiredFields
    const autoDraft = missingRequired.length > 0 && plainSave && canSaveAsDraft(Boolean(editProductId), productToEdit.data?.status)
    if (missingRequired.length && !autoDraft) { stopOnRequiredFields(missingRequired, Boolean(editProductId) && !editingDraft && plainSave); return }
    const saveDraft = explicitDraft || autoDraft
    const saveAndStay = saveDraft || submitter?.getAttribute('data-submit-intent') === 'save'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'save')
      || submitData.get('intent') === 'save'
    const createOnly = !editProductId && !saveDraft && (submitter?.getAttribute('data-submit-intent') === 'create'
      || (submitter?.getAttribute('name') === 'intent' && submitter?.getAttribute('value') === 'create'))
    // A draft's normal Kaydet puts it on sale, so it is checked as a new card is.
    const requireCompleteCatalog = !saveDraft && (!editProductId || !saveAndStay || editingDraft)
    // A draft may keep red codes; every other save waits until they are fixed.
    if ((variantCodeRulesStopSave(saveDraft, codeRuleSaveBlock) || draftCodeSaveBlock) && blockedByVariantCodeRules()) return
    // A model code belongs to one product: a new card (a draft too) may not take one the catalog already holds.
    if (!editProductId && modelCodeHolder) {
      setFieldErrors(current => ({ ...current, modelCode: modelCodeHolder.message }))
      if (!publishOnly) setWizardStep(1)
      showFeedback('Model Kodu Kullanılıyor', modelCodeHolder.message, 'error')
      return
    }
    // KDV Oranı is required on every card (the server refuses a card without one): stop here, on the first step where
    // the field is, with its message under it. A draft may be saved without it.
    const vatRateIssue = editorVatRateIssue(form.vatRate)
    if (vatRateIssue && !saveDraft) {
      setFieldErrors(current => ({ ...current, vatRate: vatRateIssue }))
      if (!publishOnly) setWizardStep(1)
      setError(new Error(vatRateIssue)); setNotice(vatRateIssue)
      showFeedback('Zorunlu Alan Eksik', vatRateIssue, 'error')
      return
    }
    if (wizardStep !== 2 && !saveAndStay && !createOnly && !platformUpdate) {
      setWizardStep(2)
      return
    }
    // Whether the product itself had edits to write: the result says "Ürün bilgileri kaydedildi" only then.
    const productChangedAtSubmit = editorDirty
    // The pending toast names what this press does: publishing (Yayınla), updating the stores, or only saving.
    const publishing = !saveAndStay && !createOnly && !platformUpdate && selectedChannelIds.length > 0
    const storeCount = `${selectedChannelIds.length} platforma`
    const [pendingTitle, pendingMessage] = saveDraft
      ? ['Taslak Kaydediliyor', 'Ürün taslak olarak kaydediliyor…']
      : platformUpdate
      ? ['Platformlar Güncelleniyor', 'Ürün kaydediliyor ve seçili mağazalarda güncelleniyor…']
      : publishing
        ? editProductId
          ? ['Ürün Yayınlanıyor', productChangedAtSubmit ? `Ürün kaydediliyor ve ${storeCount} gönderiliyor…` : `Ürün ${storeCount} gönderiliyor…`]
          : ['Ürün Oluşturuluyor', `Ürün oluşturuluyor ve ${storeCount} gönderiliyor…`]
        : editProductId ? ['Ürün Kaydediliyor', 'Ürün değişiklikleri kaydediliyor…'] : ['Ürün Oluşturuluyor', 'Ürün oluşturuluyor…']
    setError(undefined); setFieldErrors({}); setNotice(''); showFeedback(pendingTitle, pendingMessage, 'info', { persistent: true, key: productSaveToastKey }); setSubmitting(true); editorSaveInFlight.current = true; setProductAtSave(seededProduct.current ?? productToEdit.data ?? null); let productCreated: Product | undefined; let familyMediaChanged = false; let mediaStep = false
    const uploadedMediaFileIds = new Set<string>()
    // What a create that stops after the product exists hands to the product's page (pending-create-media.ts).
    const storedUrlByFileId = new Map<string, string>()
    const writtenVariantMediaIds = new Set<string>()
    let submittedRows: VariantDraft[] = []
    try {
      // A picture assignment still being saved on its own goes first (WEB-SAYFALAR-9): the save then starts from the
      // product it left, instead of racing it.
      if (mediaAutoSave.current) await mediaAutoSave.current
      let submitRequirements = requirements.data ?? []
      if (form.categoryId && !requirements.data) {
        const loadedRequirements = await requirements.refetch()
        if (loadedRequirements.isError || !loadedRequirements.data) throw new Error('Kategori özellikleri alınamadı. Güvenli kayıt için kategori eşleştirmesini kontrol edip tekrar deneyin.')
        submitRequirements = loadedRequirements.data
      }
      const optionRequirementsForSubmit = submitRequirements.filter(isOptionRequirement).map(item => ({ attributeId: item.attributeId, name: item.attribute.name, values: item.attribute.values }))
      if (requireCompleteCatalog && form.categoryId && requirements.isError && !requirements.data) throw new Error('Kategori özellikleri alınamadı. Önce kategori eşleştirmesini kontrol edin.')
      const requirementList = mappedRequirements
      // A draft is stopped only by values the server refuses (clashing codes, malformed prices or image links); what
      // the normal save would ask for comes back as the list of what to complete before the card goes on sale.
      let draftMissing = ''
      let rows: VariantDraft[]
      if (saveDraft) {
        try { validate(rowsForSubmit(true), true, true) } catch (reason) { draftMissing = reason instanceof Error ? reason.message : '' }
        if (vatRateIssue) draftMissing = `${vatRateIssue} ${draftMissing}`.trim()
        rows = rowsForSubmit(false)
        const blocking = draftBlockingIssues(rows, form)
        const invalidMediaUrl = publicProductMediaUrls([...mediaUrls, ...rows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4)))]).map(productMediaUrlIssue).find((issue): issue is string => issue !== null)
        if (invalidMediaUrl) blocking.push(`Görsel bağlantısı geçersiz: ${invalidMediaUrl} Ürün kaydedilmeden önce düzeltin veya kaldırın.`)
        if (form.warrantyMonths.trim() && (!Number.isInteger(Number(form.warrantyMonths)) || Number(form.warrantyMonths) < 0 || Number(form.warrantyMonths) > 240)) blocking.push('Garanti süresi 0-240 ay arasında tam sayı olmalıdır.')
        if (blocking.length) throw new Error(blocking.join(' '))
      } else {
        rows = rowsForSubmit(requireCompleteCatalog); validate(rows, requireCompleteCatalog, !createOnly)
      }
      submittedRows = rows
      const savedTitle = saveDraft ? draftProductTitle(form.title, form.modelCode, new Date()) : form.title
      const savedStatus = saveDraft ? 'DRAFT' : form.status
      const savedVatRate = saveDraft && vatRateIssue ? null : Number(form.vatRate || 0)
      const savedVatInclusion = saveDraft && vatRateIssue ? null : form.vatIncluded || 'INCLUDED'
      const regularGlobalAttributes = requirementList
        .filter(item => !isOptionRequirement(item) && item.attributeId !== webColorRequirement?.attributeId && !variantAttributeIds.includes(item.attributeId))
        .flatMap((item, index) => productAttributePayload(item, attributeSelections[item.attributeId] ?? [], attributeTextValues[item.attributeId] ?? '', index))
      // The manual Web Color is sent apart (trendyolWebColorValueId) so it never becomes the product's Renk on other marketplaces.
      const colorGlobalAttributes = webColorRequirement && !variantAttributeIds.includes(webColorRequirement.attributeId)
          ? productAttributePayload(webColorRequirement, attributeSelections[webColorRequirement.attributeId] ?? [], attributeTextValues[webColorRequirement.attributeId] ?? '', regularGlobalAttributes.length)
          : []
      const globalAttributes = [...regularGlobalAttributes, ...colorGlobalAttributes]
      // Do not overwrite existing assignments while a newly selected category's
      // requirements are still loading (or failed). The edit form may be saved
      // without optional mapping data.
      const shouldPersistAttributes = !editProductId || Boolean(form.categoryId && requirements.isSuccess)
      const variantAttributesForPayload = (row: VariantDraft) => buildVariantAttributeAssignments({
        options: row.options,
        selectedValueIds: row.attributeValueIds,
        existing: row.persistedAttributes ?? [],
        optionRequirements: optionRequirementsForSubmit
      })
      const safeDescription = sanitizeRichText(form.description)
      const variantPayload = (row: VariantDraft, index: number) => ({ sku: row.sku, barcode: row.barcode || null, modelCode: form.modelCode.trim() || null, sortOrder: index, weight: calculateDesi ? Number(form.weight) || null : null, width: calculateDesi ? Number(form.width) || null : null, height: calculateDesi ? Number(form.height) || null : null, length: calculateDesi ? Number(form.length) || null : null, desi: calculateDesi ? desi || 1 : parseProductDesi(form.desi) || null, costPrice: row.costPrice, defaultListPrice: row.listPrice, defaultSalePrice: row.salePrice, options: row.options, attributes: variantAttributesForPayload(row), onHand: editorOpeningStock(row.stock) })
      // The product the form was filled from (WEB-SAYFALAR-1): its version, variants, model codes and desi are what this
      // save is made against; a newer refetch held back by the stale banner is not.
      const loadedProduct = editProductId ? seededProduct.current ?? productToEdit.data : undefined
      const existingVariantIds = new Set(loadedProduct?.variants.map(variant => variant.id) ?? [])
      const placeholderIdsToDelete = replacedPlaceholderIds.filter(id => existingVariantIds.has(id) && !rows.some(row => row.key === id) && !removedVariantIds.includes(id))
      const modelCodeForRow = (variantId: string) => modelCodeForExistingVariant(
        form.modelCode,
        initialEditModelCode.current,
        loadedProduct?.variants.find(variant => variant.id === variantId)?.modelCode
      )
      let product: Product
      let productVersionRetryUsed = false
      if (loadedProduct) {
        const editProduct = loadedProduct
        const updatePayload = {
          title: savedTitle,
          status: savedStatus,
          description: safeDescription,
          brandId: form.brandId || null,
          categoryId: form.categoryId || null,
          defaultListPrice: fallbackListPrice,
          defaultSalePrice: fallbackSalePrice,
          ...editorWarrantyUpdate(form.warrantyMonths),
          deliveryDays: productDeliveryDays(form.deliveryDays),
          vatRate: savedVatRate,
          vatInclusion: savedVatInclusion,
          trendyolWebColorValueId: !webColorAutoEnabled && manualWebColorValueId ? manualWebColorValueId : '00000000-0000-0000-0000-000000000000',
          // Temel Ürün Bilgileri are the model's: every save writes them to every colour card (family-shared-fields.ts).
          familySharedFields: [...FAMILY_SHARED_FIELDS],
          // The colour cards' versions as the form was filled (WEB-SAYFALAR-2): a card changed meanwhile whose shared
          // field this save would overwrite refuses it (409 FAMILY_CHANGED) instead.
          loadedFamilyVersions: editProduct.familyVersions ?? null,
          ...(shouldPersistAttributes ? { attributes: globalAttributes } : {}),
          variantIdsToDelete: removedVariantIds.filter(id => existingVariantIds.has(id) && !rows.some(row => row.key === id)),
          placeholderVariantIdsToDelete: placeholderIdsToDelete,
          variantsToCreate: rows.filter(row => !existingVariantIds.has(row.key)).map(variantPayload),
          variantUpdates: rows.filter(row => existingVariantIds.has(row.key)).map(row => ({
            id: row.key,
            sku: row.sku,
            barcode: row.barcode || null,
            modelCode: modelCodeForRow(row.key),
            // Only a cost the user changed is sent, with the cost it was loaded with; an unsent (null) cost is left as
            // it is on the server, and a cost changed there meanwhile refuses the save (409).
            ...editorVariantCostUpdate(row),
            // The desi is the model's too (owner, 14.11.2026): every save gives it to every size of every colour.
            ...editorVariantDesiUpdate(calculateDesi ? desi || 1 : parseProductDesi(form.desi), true, editProduct.variants.find(variant => variant.id === row.key)?.desi),
            defaultListPrice: row.listPrice,
            defaultSalePrice: row.salePrice,
            // The version the row was loaded with: a row changed meanwhile elsewhere refuses the save (409) instead of
            // being overwritten with what this form still shows.
            loadedVersion: row.loadedVersion ?? null,
            sortOrder: rows.findIndex(candidate => candidate.key === row.key),
            options: row.options,
            attributes: variantAttributesForPayload(row)
          }))
        }
        const updateProductAtVersion = (version: number) => hubApi<Product>(`/products/${editProduct.id}`, {
          method: 'PATCH',
          headers: { 'If-Match': `"v${version}"` },
          body: JSON.stringify(updatePayload)
        })
        try {
          product = await updateProductAtVersion(editProduct.version)
        } catch (reason) {
          if (!(reason instanceof ApiRequestError) || reason.status !== 412) throw reason
          const latestProduct = await hubApi<Product>(`/products/${editProduct.id}`)
          if (!sameProductSnapshotIgnoringVersion(editProduct, latestProduct)) {
            throw new Error(`Ürün siz düzenlerken panelde başka bir işlemle güncellendi, bu yüzden değişiklikleriniz kaydedilmedi. Sayfayı yenileyip tekrar deneyin.`)
          }
          try {
            product = await updateProductAtVersion(latestProduct.version)
            productVersionRetryUsed = true
          } catch (retryReason) {
            if (retryReason instanceof ApiRequestError && retryReason.status === 412) {
              throw new Error('Ürün kaydedilirken yeniden değişti. Güncel veriyi korumak için sayfayı yenileyip tekrar deneyin.')
            }
            throw retryReason
          }
        }
      } else {
        product = await sendWithVersionedIdempotencyKey(productCreateKey, key => hubApi<Product>('/products', { method: 'POST', headers: { 'Idempotency-Key': key }, body: JSON.stringify({ title: savedTitle, status: savedStatus, description: safeDescription, brandId: form.brandId || null, categoryId: form.categoryId || null, defaultListPrice: fallbackListPrice, defaultSalePrice: fallbackSalePrice, warrantyMonths: editorWarrantyMonths(form.warrantyMonths), deliveryDays: productDeliveryDays(form.deliveryDays), vatRate: savedVatRate, vatInclusion: savedVatInclusion, trendyolWebColorValueId: !webColorAutoEnabled && manualWebColorValueId ? manualWebColorValueId : null, attributes: globalAttributes, variants: rows.map(variantPayload) }) }))
      }
      productCreated = product; const completed: string[] = []; const warnings: string[] = []
      if (placeholderIdsToDelete.some(id => product.variants.some(variant => variant.id === id))) warnings.push(PLACEHOLDER_KEPT_MESSAGE)
      const publications: MarketplacePublishOutcome[] = []
      const publicationErrors: Array<{ platformName: string; message: string }> = []
      const updates: MarketplaceUpdateOutcome[] = []
      // The jobs this press started, followed until they say how they ended (aktarıldı / aktarılamadı, with the reason).
      const followedJobs: FollowedJob[] = []
      // The steps after the product (media, stock, channel prices, publication) can take a while; the progress toast names them.
      if (!editProductId) showFeedback('Ürün Oluşturuldu', 'Ürün oluşturuldu; görseller, stok ve fiyatlar kaydediliyor…', 'info', { persistent: true, key: productSaveToastKey })
      // O8: from here the card is saved; a picture step that fails says so, the card's data stays saved.
      mediaStep = true
      // The create page makes one card: its pictures go to it. The edit page holds the whole model: every colour card's
      // pictures are written from the one gallery (family-gallery-save.ts), the same whichever colour row was opened.
      if (!editProductId && mediaUrls.length > 0) await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: product.id, variantId: null, items: mediaUrls.map((url, sortOrder) => ({ url, sortOrder })), altText: form.title }) })
      const rowsBySku = new Map(rows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      const savedProduct = product
      // Every colour card of the model after the save (a new colour's card included), oldest first.
      const familyCards: FamilyCardPictures[] = [...(savedProduct.familyGalleries ?? [{ productId: savedProduct.id, mediaUrls: savedProduct.mediaUrls ?? [], variantMediaUrls: [], sizeChartUrl: initialSizeChartUrl.current }])]
      for (const variant of savedProduct.variants) {
        const cardId = variant.productId || savedProduct.id
        if (!familyCards.some(card => card.productId === cardId)) familyCards.push({ productId: cardId, mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null })
      }
      const colourRows = (resolve: (ref: string) => string | null): ColourRowPictures[] => savedProduct.variants.flatMap(variant => {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        return row ? [{ productId: variant.productId || savedProduct.id, refs: pinSizeChartLast(row.mediaRefs, chartKey).flatMap(ref => resolve(ref) ?? []) }] : []
      })
      // Uploads and media writes on one product stay one at a time: each raises the product's version, which the
      // server checks. Each new file is uploaded once, on the first card of its colours; the others reference its copy.
      const progressTitle = editProductId ? pendingTitle : 'Ürün Oluşturuldu'
      const showProgress = (message: string) => showFeedback(progressTitle, message, 'info', { persistent: true, key: productSaveToastKey })
      const uploadTargets = editProductId ? familyUploadTargets(mediaFiles.map(item => item.id), familyCards, colourRows(ref => ref), savedProduct.id) : new Map<string, string>()
      const uploadedByCard = new Map<string, string[]>()
      for (const [fileIndex, pendingMedia] of mediaFiles.entries()) {
        showProgress(mediaSaveProgressMessage('upload', fileIndex, mediaFiles.length))
        const targetId = uploadTargets.get(pendingMedia.id) ?? product.id
        const sortOrder = editProductId ? (familyCards.find(card => card.productId === targetId)?.mediaUrls.length ?? 0) + (uploadedByCard.get(targetId)?.length ?? 0) : mediaUrls.length + fileIndex
        const data = new FormData(); data.set('file', pendingMedia.file); data.set('productId', targetId); data.set('mediaRole', sortOrder === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(Math.min(sortOrder, 999))); data.set('altText', form.title)
        const uploaded = await hubApi<{ fileAssetId?: string }>('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: data })
        if (uploaded?.fileAssetId) {
          storedUrlByFileId.set(pendingMedia.id, storedProductMediaUrl(uploaded.fileAssetId))
          uploadedByCard.set(targetId, [...(uploadedByCard.get(targetId) ?? []), storedProductMediaUrl(uploaded.fileAssetId)])
        }
        uploadedMediaFileIds.add(pendingMedia.id)
      }
      // The create page: the uploads above go after the saved pictures; a new file put before them takes its place now.
      if (!editProductId && storedUrlByFileId.size) {
        const productMediaOrder = savedMediaOrder([...mediaStrip.filter(ref => ref.startsWith('file|') || draftMediaKeys.has(mediaImageKey(ref.slice(4)))), ...mediaUrls.map(url => `url|${url}`)], storedUrlByFileId)
        const appendedMediaOrder = savedMediaOrder([...mediaUrls.map(url => `url|${url}`), ...mediaFiles.map(item => `file|${item.id}`)], storedUrlByFileId)
        if (!mediaRefsEqual(productMediaOrder, appendedMediaOrder)) await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: product.id, variantId: null, items: productMediaOrder.map((url, sortOrder) => ({ url, sortOrder })), altText: form.title }) })
      }
      const pendingFileIds = new Set(mediaFiles.map(item => item.id))
      const variantMediaWrites = product.variants.flatMap(variant => {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        if (!row) return []
        const originalMediaRefs = initialEditVariantMediaRefs.current[variant.id]
        if (editProductId && originalMediaRefs && mediaRefsEqual(pinSizeChartLast(row.mediaRefs, chartKey), originalMediaRefs)) return []
        return [{ variant, row: { ...row, mediaRefs: pinSizeChartLast(row.mediaRefs, chartKey) } }]
      })
      for (const [writeIndex, { variant, row }] of variantMediaWrites.entries()) {
        if (mediaFiles.length) showProgress(mediaSaveProgressMessage('variants', writeIndex, variantMediaWrites.length))
        const altText = `${form.title} · ${row.optionSignature}`
        const plan = variantMediaReconcilePlan(row.mediaRefs, storedUrlByFileId, pendingFileIds)
        const variantProductId = variantOwnerProductId(product.variants, variant.id, product.id)
        await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: variantProductId, variantId: variant.id, items: plan.items, altText }) })
        for (const upload of plan.uploads) {
          const file = mediaFiles.find(item => item.id === upload.fileId)?.file
          if (file) { const data = new FormData(); data.set('file', file); data.set('productId', variantProductId); data.set('variantId', variant.id); data.set('mediaRole', upload.sortOrder === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(upload.sortOrder)); data.set('altText', altText); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: data }) }
        }
        writtenVariantMediaIds.add(variant.id)
      }
      // Every colour card's gallery from the one strip: a picture goes to the colours it is assigned to, a new one without
      // a colour and the size chart to every colour, the rest stay where they are (family-gallery-save.ts).
      const familyStripUrls = savedMediaOrder(mediaStrip, storedUrlByFileId)
      let galleryWriteCount = 0
      let familyChartMarked = false
      const cardColourLabels: string[] = []
      if (editProductId) {
        const heldKeys = new Set(familyCards.flatMap(card => [...card.mediaUrls, ...card.variantMediaUrls]).map(mediaImageKey))
        const resolveRef = (ref: string) => ref.startsWith('url|') ? ref.slice(4) : ref.startsWith('file|') ? storedUrlByFileId.get(ref.slice(5)) ?? null : null
        const galleryPlan = familyGalleryPlan({
          strip: familyStripUrls,
          cards: familyCards,
          rows: colourRows(resolveRef),
          uploaded: uploadedByCard,
          newUrls: [...storedUrlByFileId.values(), ...familyStripUrls.filter(url => !heldKeys.has(mediaImageKey(url)))],
          removedKeys: pendingRemovalMediaKeys,
          chart: { url: sizeChartUrl, initialUrl: initialSizeChartUrl.current }
        })
        const colourOfCard = (productId: string) => {
          const variant = savedProduct.variants.find(item => (item.productId || savedProduct.id) === productId)
          const row = variant ? rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR')) : undefined
          return Object.entries(row?.options ?? {}).find(([name, value]) => isColorOptionName(name) && value.trim())?.[1].trim() ?? 'renk kartı'
        }
        // A size chart made with "Kullan" is on no card yet and only the card it was made for may take it first; the other
        // colours then reference that copy.
        const galleryWrites = sizeChartUrl && !heldKeys.has(mediaImageKey(sizeChartUrl))
          ? [...galleryPlan.galleryWrites].sort((left, right) => Number(right.productId === savedProduct.id) - Number(left.productId === savedProduct.id))
          : galleryPlan.galleryWrites
        for (const write of galleryWrites) {
          await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: write.productId, variantId: null, items: write.urls.map((url, sortOrder) => ({ url, sortOrder })), altText: form.title }) })
          cardColourLabels.push(`${colourOfCard(write.productId)}${write.added ? ` +${write.added}` : ''}${write.removed ? ` −${write.removed}` : ''}`)
        }
        galleryWriteCount = galleryPlan.galleryWrites.length
        // Which picture is the size chart, once the pictures are written (the chart must be one of each card's own).
        for (const mark of galleryPlan.chartMarks) {
          await hubApi(`/products/${mark.productId}/size-chart`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ url: mark.url }) })
          familyChartMarked = true
        }
      }
      let removedFamilyMediaCount = 0
      if (editProductId && pendingFamilyMediaRemovals.length) {
        removedFamilyMediaCount = await removePendingFamilyMedia(pendingFamilyMediaRemovals, mediaIds => hubApi<{ deletedCount: number }>('/files/product-media-items', {
          method: 'DELETE',
          headers: { 'Idempotency-Key': newIdempotencyKey() },
          body: JSON.stringify({ mediaIds })
        }))
        setPendingFamilyMediaRemovals([])
        sizeChartBeforeFamilyRemoval.current = null
      }
      // The model's order (the strip) on every card, after a move or any picture written above: each card numbers its own
      // rows 0, 1… when written, the family order ranks them again.
      if (editProductId && (familyMediaOrderDirty || galleryWriteCount > 0 || mediaFiles.length > 0) && familyStripUrls.length) {
        const result = await hubApi<{ changedProductCount: number }>('/files/product-media-family-reorder', {
          method: 'PUT',
          headers: { 'Idempotency-Key': newIdempotencyKey() },
          body: JSON.stringify({ productId: familyCards[0]?.productId ?? product.id, urlsInOrder: familyStripUrls })
        })
        familyMediaChanged = result.changedProductCount > 0
        setFamilyMediaOrderDirty(false)
      }
      if (editProductId && sizeChartUrl !== initialSizeChartUrl.current) {
        initialSizeChartUrl.current = sizeChartUrl
        completed.push(sizeChartUrl ? 'beden tablosu' : 'beden tablosu işareti kaldırıldı')
      } else if (familyChartMarked) completed.push('beden tablosu diğer renklere')
      mediaStep = false
      if (editProductId) {
        const unpicturedColours = newColoursWithoutPictures(rows, new Set((productToEdit.data?.variants ?? []).map(variant => variant.id)), isColorOptionName)
        if (unpicturedColours.length) warnings.push(`Yeni ${unpicturedColours.length > 1 ? 'renklere' : 'renge'} görsel atanmadı: ${unpicturedColours.join(', ')}. Bu renk yalnız renksiz görselleri (beden tablosu, yeni yüklenen ve atanmamış görseller) alır. Seçeneklere Görsel Ata'dan bu renge görsellerini atayın, yoksa pazaryerine kendi görselleri olmadan gider.`)
      }
      if (editProductId ? galleryWriteCount > 0 || mediaFiles.length > 0 : mediaUrls.length || mediaFiles.length) completed.push(cardColourLabels.length ? `renk kartlarının görselleri (${cardColourLabels.join(', ')})` : 'görseller')
      if (removedFamilyMediaCount) completed.push(`modelden kaldırılan ${removedFamilyMediaCount} görsel`)
      if (familyMediaChanged && !galleryWriteCount) completed.push('modelin görsel sırası')
      // A draft is never sent: no channel price is written for it and nothing is published. A marketplace whose card
      // shows a required gap is never sent either (its toggle goes off with the gap; this holds if one slipped through).
      const gapCards = platformUpdate ? {} : blockedPublishCards
      const targetConnectionIds = createOnly || saveDraft ? [] : platformUpdate?.connectionIds ?? selectedChannelIds.filter(id => !gapCards[id])
      // Said again by name when the send reaches Trendyol (one picture list per colour content); nothing is changed silently.
      if (trendyolMixedColours.length && targetConnectionIds.some(id => connections.data?.items.find(connection => connection.id === id)?.platformCode === 'TRENDYOL'))
        warnings.push(`Trendyol: ${trendyolMixedColours.join(', ')} bedenleri farklı görsellerle atanmış; Trendyol bir rengin bedenlerini tek görsel listesiyle tutar, bu renklerin güncellemesi reddedilebilir.`)
      // Only rows whose stock the user changed are sent, as absolute values against the stock loaded into the form;
      // a stock changed meanwhile (order, return, another tab) is refused for that row instead of being overwritten.
      // A new variant's stock went with the variant itself (its Açılış Stoğu).
      const stockResult = await applyVariantStockSets(editorStockUpdates(rows, product.variants), update => setVariantOnHand(update, 'Ürün düzenleme stoğu'))
      if (stockResult.failed.length) warnings.push(`Stok kaydedilemedi: ${stockResult.failed.map(item => `${item.sku}: ${item.message}`).join('; ')}`)
      // A marketplace switched to or from "Özel Fiyat" stores the choice on the card's offers there, even when no price
      // is written to it (no price changes, nothing goes to the marketplace).
      if (editProductId) {
        const choiceChanged = (connections.data?.items ?? []).map(connection => connection.id).filter(connectionId => (connectionId in channelPricing) !== initialCustomChannelIds.current.has(connectionId))
        for (const connectionId of choiceChanged) {
          const followsPanelPrice = !(connectionId in channelPricing)
          const variantIds = panelFollowChoiceVariantIds(rows.map(row => ({ id: row.key, platformStatuses: row.platformStatuses })), connectionId, followsPanelPrice)
          if (variantIds.length) await hubApi('/channel-offers/panel-price-follow', { method: 'PUT', body: JSON.stringify({ connectionId, variantIds, followsPanelPrice }) })
        }
        initialCustomChannelIds.current = new Set(Object.keys(channelPricing))
      }
      const connectionPlatformName = (connectionId: string) => publishPlatformName(connections.data?.items.find(item => item.id === connectionId)?.platformCode ?? 'Pazaryeri')
      // Every (variant, store) price is its own row, so they are written a few at a time instead of one by one.
      // "Platformlarda Güncelle" writes a store's prices only when its send carries a price or the stock: a pick of the
      // pictures alone writes none (live 10 Nov 2026, MZ052BOD).
      const offerConnectionIds = platformUpdate
        ? targetConnectionIds.filter(connectionId => platformUpdateWritesOffers(connections.data?.items.find(item => item.id === connectionId)?.platformCode ?? '', platformUpdate.fields))
        : targetConnectionIds
      const offerWrites = product.variants.flatMap(variant => {
        const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
        return row ? offerConnectionIds.map(connectionId => ({ variant, row, connectionId })) : []
      })
      let offersWritten = 0
      const unpricedOfferSkus = new Set<string>()
      await mapWithConcurrency(offerWrites, channelOfferWriteConcurrency, async ({ variant, row, connectionId }) => {
        const pricing = channelPriceDraft(connectionId)
        const platform = row.platformStatuses?.find(item => item.connectionId === connectionId)
        const variantPricing = variantChannelPricing[`${row.key}:${connectionId}`]
        // A marketplace that follows the panel takes this variant's own panel price; the card price is only the price
        // of a marketplace switched to "Özel Fiyat".
        const offerPrices = editorChannelOfferPrices({
          custom: connectionId in channelPricing,
          panel: { listPrice: row.listPrice, salePrice: row.salePrice },
          card: { listPrice: Number(pricing.listPrice), salePrice: Number(pricing.salePrice) },
          cardEdited: channelPriceEdited(connectionId),
          variantPrice: !platformUpdate && variantPricing ? { listPrice: Number(variantPricing.listPrice), salePrice: Number(variantPricing.salePrice) } : null,
          offer: platform,
          takesListPrice: channelTakesListPrice(connections.data?.items.find(item => item.id === connectionId)?.platformCode ?? platform?.platformCode)
        })
        // A variant without a panel price (0 or empty in the variant table, often a colour imported without one) has
        // no price to write: the server refuses a sale price of zero, and one such row used to stop the whole save
        // after the card was written (owner 7.11.2026, MZ052BOD: "Satış fiyatı sıfırdan büyük olmalıdır").
        if (!offerHasSalePrice(offerPrices)) { unpricedOfferSkus.add(row.sku); return }
        const offerCommand = { connectionId, variantId: variant.id, listPrice: offerPrices.listPrice, salePrice: offerPrices.salePrice, followsPanelPrice: offerPrices.followsPanelPrice, currency: platform?.currency ?? form.currency ?? 'TRY', vatRate: Number(form.vatRate || platform?.vatRate || 0), vatInclusion: form.vatIncluded || platform?.vatInclusion || 'INCLUDED', roundingMode: platform?.roundingMode ?? 'HALF_EVEN', safetyStock: Number(form.safetyStock || platform?.safetyStock || 0), status: 'ACTIVE', reason: editorOfferPriceReason(platformUpdate) }
        // An offer the variant already has is written at the version this form loaded (PATCH + If-Match), so a price
        // changed meanwhile elsewhere is refused instead of overwritten; one the write would leave as it is is skipped.
        // POST only creates: the server refuses it over an offer this form did not know of.
        try {
          if (platform?.offerId) {
            if (platform.offerVersion == null) throw new Error(`${row.sku}: platform fiyat sürümü eksik; fiyat güncellenmedi.`)
            if (!editorOfferUnchanged(platform, offerCommand)) await hubApi(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(offerCommand) })
          } else {
            await hubApi('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(offerCommand) })
          }
        } catch (reason) {
          if (reason instanceof ApiRequestError && (reason.status === 409 || reason.status === 412)) throw new Error(`${row.sku}: ${connectionPlatformName(connectionId)} fiyatı bu form açıkken başka bir işlemle değişti; fiyat yazılmadı. Sayfayı yenileyip tekrar deneyin.`)
          throw reason
        }
        offersWritten++
        if (offerWrites.length > channelOfferWriteConcurrency) showProgress(mediaSaveProgressMessage('prices', offersWritten, offerWrites.length))
      })
      const unpricedWarning = unpricedOfferWarning(unpricedOfferSkus)
      if (unpricedWarning) warnings.push(unpricedWarning)
      // An edit names the stock only when a changed stock was written; a new product names the stock it opened with.
      if (stockResult.saved.length > 0 || rows.some(row => !existingVariantIds.has(row.key) && editorOpeningStock(row.stock) > 0)) completed.push('stok')
      if (!editProductId && targetConnectionIds.length) completed.push('kanal fiyatları')
      const publicationConnectionIds = productPublicationTargets(saveAndStay, targetConnectionIds)
      // A card already matched there is not sent again: its links are read from the latest product (a status check or a
      // store lookup since the form was filled), then from the saved product when it carries them.
      const linkRowsBySku = new Map(withLatestVariantLinks(withLatestVariantLinks(rows, productToEdit.data?.variants), product.variants).map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
      await sendToMarketplaces({ publicationConnectionIds, connectionPlatformName, platformUpdate, showFeedback, updates, connections, product, productToEdit, linkRowsBySku, followedJobs, form, client, publications, publicationErrors })
      const draftMissingItems = draftMissingSummary(draftMissing)
      const result: ProductSaveFeedback = saveDraft
        ? {
            title: 'Taslak Kaydedildi',
            kind: warnings.length ? 'error' : 'success',
            persistent: autoDraft || draftMissingItems.count > 0 || warnings.length > 0,
            lines: [
              'Ürün taslak olarak kaydedildi; pazaryerlerine gönderilmez ve stok sayımlarına girmez.',
              ...(autoDraft ? [`Zorunlu alanlar boş olduğu için taslak kaldı: ${missingRequired.map(field => requiredEditorFieldLabels[field]).join(', ')}.`] : []),
              ...warnings,
              ...(draftMissingItems.count ? [`Satışa açmadan önce tamamlanacak ${draftMissingItems.count} eksik: ${draftMissingItems.lines.join(' ')}`] : [])
            ]
          }
        : productSaveFeedback({
            created: !editProductId,
            productChanged: productChangedAtSubmit,
            savedParts: completed,
            versionRetried: productVersionRetryUsed,
            saveWarnings: warnings,
            publishRequested: !saveAndStay && !createOnly,
            publications,
            publicationErrors,
            gapSkipped: chosenGapSkipped(connections.data?.items ?? [], selectedChannelIds, gapCards, connection => connectionPlatformName(connection.id)),
            updates: platformUpdate ? updates : undefined
          })
      const message = result.lines.join(' ')
      setRemovedVariantIds([]); setRemovedVariantRows([]); setReplacedPlaceholderIds([])
      if (editProductId) setVariantRows(current => rekeySavedVariantRows(current, product.variants))
      setVariantCodeConflicts({})
      mediaFilesKeptOnReseed.current = null
      setNotice(createOnly ? 'Ürün oluşturuldu. Ürün sayfası açılıyor.' : message)
      // The saved form is the new clean state; the refetch below may refill it from the server.
      allowReseedUntil.current = Date.now() + 10_000
      setSnapshotRequest(value => value + 1)
      await client.invalidateQueries({ queryKey: queryKeys.products() })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: queryKeys.product(editProductId) })
        await productToEdit.refetch()
        editorSaveInFlight.current = false
        // The uploaded files leave the queue in the same render as the refill that shows their stored copies (and the
        // strip goes back to the latest product then): dropped before the refetch, the strip stood half empty
        // meanwhile. Gone from the queue, the next Kaydet does not send them again even if the refill is held back.
        setMediaFiles(current => mediaFilesLeftAfterFailedSave(current, uploadedMediaFileIds))
        setProductAtSave(null)
        setReseedRequest(value => value + 1)
      }
      // A new product's files stay on "Yeni Ürün Ekle" until it moves to the product's own page (a new editor that
      // shows the stored copies), so its strip never stands empty meanwhile.
      // One line per marketplace; a refused marketplace stays on screen until it is closed, so it is not lost among
      // later toasts.
      if (createOnly) operationToasts.dismiss(productSaveToastKey)
      // A "sending" toast with no job to follow (the server named none) does not spin forever.
      else setFeedback({ message, kind: result.kind, persistent: result.persistent && (result.title !== 'Aktarım Başladı' || followedJobs.length > 0), title: result.title, lines: result.lines, durationMs: result.durationMs, key: productSaveToastKey, context: productCreated ? { subject: productNotificationSubject({ id: productCreated.id, name: form.title, modelCode: form.modelCode }) } : undefined })
      // "Aktarım Başladı" turns into how the jobs ended; a toast that already names a refusal keeps its place and the
      // result comes below it. The result also goes to the notification center, so it is not lost if the page is left.
      if (followedJobs.length && !createOnly) {
        const resultKey = result.kind === 'error' ? `${productSaveToastKey}-result` : productSaveToastKey
        const subject = productNotificationSubject({ id: product.id, name: form.title, modelCode: form.modelCode })
        const operation = platformUpdate ? 'Platformlarda Güncelle' : 'Yayınla'
        void followJobOutcomes(followedJobs, outcomeTarget(followedJobs), (outcome, interim) => operationToasts.showDetached({
          title: outcome.title, message: outcome.lines.join(' '), lines: outcome.lines, kind: outcome.kind,
          persistent: outcome.kind === 'error', durationMs: interim ? 8000 : 6000, key: resultKey,
          context: { subject, operation, jobId: followedJobs.length === 1 ? followedJobs[0].jobId : undefined }
        }))
      }
      // A new product or a copy opens on its own page once created, whichever button saved it: there it saves as an
      // edit and its publication progress shows. A publish opens the publication step; the copy source is dropped.
      if (productCreated && !editProductId) {
        // The save toast of a publish already went to the history; a bare create (its toast closed) is recorded here.
        if (createOnly) appendNotification({ title: 'Ürün Oluşturuldu', message: 'Ürün kataloğa eklendi.', kind: 'success', subject: productNotificationSubject({ id: productCreated.id, name: form.title, modelCode: form.modelCode }) })
        leavingAfterSave.current = true
        navigate(`/products/${productCreated.id}`, { replace: true, state: createOnly || saveDraft ? null : { wizardStep: 2 } })
      }
    } catch (reason) {
      // The server refused a variant delete (order or stock history, a pending job): the whole save was refused, so the
      // removed rows go back into the table and nothing waits to be deleted.
      const variantDeleteRefused = !productCreated && isVariantDeleteRefusal(reason)
      if (variantDeleteRefused) {
        const refusedRows = removedVariantRows
        setVariantRows(current => restoreRemovedVariantRows(current, refusedRows))
        setRemovedVariantIds([]); setRemovedVariantRows([])
      }
      const reasonMessage = reason instanceof Error ? reason.message : 'Kayıt tamamlanamadı.'
      const message = variantDeleteRefused ? `${reasonMessage} ${VARIANT_DELETE_REFUSED_HINT}` : reasonMessage
      const feedbackMessage = productCreated ? mediaStep ? `Ürün bilgileri kaydedildi ancak görseller kaydedilemedi: ${message} Görselleri kontrol edip Kaydet'e yeniden basın.` : `Ürün kaydedildi ancak sonraki işlem tamamlanamadı: ${message}` : message
      setError(reason); setNotice(feedbackMessage)
      setFeedback({ message: feedbackMessage, kind: 'error', persistent: true, title: productCreated ? 'İşlem Yarım Kaldı' : editProductId ? 'Ürün Kaydedilemedi' : 'Ürün Oluşturulamadı', key: productSaveToastKey, context: productCreated ? { subject: productNotificationSubject({ id: productCreated.id, name: form.title, modelCode: form.modelCode }) } : undefined })
      if (!productCreated) { setFieldErrors(editorFieldErrors(reason)); setVariantCodeConflicts(variantCodeConflictsFrom(reason)) }
      // WEB-SAYFALAR-2: another colour card of the model changed while this form was open and nothing was saved; the
      // stale banner offers to reload the model, the unsaved edits stay until then.
      if (!productCreated && reason instanceof ApiRequestError && reason.code === 'FAMILY_CHANGED')
        setStaleProductVersion(seededProduct.current?.version ?? productToEdit.data?.version ?? 0)
      // The product itself was saved before a later step (media, a channel offer, the panel price choice) failed: its
      // new version is read again and the form may refill from it, so the next try does not take its own save for
      // another operation's change. Only the files this try uploaded leave the queue.
      if (productCreated && editProductId) {
        const keptFiles = mediaFilesLeftAfterFailedSave(mediaFiles, uploadedMediaFileIds)
        mediaFilesKeptOnReseed.current = keptFiles
        allowReseedUntil.current = Date.now() + 10_000
        await Promise.all([client.invalidateQueries({ queryKey: queryKeys.products() }), client.invalidateQueries({ queryKey: queryKeys.product(editProductId) })]).catch(() => undefined)
        await productToEdit.refetch().catch(() => undefined)
        editorSaveInFlight.current = false
        setMediaFiles(keptFiles)
        setProductAtSave(null)
        setReseedRequest(value => value + 1)
      }
      if (productCreated && !editProductId) {
        // The product's page is a new editor: the files not stored yet and each variant's images not written wait for
        // it, so it shows them as unsaved changes instead of losing them.
        const queuedFiles = mediaFilesLeftAfterFailedSave(mediaFiles, uploadedMediaFileIds)
        const queuedFileIds = new Set(queuedFiles.map(item => item.id))
        const rowsBySku = new Map(submittedRows.map(row => [row.sku.trim().toLocaleUpperCase('tr-TR'), row]))
        keepPendingCreateMedia(productCreated.id, {
          files: queuedFiles,
          variantMediaRefs: Object.fromEntries(productCreated.variants.flatMap(variant => {
            const row = rowsBySku.get(variant.sku.trim().toLocaleUpperCase('tr-TR'))
            const refs = row && !writtenVariantMediaIds.has(variant.id) ? pendingVariantMediaRefs(row.mediaRefs, storedUrlByFileId, queuedFileIds) : []
            return refs.length ? [[variant.id, refs]] : []
          }))
        })
        leavingAfterSave.current = true
        navigate(`/products/${productCreated.id}`)
      }
    } finally { editorSaveInFlight.current = false; setProductAtSave(null); setSubmitting(false) }
  }

  return { submit }
}
export type EditorSubmitResult = ReturnType<typeof useEditorSubmit>
