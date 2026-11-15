// What a save checks before it writes (the rows it sends, the catalog and publication rules) and how a stopped save
// shows it: the required fields framed red, the first refused field brought into view.
import { type FormEvent } from 'react'
import { revealFieldError } from './field-error-reveal'
import { isStoredProductMediaUrl, publicProductMediaUrls } from '../product-media-editor'
import { buildVariantGenerationDefaults } from '../variant-generation'
import { autoWebColorIssues, isOptionRequirement } from './publication-readiness'
import { isProductDeliveryDays } from './product-delivery-days'
import { requiredFieldMessage, requiredFieldsMessage, type RequiredEditorField } from './required-fields'
import { parseProductDesi, productDesiTooLarge, productDesiTooLargeMessage } from '../product-desi'
import { productMediaUrlIssue } from '../product-media-url'
import { productCopyIdentifierConflicts } from '../product-copy-identifiers'
import { VariantDraft } from './editor-types'
import { MAX_VARIANTS } from './editor-helpers'
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

export function useEditorChecks(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult & VariantCodeActionsResult & EditorMediaActionsResult & PlatformUpdateResult & ChannelPricingResult) {
  const { editProductId, publishOnly, duplicateProductId, setError, setNotice, operationToasts, form, attributeSelections, attributeTextValues, variantAttributeIds, variantRows, selectedChannelIds, automaticBarcodeGeneration, setWizardStep, mediaFiles, removedVariantIds, webColorAutoEnabled, manualWebColorValueId, setRequiredErrors, lastFieldReveal, initialEditDesi, showFeedback, productToCopy, connections, cardPriceDefaults, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock, missingRequiredFields, mediaUrls, mappedRequirements, colorOptionRequirement, webColorRequirement, channelPriceDraft } = c
  function rowsForSubmit(requireCompleteCatalog = true) {
    if (requireCompleteCatalog && variantAttributeIds.length && !variantRows.length) throw new Error('Varyant özellikleri seçili. Önce “Ürünleri Ekle” ile varyantları oluşturun.')
    if (editProductId && !variantRows.length) {
      if (removedVariantIds.length) throw new Error('Tüm kayıtlı varyantlar temizlendi. Kaydetmeden önce “Ürünleri Ekle” ile yeni varyant satırları oluşturun; ürünün en az bir varyantı kalmalıdır.')
      return []
    }
    if (variantRows.length) return variantRows
    const generatedDefaults = automaticBarcodeGeneration ? buildVariantGenerationDefaults({ modelCode: form.modelCode, sequence: 1, automaticBarcodes: true, fallbackSalePrice, fallbackListPrice }) : null
    return [{ key: crypto.randomUUID(), optionSignature: 'Tek Ürün', options: {}, attributeValueIds: {}, sku: generatedDefaults?.sku ?? form.baseSku.trim(), barcode: generatedDefaults?.barcode ?? form.barcode, stock: initialStock, salePrice: generatedDefaults?.salePrice ?? fallbackSalePrice, listPrice: generatedDefaults?.listPrice ?? fallbackListPrice, costPrice: fallbackCostPrice, mediaRefs: [] }]
  }
  function validate(rows: VariantDraft[], requireCompleteCatalog = true, requirePublicationReadiness = true) {
    const issues: string[] = []; const requirementList = mappedRequirements
    if (requireCompleteCatalog && (variantAttributeIds.length > 2 || variantAttributeIds.some(id => { const requirement = requirementList.find(item => item.attributeId === id); return !requirement || !isOptionRequirement(requirement) }))) issues.push('Varyant için en fazla 2 seçenek başlığı kullanılabilir.')
    if (requireCompleteCatalog && !webColorAutoEnabled && (!webColorRequirement || !manualWebColorValueId)) issues.push('Manuel Web Color aktarımı için gönderilecek panel renk değerini seçin.')
    if (!webColorAutoEnabled && webColorRequirement && manualWebColorValueId && !webColorRequirement.attribute.values.some(value => value.id === manualWebColorValueId)) issues.push('Manuel Web Color için seçilen değer geçerli değil.')
    // Automatic Web Color reads each row's own Renk, as the publish checks and the server do (publication-readiness).
    if (requireCompleteCatalog && webColorAutoEnabled && webColorRequirement) issues.push(...autoWebColorIssues({ variantRows: rows, variantAttributeIds, attributeSelections }, colorOptionRequirement))
    if (!form.title.trim()) issues.push('Ürün adı zorunludur.')
    if (cardPriceDefaults.issue) issues.push(cardPriceDefaults.issue)
    if (automaticBarcodeGeneration && !form.modelCode.trim()) issues.push('Otomatik barkod oluşturmak için model kodu zorunludur.')
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
    const publicationMediaRefs = [...mediaUrls, ...rows.flatMap(row => row.mediaRefs.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4)))]
    const allMediaUrls = publicProductMediaUrls(publicationMediaRefs)
    const invalidMediaUrl = allMediaUrls.map(productMediaUrlIssue).find((issue): issue is string => issue !== null)
    if (invalidMediaUrl) issues.push(`Görsel bağlantısı geçersiz: ${invalidMediaUrl} Ürün kaydedilmeden önce düzeltin veya kaldırın.`)
    const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')); if (skus.some(value => !value)) issues.push('Tüm varyantlarda stok kodu zorunludur.'); if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
    const signatures = rows.map(row => row.optionSignature); if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
    const barcodes = rows.map(row => row.barcode.trim()).filter(Boolean); if (new Set(barcodes.map(value => value.toLocaleUpperCase('tr-TR'))).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
    if (duplicateProductId && productToCopy.data) {
      const conflicts = productCopyIdentifierConflicts(productToCopy.data.variants, rows)
      if (conflicts.barcodes) issues.push('Kaynak ürünün barkodları zaten kayıtlı. Yeni ürünü oluşturmak için çakışan barkodları değiştirin.')
      if (conflicts.skus) issues.push('Kaynak ürünün stok kodları zaten kayıtlı. Yeni ürünü oluşturmak için çakışan stok kodlarını değiştirin.')
    }
     if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
     // Owner, 10 Nov 2026 ("0 olmayacak onlarda"): a variant's list and sale price are above 0, like the card's.
     if (rows.some(row => !(row.salePrice > 0) || !(row.listPrice > 0))) issues.push('Her varyantta liste ve satış fiyatı 0\'dan büyük olmalı.')
     // A card none of whose variants has a desi opens with the field empty (no guessed 1) and may be saved so.
     if (!(editProductId && initialEditDesi.current === 0 && !form.desi.trim()) && (!form.desi.trim() || parseProductDesi(form.desi) <= 0)) issues.push('Desi sıfırdan büyük olmalıdır.')
     if (productDesiTooLarge(form.desi)) issues.push(productDesiTooLargeMessage)
     if (form.warrantyMonths.trim() && (!Number.isInteger(Number(form.warrantyMonths)) || Number(form.warrantyMonths) < 0 || Number(form.warrantyMonths) > 240)) issues.push('Garanti süresi 0-240 ay arasında tam sayı olmalıdır.')
     if (!isProductDeliveryDays(form.deliveryDays)) issues.push('Teslimat süresi 1-30 gün arasında tam sayı olmalıdır.')
     if (requireCompleteCatalog && requirePublicationReadiness && selectedChannelIds.length) {
      if (!form.brandId) issues.push('Trendyol yayını için marka zorunludur.'); if (!form.modelCode.trim() || form.modelCode.trim().length > 40) issues.push('Trendyol yayını için en fazla 40 karakterlik model kodu zorunludur.'); if (form.title.trim().length > 100) issues.push('Trendyol ürün başlığı en fazla 100 karakter olabilir.')
      const publicMediaUrls = publicProductMediaUrls(publicationMediaRefs)
      const hasLocalPublicationMedia = mediaFiles.length > 0 || publicationMediaRefs.some(isStoredProductMediaUrl)
      if (!publicMediaUrls.length && !hasLocalPublicationMedia) issues.push('Trendyol yayını için en az bir ürün görseli ekleyin.')
      if (publicMediaUrls.some(url => !url.startsWith('https://'))) issues.push('Tüm görsel adresleri HTTPS olmalıdır.')
      if (rows.some(row => !row.barcode.trim() || !/^[a-zA-Z0-9._-]+$/.test(row.barcode.trim()))) issues.push('Trendyol yayını için her varyantta geçerli ve benzersiz barkod zorunludur.'); if (rows.some(row => row.salePrice <= 0)) issues.push('Trendyol yayını için satış fiyatı sıfırdan büyük olmalıdır.')
      for (const connectionId of selectedChannelIds) {
        const draft = channelPriceDraft(connectionId); const listPrice = Number(draft.listPrice); const salePrice = Number(draft.salePrice)
        if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || salePrice <= 0 || listPrice < salePrice) {
          const name = (connections.data?.items ?? []).find(item => item.id === connectionId)?.displayName ?? 'Seçili kanal'
          issues.push(`${name} için liste fiyatı satış fiyatından küçük olamaz; satış fiyatı sıfırdan büyük olmalıdır.`)
        }
      }
    }
    if (issues.length) throw new Error(issues.join(' '))
  }

  function handleInvalid(event: FormEvent<HTMLFormElement>) {
    const target = event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
    const message = target.validationMessage || 'Lütfen zorunlu alanları doldurun.'
    // Only the first refused field of one save is brought into view.
    if (Date.now() - lastFieldReveal.current > 600) { lastFieldReveal.current = Date.now(); revealFieldError(null, target) }
    setError(new Error(message))
    setNotice(message)
    showFeedback('Zorunlu Alan Eksik', message, 'error')
  }

  // Editing the form takes back the error it was showing.
  function dismissProductError() {
    setError(undefined)
    setNotice('')
    operationToasts.dismissErrors()
  }

  // Empty required fields stop this action: each is framed red with its message, the first step opens on the first one.
  function stopOnRequiredFields(missing: readonly RequiredEditorField[], liveCard = false) {
    const message = `${requiredFieldsMessage(missing)} ${liveCard ? 'Satıştaki ürün taslağa alınmaz; alanları doldurup kaydedin.' : 'Yayınlamak ve pazaryerlerinde güncellemek için önce bu alanları doldurun.'}`
    setRequiredErrors(Object.fromEntries(missing.map(field => [field, requiredFieldMessage(field, form)])))
    if (!publishOnly) setWizardStep(1)
    requestAnimationFrame(() => { lastFieldReveal.current = Date.now(); revealFieldError(document.getElementById('product-creation-form')) })
    setError(new Error(message)); setNotice(message)
    showFeedback('Zorunlu Alan Eksik', message, 'error')
  }
  function blockedByRequiredFields() {
    if (!missingRequiredFields.length) return false
    stopOnRequiredFields(missingRequiredFields)
    return true
  }
  return { rowsForSubmit, validate, handleInvalid, dismissProductError, stopOnRequiredFields, blockedByRequiredFields }
}
export type EditorChecksResult = ReturnType<typeof useEditorChecks>
