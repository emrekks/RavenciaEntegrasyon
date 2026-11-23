// Pazaryerlerine Özel Fiyat and the variant platform price window, with their saves.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { knownPlatformName } from '../../../shared/platform-names'
import { hubApi } from '../../../shared/api'
import { failedWithHttpAnswer, newIdempotencyKey } from '../../../shared/idempotency'
import { channelTakesListPrice, typedOfferPriceChoice } from '../quick-price-edit'
import { editorVatRateIssue } from '../product-editor-state'
import { channelOfferPostKey, emptyChannelPricingDraft, rowsWithSavedOffer, type ChannelPricingDraft, type SavedChannelOffer } from './marketplace-channel-pricing'
import { VariantPlatformStatus } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { VariantDraft } from './editor-types'
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
import { editorCount, parseEditorNumber } from './editor-number'
import { priceInputNumber } from './card-prices'
import { priceFieldText } from '../quick-price-edit'

export function useChannelPricing(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult & VariantCodeActionsResult & EditorMediaActionsResult & PlatformUpdateResult) {
  const { editProductId, client, setNotice, form, variantRows, setVariantRows, channelPricing, setChannelPricing, setVariantChannelPricing, offerPostKeys, channelPricingBase, setFieldErrors, variantPlatformPricing, setVariantPlatformPricing, variantPlatformPricingDraft, setVariantPlatformPricingDraft, setVariantPlatformPricingSaving, showFeedback, productToEdit, fallbackListPrice, fallbackSalePrice, adoptOwnProductWrite, variantPricingPlatforms } = c
  // A marketplace offer keeps the VAT it has; a new one takes the card's KDV Oranı, and with that field empty it is
  // refused (never priced at 0 %).
  function offerVatRate(known: number | null | undefined): number {
    if (known != null) return known
    const issue = editorVatRateIssue(form.vatRate)
    if (issue) {
      setFieldErrors(current => ({ ...current, vatRate: issue }))
      throw new Error(issue)
    }
    return parseEditorNumber(form.vatRate)
  }
  function channelPriceDraft(connectionId: string): ChannelPricingDraft {
    return channelPricing[connectionId] ?? { listPrice: priceFieldText(fallbackListPrice), salePrice: priceFieldText(fallbackSalePrice) }
  }
  function toggleCustomChannelPrice(connectionId: string) {
    if (!(connectionId in channelPricing)) channelPricingBase.current = { ...channelPricingBase.current, [connectionId]: emptyChannelPricingDraft }
    setChannelPricing(current => {
      if (connectionId in current) { const { [connectionId]: _removed, ...rest } = current; return rest }
      return { ...current, [connectionId]: emptyChannelPricingDraft }
    })
  }
  // True when the person changed the card's own price since it was filled or switched on.
  function channelPriceEdited(connectionId: string) {
    const base = channelPricingBase.current[connectionId]
    const draft = channelPricing[connectionId]
    if (!base || !draft) return true
    // Text first (O2): an untouched box is never edited, a stored 99.999 included (its text reads as NaN by the typed rule).
    if (base.listPrice === draft.listPrice && base.salePrice === draft.salePrice) return false
    return priceInputNumber(base.listPrice) !== priceInputNumber(draft.listPrice) || priceInputNumber(base.salePrice) !== priceInputNumber(draft.salePrice)
  }
  function updateChannelPrice(connectionId: string, field: keyof ChannelPricingDraft, value: string) {
    setChannelPricing(current => ({ ...current, [connectionId]: { ...(current[connectionId] ?? emptyChannelPricingDraft), [field]: value } }))
  }
  function platformDisplayName(platform: VariantPlatformStatus) {
    const code = platform.platformCode.trim().toLocaleUpperCase('tr-TR')
    return knownPlatformName(code) ?? platform.platform
  }
  function openVariantPlatformPricing(row: VariantDraft, platform: VariantPlatformStatus) {
    setVariantPlatformPricing({ rowKey: row.key, platform })
    setVariantPlatformPricingDraft({
      listPrice: priceFieldText(platform.listPrice ?? row.listPrice ?? fallbackListPrice),
      salePrice: priceFieldText(platform.salePrice ?? row.salePrice ?? fallbackSalePrice)
    })
    requestAnimationFrame(() => {
      document.querySelector(`[data-variant-row-key="${row.key}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' })
    })
  }
  async function saveVariantPlatformPricingBulk(targetPlatform?: VariantPlatformStatus, targetDraft?: ChannelPricingDraft) {
    if (!variantPlatformPricing) return
    const selectedPlatform = targetPlatform ?? variantPlatformPricing.platform
    if (!selectedPlatform.connectionId) {
      showFeedback('Fiyat Bilgisi Alınamadı', 'Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const activeDraft = targetDraft ?? variantPlatformPricingDraft
    const listPrice = priceInputNumber(activeDraft.listPrice)
    const salePrice = priceInputNumber(activeDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Geçersiz Fiyat', 'Liste fiyatı satış fiyatından küçük olamaz.', 'error')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const savedOffers: Array<{ rowKey: string; platform: VariantPlatformStatus; saved: { id: string; version: number; listPrice: number; salePrice: number; currency: string } }> = []
      for (const row of variantRows) {
        const platform = row.platformStatuses?.find(item => item.connectionId === selectedPlatform.connectionId)
        if (!platform?.connectionId) continue
        const body = {
          listPrice,
          salePrice,
          currency: platform.currency ?? selectedPlatform.currency ?? form.currency ?? 'TRY',
          vatRate: offerVatRate(platform.vatRate ?? selectedPlatform.vatRate),
          vatInclusion: platform.vatInclusion ?? selectedPlatform.vatInclusion ?? form.vatIncluded,
          roundingMode: platform.roundingMode ?? selectedPlatform.roundingMode ?? 'HALF_EVEN',
          safetyStock: platform.safetyStock ?? selectedPlatform.safetyStock ?? editorCount(form.safetyStock || 0),
          status: 'ACTIVE',
          reason: `${platformDisplayName(platform)} toplu varyant fiyatı`
        }
        const saved = platform.offerId && platform.offerVersion != null
          ? await hubApi<ApiAnswer<'patchChannelOffersById'>>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
          : await hubApi<ApiAnswer<'postChannelOffers'>>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: row.key, ...body }) })
        savedOffers.push({ rowKey: row.key, platform, saved })
      }
      if (!savedOffers.length) {
        showFeedback('Varyant Bulunamadı', 'Bu platform için güncellenebilir varyant bulunamadı.', 'error')
        return
      }
      const savedByRow = new Map(savedOffers.map(item => [item.rowKey, item]))
      setVariantRows(rows => rows.map(row => {
        const saved = savedByRow.get(row.key)
        if (!saved) return row
        return { ...row, platformStatuses: row.platformStatuses?.map(platform => platform.connectionId === saved.platform.connectionId ? { ...platform, offerId: saved.saved.id, listPrice: saved.saved.listPrice, salePrice: saved.saved.salePrice, currency: saved.saved.currency, offerVersion: saved.saved.version } : platform) }
      }))
      setVariantPlatformPricing(null)
      const message = `${platformDisplayName(selectedPlatform)} için ${savedOffers.length} varyantın fiyatı toplu olarak kaydedildi.`
      setNotice(message); showFeedback('Platform Fiyatları Kaydedildi', message, 'success')
      await client.invalidateQueries({ queryKey: queryKeys.products() })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: queryKeys.product(editProductId) })
        adoptOwnProductWrite((await productToEdit.refetch()).data)
      }
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Pazaryeri varyant fiyatları kaydedilemedi.'
      showFeedback('Platform Fiyatları Kaydedilemedi', message, 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  async function saveVariantPlatformPricingRow(targetRow: VariantDraft, targetPlatform: VariantPlatformStatus, targetDraft: ChannelPricingDraft) {
    const platform = targetRow.platformStatuses?.find(item => item.connectionId === targetPlatform.connectionId)
    if (!platform?.connectionId) {
      showFeedback('Fiyat Bilgisi Alınamadı', 'Bu platform bağlantısının fiyat bilgisi alınamadı.', 'error')
      return
    }
    const listPrice = priceInputNumber(targetDraft.listPrice)
    const salePrice = priceInputNumber(targetDraft.salePrice)
    if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
      showFeedback('Geçersiz Fiyat', 'Liste fiyatı satış fiyatından küçük olamaz.', 'error')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      const body = {
        listPrice,
        salePrice,
        currency: platform.currency ?? targetPlatform.currency ?? form.currency ?? 'TRY',
        vatRate: offerVatRate(platform.vatRate ?? targetPlatform.vatRate),
        vatInclusion: platform.vatInclusion ?? targetPlatform.vatInclusion ?? form.vatIncluded,
        roundingMode: platform.roundingMode ?? targetPlatform.roundingMode ?? 'HALF_EVEN',
        safetyStock: platform.safetyStock ?? targetPlatform.safetyStock ?? editorCount(form.safetyStock || 0),
        status: 'ACTIVE',
        reason: `${platformDisplayName(platform)} varyant fiyatı`
      }
      const saved = platform.offerId && platform.offerVersion != null
        ? await hubApi<ApiAnswer<'patchChannelOffersById'>>(`/channel-offers/${platform.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${platform.offerVersion}"` }, body: JSON.stringify(body) })
        : await hubApi<ApiAnswer<'postChannelOffers'>>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ connectionId: platform.connectionId, variantId: targetRow.key, ...body }) })
      setVariantRows(rows => rows.map(row => row.key !== targetRow.key ? row : { ...row, platformStatuses: row.platformStatuses?.map(item => item.connectionId === platform.connectionId ? { ...item, offerId: saved.id, listPrice: saved.listPrice, salePrice: saved.salePrice, currency: saved.currency, offerVersion: saved.version } : item) }))
      showFeedback('Platform Fiyatı Kaydedildi', `${platformDisplayName(platform)} ${targetRow.optionSignature || targetRow.sku} fiyatı kaydedildi.`, 'success')
      await client.invalidateQueries({ queryKey: queryKeys.products() })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: queryKeys.product(editProductId) })
        adoptOwnProductWrite((await productToEdit.refetch()).data)
      }
    } catch (reason) {
      showFeedback('Platform Fiyatı Kaydedilemedi', reason instanceof Error ? reason.message : 'Varyant kanal fiyatı kaydedilemedi.', 'error')
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  void saveVariantPlatformPricingBulk
  void saveVariantPlatformPricingRow
  async function saveVariantPlatformPricingMatrix(drafts: Record<string, ChannelPricingDraft>) {
    if (!variantPlatformPricing) return
    const sourceRow = variantRows.find(item => item.key === variantPlatformPricing.rowKey)
    const sourcePlatforms = sourceRow?.platformStatuses?.length ? sourceRow.platformStatuses : !editProductId ? variantPricingPlatforms : []
    if (!sourceRow || !sourcePlatforms.length) {
      showFeedback('Platform Fiyatı Bulunamadı', 'Bu ürün için güncellenebilir platform fiyatı bulunamadı.', 'error')
      return
    }
    const platformKey = (platform: VariantPlatformStatus) => `${platform.platformCode}:${platform.connectionId ?? platform.platform}`
    const targets = variantRows.flatMap(targetRow => sourcePlatforms.flatMap(sourcePlatform => {
      const platform = targetRow.platformStatuses?.find(item => item.connectionId === sourcePlatform.connectionId) ?? (!editProductId ? sourcePlatform : undefined)
      const draft = drafts[`${targetRow.key}:${platformKey(sourcePlatform)}`]
      return platform?.connectionId && draft ? [{ row: targetRow, platform, draft }] : []
    }))
    if (!targets.length) {
      showFeedback('Varyant Bulunamadı', 'Bu platform için güncellenebilir varyant bulunamadı.', 'error')
      return
    }
    for (const target of targets) {
      const listPrice = priceInputNumber(target.draft.listPrice)
      const salePrice = priceInputNumber(target.draft.salePrice)
      if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) {
        showFeedback('Geçersiz Fiyat', 'Liste fiyatı satış fiyatından küçük olamaz.', 'error')
        return
      }
    }
    if (!editProductId) {
      setVariantChannelPricing(current => ({
        ...current,
        ...Object.fromEntries(targets.map(target => [`${target.row.key}:${target.platform.connectionId}`, target.draft]))
      }))
      setVariantPlatformPricing(null)
      const message = `${targets.length} varyantın platform fiyatı ürün kaydedilince uygulanacak.`
      setNotice(message); showFeedback('Platform Fiyatları Hazırlandı', message, 'success')
      return
    }
    setVariantPlatformPricingSaving(true)
    try {
      let savedCount = 0
      for (const target of targets) {
        const listPrice = priceInputNumber(target.draft.listPrice)
        const salePrice = priceInputNumber(target.draft.salePrice)
        const body = {
          listPrice,
          salePrice,
          currency: target.platform.currency ?? form.currency ?? 'TRY',
          vatRate: offerVatRate(target.platform.vatRate),
          vatInclusion: target.platform.vatInclusion ?? form.vatIncluded,
          roundingMode: target.platform.roundingMode ?? 'HALF_EVEN',
          safetyStock: target.platform.safetyStock ?? editorCount(form.safetyStock || 0),
          status: 'ACTIVE',
          reason: `${platformDisplayName(target.platform)} toplu varyant fiyatı`,
          // A price typed apart from the variant's panel price is this marketplace's own ("Özel Fiyat"), so a panel save
          // never overwrites it; one equal to the panel price keeps the stored choice.
          followsPanelPrice: typedOfferPriceChoice({ listPrice, salePrice }, { listPrice: target.row.listPrice, salePrice: target.row.salePrice }, channelTakesListPrice(target.platform.platformCode))
        }
        const connectionId = target.platform.connectionId!
        const current = target.platform
        let saved: SavedChannelOffer
        if (current.offerId && current.offerVersion != null) {
          saved = await hubApi<SavedChannelOffer>(`/channel-offers/${current.offerId}`, { method: 'PATCH', headers: { 'If-Match': `"v${current.offerVersion}"` }, body: JSON.stringify(body) })
        } else {
          const slot = `${target.row.key}:${connectionId}`
          const postBody = JSON.stringify({ connectionId, variantId: target.row.key, ...body })
          try {
            saved = await hubApi<SavedChannelOffer>('/channel-offers', { method: 'POST', headers: { 'Idempotency-Key': channelOfferPostKey(offerPostKeys.current, slot, postBody, newIdempotencyKey) }, body: postBody })
            offerPostKeys.current.delete(slot)
          } catch (reason) {
            // A refusal the server answered is replayed for the same key: the next try takes a new one.
            if (failedWithHttpAnswer(reason)) offerPostKeys.current.delete(slot)
            throw reason
          }
        }
        // Each written offer goes into the rows at once, so a later failure does not lose its new version.
        setVariantRows(rows => rowsWithSavedOffer(rows, target.row.key, connectionId, saved))
        savedCount++
      }
      setVariantPlatformPricing(null)
      const message = `${savedCount} varyant platform fiyatı kaydedildi.`
      setNotice(message); showFeedback('Platform Fiyatları Kaydedildi', message, 'success')
      await client.invalidateQueries({ queryKey: queryKeys.products() })
      if (editProductId) {
        await client.invalidateQueries({ queryKey: queryKeys.product(editProductId) })
        adoptOwnProductWrite((await productToEdit.refetch()).data)
      }
    } catch (reason) {
      showFeedback('Platform Fiyatları Kaydedilemedi', reason instanceof Error ? reason.message : 'Pazaryeri varyant fiyatları kaydedilemedi.', 'error')
      // The offers written before the failure changed the product's lists: they are read again.
      await Promise.all([client.invalidateQueries({ queryKey: queryKeys.products() }), client.invalidateQueries({ queryKey: queryKeys.product(editProductId) })]).catch(() => undefined)
    } finally {
      setVariantPlatformPricingSaving(false)
    }
  }
  return { offerVatRate, channelPriceDraft, toggleCustomChannelPrice, channelPriceEdited, updateChannelPrice, platformDisplayName, openVariantPlatformPricing, saveVariantPlatformPricingBulk, saveVariantPlatformPricingRow, saveVariantPlatformPricingMatrix }
}
export type ChannelPricingResult = ReturnType<typeof useChannelPricing>
