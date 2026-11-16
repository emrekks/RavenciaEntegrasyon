// Kaydet, Ürünü Oluştur, Taslak, Yayınla and Platformlarda Güncelle: the product write and the steps after it.
// Owns: Kaydet awaits a running picture assignment save, names the seeded version in If-Match, marks the save in
// flight so a refetch meanwhile is no refill, and asks for the reseed after its own refetch. The steps themselves are
// separate (WEB-SAYFALAR-10): the product write (submit-product-payload.ts), the pictures (submit-media-step.ts), the
// stock (submit-stock-step.ts), the marketplace prices (submit-offer-writes.ts) and the sends (editor-marketplace-sends.ts).
import { type FormEvent } from 'react'
import { ApiRequestError } from '../../../shared/api'
import { appendNotification, productNotificationSubject } from '../../../shared/notifications'
import { followJobOutcomes, outcomeTarget, type FollowedJob } from '../../../shared/job-outcome'
import { isVariantDeleteRefusal, restoreRemovedVariantRows, VARIANT_DELETE_REFUSED_HINT } from '../variant-row-removal'
import { mediaFilesLeftAfterFailedSave, newColoursWithoutPictures } from './product-media-save'
import { keepPendingCreateMedia, pendingVariantMediaRefs } from './pending-create-media'
import { withLatestVariantLinks } from './publish-card-variants'
import { requiredEditorFieldLabels } from './required-fields'
import { canSaveAsDraft, draftMissingSummary, isDraftStatus, variantCodeRulesStopSave } from '../product-draft'
import { editorFieldErrors, editorOpeningStock, rekeySavedVariantRows, variantCodeConflictsFrom } from '../product-editor-state'
import { chosenGapSkipped, productPublicationTargets, productSaveFeedback, type MarketplacePublishOutcome, type MarketplaceUpdateOutcome, type ProductSaveFeedback } from '../product-publication-submit'
import { Product, isColorOptionName } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { VariantDraft } from './editor-types'
import { productSaveToastKey, PLACEHOLDER_KEPT_MESSAGE, publishPlatformName } from './editor-helpers'
import { readSubmitIntent } from './submit-intent'
import { requiredFieldsOutcome } from './validate-editor'
import { buildCreatePayload, buildGlobalAttributes, buildUpdatePayload, optionRequirementsForSubmit, placeholderIdsToDelete, saveProductCreate, saveProductUpdate, savedProductFields, type ProductPayloadInput } from './submit-product-payload'
import { mediaStepParts, newMediaStepProgress, rowsBySku, runMediaStep, skuKey } from './submit-media-step'
import { runStockStep } from './submit-stock-step'
import { offerConnectionIdsFor, runOfferWrites, runPanelFollowChoiceWrites } from './submit-offer-writes'
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
  const { editProductId, publishOnly, client, navigate, setError, setNotice, operationToasts, setFeedback, setSubmitting, calculateDesi, form, attributeSelections, attributeTextValues, variantAttributeIds, setVariantRows, selectedChannelIds, blockedPublishCards, channelPricing, variantChannelPricing, wizardStep, setWizardStep, pendingPlatformUpdate, mediaFiles, setMediaFiles, pendingFamilyMediaRemovals, setPendingFamilyMediaRemovals, familyMediaOrderDirty, setFamilyMediaOrderDirty, sizeChartUrl, initialSizeChartUrl, sizeChartBeforeFamilyRemoval, chartKey, removedVariantIds, setRemovedVariantIds, removedVariantRows, setRemovedVariantRows, replacedPlaceholderIds, setReplacedPlaceholderIds, requestBaseline, webColorAutoEnabled, manualWebColorValueId, setStaleProductVersion, allowReseedUntil, editorSaveInFlight, seededProduct, setProductAtSave, setVariantCodeConflicts, mediaFilesKeptOnReseed, initialCustomChannelIds, setFieldErrors, productCreateKey, leavingAfterSave, initialEditModelCode, initialEditVariantMediaRefs, showFeedback, mediaAutoSave, editorDirty, productToEdit, connections, requirements, fallbackListPrice, fallbackSalePrice, missingRequiredFields, desi, mediaUrls, mappedRequirements, webColorRequirement, setReseedRequest, draftCodeSaveBlock, codeRuleSaveBlock, modelCodeHolder, blockedByVariantCodeRules, draftMediaKeys, pendingRemovalMediaKeys, mediaStrip, trendyolMixedColours, channelPriceDraft, channelPriceEdited, rowsForSubmit, validate, draftBlockingIssues, stopOnRequiredFields, vatRateIssue } = c
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const platformUpdate = pendingPlatformUpdate.current
    pendingPlatformUpdate.current = null
    const submitter = (event.nativeEvent as SubmitEvent).submitter
    const intent = readSubmitIntent(submitter, new FormData(event.currentTarget).get('intent'))
    // Taslak Olarak Kaydet (product-draft.ts): saves the card as it is, on a new card or a draft only, and sends nothing.
    const explicitDraft = !platformUpdate && intent.draft && canSaveAsDraft(Boolean(editProductId), productToEdit.data?.status)
    const editingDraft = Boolean(editProductId) && isDraftStatus(productToEdit.data?.status)
    // Required fields (required-fields.ts; owner, 10 Nov 2026). With one empty, Kaydet / Ürünü Oluştur on a new card or a
    // draft keeps the card as a draft (never sent; the toast names what is missing); publishing, Yayınlamaya Devam Et and
    // a platform update stop with each field framed red. A card on sale or closed is not turned into a draft (the server
    // moves the whole colour family with a status change): its save stops the same way until the fields are filled.
    const plainSave = !platformUpdate && !explicitDraft && (intent.save || (!editProductId && intent.create))
    const { missing: missingRequired, autoDraft, stop } = requiredFieldsOutcome(missingRequiredFields, { explicitDraft, plainSave, draftAllowed: canSaveAsDraft(Boolean(editProductId), productToEdit.data?.status) })
    if (stop) { stopOnRequiredFields(missingRequired, Boolean(editProductId) && !editingDraft && plainSave); return }
    const saveDraft = explicitDraft || autoDraft
    const saveAndStay = saveDraft || intent.save
    const createOnly = !editProductId && !saveDraft && intent.create
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
    // the field is, with its message under it. A draft may be saved without it (vatRateIssue: validate-editor.ts).
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
    setError(undefined); setFieldErrors({}); setNotice(''); showFeedback(pendingTitle, pendingMessage, 'info', { persistent: true, key: productSaveToastKey }); setSubmitting(true); editorSaveInFlight.current = true; setProductAtSave(seededProduct.current ?? productToEdit.data ?? null); let productCreated: Product | undefined; let mediaStep = false
    // What the media step got done (pending-create-media.ts): a create that stops after the product exists hands the
    // rest to the product's page.
    const mediaProgress = newMediaStepProgress()
    const { uploadedMediaFileIds, storedUrlByFileId, writtenVariantMediaIds } = mediaProgress
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
      const optionRequirements = optionRequirementsForSubmit(submitRequirements)
      if (requireCompleteCatalog && form.categoryId && requirements.isError && !requirements.data) throw new Error('Kategori özellikleri alınamadı. Önce kategori eşleştirmesini kontrol edin.')
      // A draft is stopped only by values the server refuses (clashing codes, malformed prices or image links); what
      // the normal save would ask for comes back as the list of what to complete before the card goes on sale.
      let draftMissing = ''
      let rows: VariantDraft[]
      if (saveDraft) {
        try { validate(rowsForSubmit(true), true, true) } catch (reason) { draftMissing = reason instanceof Error ? reason.message : '' }
        if (vatRateIssue) draftMissing = `${vatRateIssue} ${draftMissing}`.trim()
        rows = rowsForSubmit(false)
        const blocking = draftBlockingIssues(rows)
        if (blocking.length) throw new Error(blocking.join(' '))
      } else {
        rows = rowsForSubmit(requireCompleteCatalog); validate(rows, requireCompleteCatalog, !createOnly)
      }
      submittedRows = rows
      // Do not overwrite existing assignments while a newly selected category's requirements are still loading (or
      // failed). The edit form may be saved without optional mapping data.
      const payloadInput: ProductPayloadInput = {
        form,
        saved: savedProductFields(form, saveDraft, vatRateIssue, new Date()),
        fallbackListPrice,
        fallbackSalePrice,
        webColor: { autoEnabled: webColorAutoEnabled, manualValueId: manualWebColorValueId },
        globalAttributes: buildGlobalAttributes({ requirements: mappedRequirements, webColorRequirement, variantAttributeIds, attributeSelections, attributeTextValues }),
        rows,
        variant: { form, calculateDesi, calculatedDesi: desi, optionRequirements }
      }
      // The product the form was filled from (WEB-SAYFALAR-1): its version, variants, model codes and desi are what this
      // save is made against; a newer refetch held back by the stale banner is not.
      const loadedProduct = editProductId ? seededProduct.current ?? productToEdit.data : undefined
      const existingVariantIds = new Set(loadedProduct?.variants.map(variant => variant.id) ?? [])
      const placeholderIds = placeholderIdsToDelete(replacedPlaceholderIds, loadedProduct, rows, removedVariantIds)
      let product: Product
      let productVersionRetryUsed = false
      if (loadedProduct) {
        const payload = buildUpdatePayload({ ...payloadInput, persistAttributes: Boolean(form.categoryId && requirements.isSuccess), removedVariantIds, placeholderIdsToDelete: placeholderIds, initialModelCode: initialEditModelCode.current }, loadedProduct)
        const written = await saveProductUpdate(loadedProduct, payload)
        product = written.product
        productVersionRetryUsed = written.versionRetried
      } else {
        product = await saveProductCreate(productCreateKey, buildCreatePayload(payloadInput))
      }
      productCreated = product; const completed: string[] = []; const warnings: string[] = []
      if (placeholderIds.some(id => product.variants.some(variant => variant.id === id))) warnings.push(PLACEHOLDER_KEPT_MESSAGE)
      const publications: MarketplacePublishOutcome[] = []
      const publicationErrors: Array<{ platformName: string; message: string }> = []
      const updates: MarketplaceUpdateOutcome[] = []
      // The jobs this press started, followed until they say how they ended (aktarıldı / aktarılamadı, with the reason).
      const followedJobs: FollowedJob[] = []
      // The steps after the product (media, stock, channel prices, publication) can take a while; the progress toast names them.
      if (!editProductId) showFeedback('Ürün Oluşturuldu', 'Ürün oluşturuldu; görseller, stok ve fiyatlar kaydediliyor…', 'info', { persistent: true, key: productSaveToastKey })
      const progressTitle = editProductId ? pendingTitle : 'Ürün Oluşturuldu'
      const showProgress = (message: string) => showFeedback(progressTitle, message, 'info', { persistent: true, key: productSaveToastKey })
      // O8: from here the card is saved; a picture step that fails says so, the card's data stays saved.
      mediaStep = true
      const mediaResult = await runMediaStep({
        editing: Boolean(editProductId),
        product,
        rows,
        title: form.title,
        mediaUrls,
        mediaFiles,
        mediaStrip,
        draftMediaKeys,
        pendingRemovalMediaKeys,
        pendingFamilyMediaRemovals,
        familyMediaOrderDirty,
        chartKey,
        sizeChartUrl,
        initialSizeChartUrl: initialSizeChartUrl.current,
        initialVariantMediaRefs: initialEditVariantMediaRefs.current,
        showProgress,
        onFamilyRemovalsWritten: () => { setPendingFamilyMediaRemovals([]); sizeChartBeforeFamilyRemoval.current = null },
        onFamilyOrderWritten: () => setFamilyMediaOrderDirty(false)
      }, mediaProgress)
      const sizeChartChanged = sizeChartUrl !== initialSizeChartUrl.current
      const mediaParts = mediaStepParts({ editing: Boolean(editProductId), sizeChartChanged, sizeChartUrl, mediaUrlCount: mediaUrls.length, mediaFileCount: mediaFiles.length }, mediaResult)
      if (editProductId && sizeChartChanged) initialSizeChartUrl.current = sizeChartUrl
      completed.push(...mediaParts.chart)
      mediaStep = false
      if (editProductId) {
        const unpicturedColours = newColoursWithoutPictures(rows, new Set((productToEdit.data?.variants ?? []).map(variant => variant.id)), isColorOptionName)
        if (unpicturedColours.length) warnings.push(`Yeni ${unpicturedColours.length > 1 ? 'renklere' : 'renge'} görsel atanmadı: ${unpicturedColours.join(', ')}. Bu renk yalnız renksiz görselleri (beden tablosu, yeni yüklenen ve atanmamış görseller) alır. Seçeneklere Görsel Ata'dan bu renge görsellerini atayın, yoksa pazaryerine kendi görselleri olmadan gider.`)
      }
      completed.push(...mediaParts.rest)
      // A draft is never sent: no channel price is written for it and nothing is published. A marketplace whose card
      // shows a required gap is never sent either (its toggle goes off with the gap; this holds if one slipped through).
      const gapCards = platformUpdate ? {} : blockedPublishCards
      const targetConnectionIds = createOnly || saveDraft ? [] : platformUpdate?.connectionIds ?? selectedChannelIds.filter(id => !gapCards[id])
      const connectionItems = connections.data?.items ?? []
      // Said again by name when the send reaches Trendyol (one picture list per colour content); nothing is changed silently.
      if (trendyolMixedColours.length && targetConnectionIds.some(id => connections.data?.items.find(connection => connection.id === id)?.platformCode === 'TRENDYOL'))
        warnings.push(`Trendyol: ${trendyolMixedColours.join(', ')} bedenleri farklı görsellerle atanmış; Trendyol bir rengin bedenlerini tek görsel listesiyle tutar, bu renklerin güncellemesi reddedilebilir.`)
      const stockResult = await runStockStep(rows, product.variants)
      if (stockResult.warning) warnings.push(stockResult.warning)
      if (editProductId) initialCustomChannelIds.current = await runPanelFollowChoiceWrites({ connections: connectionItems, channelPricing, initialCustomChannelIds: initialCustomChannelIds.current, rows })
      const connectionPlatformName = (connectionId: string) => publishPlatformName(connections.data?.items.find(item => item.id === connectionId)?.platformCode ?? 'Pazaryeri')
      const { unpricedWarning } = await runOfferWrites({ product, rows, connectionIds: offerConnectionIdsFor(platformUpdate, targetConnectionIds, connectionItems), connections: connectionItems, form, platformUpdate, channelPricing, variantChannelPricing, channelPriceDraft, channelPriceEdited, connectionPlatformName, showProgress })
      if (unpricedWarning) warnings.push(unpricedWarning)
      // An edit names the stock only when a changed stock was written; a new product names the stock it opened with.
      if (stockResult.saved.length > 0 || rows.some(row => !existingVariantIds.has(row.key) && editorOpeningStock(row.stock) > 0)) completed.push('stok')
      if (!editProductId && targetConnectionIds.length) completed.push('kanal fiyatları')
      const publicationConnectionIds = productPublicationTargets(saveAndStay, targetConnectionIds)
      // A card already matched there is not sent again: its links are read from the latest product (a status check or a
      // store lookup since the form was filled), then from the saved product when it carries them.
      const linkRowsBySku = new Map(withLatestVariantLinks(withLatestVariantLinks(rows, productToEdit.data?.variants), product.variants).map(row => [skuKey(row.sku), row]))
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
      requestBaseline()
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
        const submittedBySku = rowsBySku(submittedRows)
        keepPendingCreateMedia(productCreated.id, {
          files: queuedFiles,
          variantMediaRefs: Object.fromEntries(productCreated.variants.flatMap(variant => {
            const row = submittedBySku.get(skuKey(variant.sku))
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
