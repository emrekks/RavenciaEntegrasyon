// What a save checks before it writes (the rows it sends, the catalog and publication rules) and how a stopped save
// shows it: the required fields framed red, the first refused field brought into view.
import { type FormEvent } from 'react'
import { revealFieldError } from './field-error-reveal'
import { buildVariantGenerationDefaults } from '../variant-generation'
import { requiredFieldMessage, requiredFieldsMessage, type RequiredEditorField } from './required-fields'
import { validateEditor, type EditorSaveValidationInput } from './validate-editor'
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
import type { ChannelPricingResult } from './use-channel-pricing'

export function useEditorChecks(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult & VariantCodeActionsResult & EditorMediaActionsResult & PlatformUpdateResult & ChannelPricingResult) {
  const { channelPricing, editProductId, publishOnly, duplicateProductId, setError, setNotice, operationToasts, form, attributeSelections, attributeTextValues, variantAttributeIds, variantRows, selectedChannelIds, automaticBarcodeGeneration, setWizardStep, mediaFiles, removedVariantIds, webColorAutoEnabled, manualWebColorValueId, setRequiredErrors, lastFieldReveal, initialEditDesi, showFeedback, productToCopy, connections, cardPriceDefaults, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, initialStock, missingRequiredFields, mediaUrls, mappedRequirements, colorOptionRequirement, webColorRequirement, channelPriceDraft, channelPriceEdited } = c
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
  // The save's checks (validate-editor.ts) on the rows it sends, with what the page knows besides the form.
  function saveValidationInput(rows: VariantDraft[]): EditorSaveValidationInput {
    return {
      form, automaticBarcodes: automaticBarcodeGeneration, hasVariants: variantRows.length > 1, rows, editing: Boolean(editProductId), initialDesi: initialEditDesi.current, cardPriceIssue: cardPriceDefaults.issue,
      variantAttributeIds, requirements: mappedRequirements, colorOptionRequirement, webColorRequirement, webColor: { autoEnabled: webColorAutoEnabled, manualValueId: manualWebColorValueId }, attributeSelections, attributeTextValues,
      mediaUrls, mediaFileCount: mediaFiles.length, copySourceVariants: duplicateProductId && productToCopy.data ? productToCopy.data.variants : null,
      selectedChannels: selectedChannelIds.map(connectionId => ({ id: connectionId, name: (connections.data?.items ?? []).find(item => item.id === connectionId)?.displayName ?? 'Seçili kanal', draft: channelPriceDraft(connectionId) })),
      customChannels: Object.entries(channelPricing).map(([connectionId, draft]) => ({ id: connectionId, name: (connections.data?.items ?? []).find(item => item.id === connectionId)?.displayName ?? 'Pazaryeri', draft, edited: channelPriceEdited(connectionId) }))
    }
  }
  function validate(rows: VariantDraft[], requireCompleteCatalog = true, requirePublicationReadiness = true) {
    const { issues } = validateEditor(saveValidationInput(rows), { kind: 'save', requireCompleteCatalog, requirePublicationReadiness })
    if (issues.length) throw new Error(issues.join(' '))
  }
  // What Taslak Olarak Kaydet refuses: only values the server refuses (clashing codes, malformed prices or image links).
  function draftBlockingIssues(rows: VariantDraft[]) {
    return validateEditor(saveValidationInput(rows), { kind: 'draft' }).issues
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
  return { rowsForSubmit, validate, draftBlockingIssues, handleInvalid, dismissProductError, stopOnRequiredFields, blockedByRequiredFields }
}
export type EditorChecksResult = ReturnType<typeof useEditorChecks>
