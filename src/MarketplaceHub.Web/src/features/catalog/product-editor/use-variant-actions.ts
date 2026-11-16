// The variant table's rows: generating, clearing, removing, editing, bulk values and picture assignment.
import { applyVariantBulkModes, variantBulkEditIssue, type VariantBulkEditField, type VariantBulkInputs } from '../variant-bulk-edit'
import { generatedRowsReplacePlaceholder, mergeGeneratedVariantRows } from '../variant-generation'
import { filterVariantsByOptions } from '../variant-filtering'
import { inheritAssignments } from './family-gallery-save'
import { readVariantMediaAssignmentDraft, variantMediaAssignmentKey } from '../variant-media-assignments'
import { normalizeVariantOptionName } from '../shared'
import { VariantDraft } from './editor-types'
import { sortVariantsAlphabetically, displayVariantOptionSignature, variantSignatureKey, variantBulkEditLabels, buildVariantMatrix, rowMatchesVariantMediaValue } from './editor-helpers'
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
import { parseEditorNumber } from './editor-number'

export function useVariantActions(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult) {
  const { editProductId, setNotice, form, attributeSelections, variantAttributeIds, variantRows, setVariantRows, automaticBarcodeGeneration, variantMediaModal, setVariantMediaModal, variantBulkEdit, setVariantBulkEdit, variantBulkEditFilterSelections, setVariantBulkEditFilterSelections, setRemovedVariantIds, setRemovedVariantRows, variantDeleteConfirmation, setVariantDeleteConfirmation, setReplacedPlaceholderIds, showFeedback, productToEdit, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, mappedRequirements, requestVariantMediaSave, mediaStrip, rowSize, rowColour, mediaChoices, bulkMediaGroups, variantBulkEditFilterGroups, hasVariantBulkEditFilters } = c
  function generateVariants(attributeIds = variantAttributeIds, preserveExistingDetails = false) {
    try {
      if (automaticBarcodeGeneration && !form.modelCode.trim()) {
        const message = 'Otomatik barkod oluşturmak için önce model kodunu girin.'; setNotice(message); showFeedback('Model Kodu Gerekli', message, 'error')
        return false
      }
      const generated = buildVariantMatrix(mappedRequirements, attributeIds, attributeSelections, fallbackListPrice, fallbackSalePrice, fallbackCostPrice, automaticBarcodeGeneration, form.modelCode)
      if (!generated.length) {
        const message = 'Önce varyant olacak özellikleri ve bu özelliklerin değerlerini seçin.'; setNotice(message); showFeedback('Varyant Özelliği Seçilmedi', message, 'error')
        return false
      }
      const replacesPlaceholder = generatedRowsReplacePlaceholder(variantRows, generated)
      const persistedIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
      const replacedSavedPlaceholderIds = replacesPlaceholder ? variantRows.filter(row => persistedIds.has(row.key)).map(row => row.key) : []
      if (replacedSavedPlaceholderIds.length) setReplacedPlaceholderIds(current => [...new Set([...current, ...replacedSavedPlaceholderIds])])
      setVariantRows(current => {
        // The option-less "Tek Ürün" row gives way to the generated rows instead of staying beside them.
        if (generatedRowsReplacePlaceholder(current, generated)) return mergeGeneratedVariantRows([], generated, { key: variantSignatureKey, sort: sortVariantsAlphabetically, keepExisting: false, automaticBarcodes: automaticBarcodeGeneration, modelCode: form.modelCode, fallbackSalePrice, fallbackListPrice })
        if (current.length === 1 && current[0].optionSignature.trim().toLocaleLowerCase('tr-TR') === 'tek ürün' && generated.length === 1) {
          const existing = current[0]
          const [next] = generated
          return [{ ...next, key: existing.key, ...(automaticBarcodeGeneration && !preserveExistingDetails ? {} : { barcode: existing.barcode || next.barcode, sku: existing.sku || next.sku, salePrice: existing.salePrice, listPrice: existing.listPrice }), stock: existing.stock, loadedStock: existing.loadedStock, loadedVersion: existing.loadedVersion, costPrice: existing.costPrice, loadedCostPrice: existing.loadedCostPrice, mediaRefs: existing.mediaRefs }]
        }
        // A saved product keeps its rows as they are: only the combinations it lacks are added (mergeGeneratedVariantRows).
        // A row added now starts with its colour's and its size's common pictures pre-selected, saved as its own list (family-gallery-save.ts inheritAssignments).
        return inheritAssignments(current, mergeGeneratedVariantRows(current, generated, { key: variantSignatureKey, sort: sortVariantsAlphabetically, keepExisting: preserveExistingDetails || Boolean(editProductId), automaticBarcodes: automaticBarcodeGeneration, modelCode: form.modelCode, fallbackSalePrice, fallbackListPrice }), rowColour, rowSize, mediaStrip)
      })
      const heldKeys = new Set(variantRows.map(row => variantSignatureKey(row.optionSignature)))
      const addedCount = editProductId ? new Set(generated.map(row => variantSignatureKey(row.optionSignature)).filter(key => !heldKeys.has(key))).size : generated.length
      const message = replacesPlaceholder
        ? `${generated.length} varyant satırı hazırlandı; “Tek Ürün” satırı bu varyantlarla değiştirildi.${replacedSavedPlaceholderIds.length ? ' Kayıtlı “Tek Ürün” varyantı Kaydet ile kaldırılır.' : ''}`
        : editProductId ? `${addedCount} yeni varyant satırı eklendi; kayıtlı satırlar değişmedi.` : `${generated.length} varyant satırı hazırlandı.`
      setNotice(message); showFeedback('Varyantlar Hazırlandı', message, 'success')
      return true
    } catch (reason) { const message = reason instanceof Error ? reason.message : 'Varyantlar oluşturulamadı.'; setNotice(message); showFeedback('Varyantlar Oluşturulamadı', message, 'error'); return false }
  }
  function clearVariants() {
    const persistedIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
    const clearedSavedIds = variantRows.filter(row => persistedIds.has(row.key)).map(row => row.key)
    setRemovedVariantIds(current => [...new Set([...current, ...clearedSavedIds])])
    setRemovedVariantRows(current => [...current, ...variantRows.flatMap((row, index) => persistedIds.has(row.key) ? [{ row, index }] : [])])
    setVariantRows([])
    const message = clearedSavedIds.length
      ? `Varyant satırları temizlendi. Kaydedince ${clearedSavedIds.length} kayıtlı varyant panelden silinir; pazaryerlerindeki ilanlara dokunulmaz.`
      : 'Oluşan varyant satırları temizlendi.'
    setNotice(message); showFeedback('Varyantlar Temizlendi', message, 'success')
  }
  // The row's trash button: a row the form made itself just leaves the table; a saved variant asks first and is deleted
  // on the server by the next Kaydet (variantIdsToDelete).
  function requestVariantRowRemoval(row: VariantDraft) {
    const persistedIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
    if (!persistedIds.has(row.key)) { setVariantRows(rows => rows.filter(item => item.key !== row.key)); return }
    setVariantDeleteConfirmation(row)
  }
  function confirmVariantRowRemoval() {
    const row = variantDeleteConfirmation
    setVariantDeleteConfirmation(null)
    if (!row) return
    const index = variantRows.findIndex(item => item.key === row.key)
    if (index < 0) return
    setRemovedVariantRows(current => [...current.filter(item => item.row.key !== row.key), { row: variantRows[index], index }])
    setRemovedVariantIds(current => [...new Set([...current, row.key])])
    setVariantRows(rows => rows.filter(item => item.key !== row.key))
    const label = displayVariantOptionSignature(row.optionSignature, row.options)
    const message = `${label} varyantı Kaydet’te silinecek.`
    setNotice(message); showFeedback('Varyant Silinecek', message, 'info')
  }
  function updateVariantRow(keyValue: string, field: keyof VariantDraft, value: string) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, [field]: field === 'costPrice' ? (value.trim() === '' ? null : parseEditorNumber(value)) : field === 'stock' || field === 'salePrice' || field === 'listPrice' ? parseEditorNumber(value || 0) : value })) }
  function updateVariantMedia(keyValue: string, values: string[]) { setVariantRows(rows => rows.map(row => row.key !== keyValue ? row : { ...row, mediaRefs: values })) }
  function openVariantBulkEdit(field: VariantBulkEditField) {
    setVariantBulkEditFilterSelections({})
    setVariantBulkEdit({ field, mode: 'set', value: '', error: '' })
  }

  function applyVariantBulkEdit() {
    if (!variantBulkEdit) return
    if (!variantBulkEdit.value.trim()) {
      setVariantBulkEdit(current => current ? { ...current, error: 'Uygulanacak değeri girin.' } : current)
      return
    }
    const targetRows = filterVariantsByOptions(variantRows, variantBulkEditFilterGroups, variantBulkEditFilterSelections, rowMatchesVariantMediaValue)
    if (!targetRows.length) {
      setVariantBulkEdit(current => current ? { ...current, error: 'Seçili filtreyle eşleşen varyant bulunamadı.' } : current)
      return
    }
    const value = parseEditorNumber(variantBulkEdit.value)
    const issue = variantBulkEditIssue(variantBulkEdit.field, value)
    if (issue) {
      setVariantBulkEdit(current => current ? { ...current, error: issue } : current)
      return
    }
    const rowKeys = new Set(targetRows.map(row => row.key))
    const modeInputs = Object.fromEntries((['stock', 'salePrice', 'costPrice', 'listPrice'] as const).map(field => [field, { mode: variantBulkEdit.mode, value: field === variantBulkEdit.field ? value : null }])) as VariantBulkInputs
    const applied = applyVariantBulkModes(variantRows, rowKeys, modeInputs)
    setVariantRows(rows => applyVariantBulkModes(rows, rowKeys, modeInputs).rows)
    showFeedback('Toplu Değer Uygulandı', `${variantBulkEditLabels[variantBulkEdit.field]} ${variantBulkEdit.mode === 'set' ? 'değeri' : variantBulkEdit.mode === 'increase' ? 'artışı' : 'azaltması'}${applied.clamped ? ` (${applied.clamped} varyant 0'da sınırlandı)` : ''} ${hasVariantBulkEditFilters ? `${targetRows.length} filtre eşleşen varyanta` : `tüm ${targetRows.length} varyanta`} uygulandı.`, 'success')
    setVariantBulkEdit(null)
  }

  function openVariantMediaPicker(rowKey: string) {
    const row = variantRows.find(item => item.key === rowKey)
    setVariantMediaModal({ mode: 'variant', rowKey, draftRefs: row?.mediaRefs ?? [] })
  }
  function openBulkVariantMediaPicker() {
    const groups = bulkMediaGroups
    if (!groups.length) {
      const message = 'Önce seçenek grubu ve en az bir seçenek değeri seçin.'
      setNotice(message); showFeedback('Seçenek Değeri Gerekli', message, 'error')
      return
    }
    const group = groups.find(item => normalizeVariantOptionName(item.name) === 'RENK') ?? groups[0]
    const draftsBySelection = Object.fromEntries(groups.flatMap(mediaGroup => mediaGroup.values.map(value => {
      const assignedRefs = new Set(variantRows
        .filter(row => rowMatchesVariantMediaValue(row, mediaGroup, value))
        .flatMap(row => row.mediaRefs))
      return [variantMediaAssignmentKey(mediaGroup.id, value.id), mediaChoices.filter(option => assignedRefs.has(option.value)).map(option => option.value)]
    })))
    setVariantMediaModal({ mode: 'bulk', draftsBySelection, groupId: group.id, valueId: group.values[0]?.id ?? '' })
  }
  function applyVariantMediaSelection() {
    if (!variantMediaModal) return
    if (variantMediaModal.mode === 'variant') {
      updateVariantMedia(variantMediaModal.rowKey, variantMediaModal.draftRefs)
      setVariantMediaModal(null)
      requestVariantMediaSave()
      showFeedback('Varyant Görselleri Güncellendi', variantMediaModal.draftRefs.length ? `${variantMediaModal.draftRefs.length} varyant görseli seçildi.` : 'Varyant görselleri kaldırıldı.', 'success')
      return
    }
    const group = bulkMediaGroups.find(item => item.id === variantMediaModal.groupId)
    if (!group) return
    const assignments = group.values.map(value => ({
      value,
      refs: readVariantMediaAssignmentDraft(variantMediaModal.draftsBySelection, variantMediaAssignmentKey(group.id, value.id)),
      matchingRows: variantRows.filter(row => rowMatchesVariantMediaValue(row, group, value))
    })).filter(assignment => assignment.matchingRows.length > 0)
    const matchingRowCount = new Set(assignments.flatMap(assignment => assignment.matchingRows.map(row => row.key))).size
    if (!matchingRowCount) {
      const message = `${group.name} değerleriyle eşleşen varyant satırı bulunamadı.`
      setNotice(message); showFeedback('Eşleşen Varyant Yok', message, 'error')
      return
    }
    setVariantRows(rows => rows.map(row => {
      const assignment = assignments.find(item => item.matchingRows.some(matchingRow => matchingRow.key === row.key))
      return assignment ? { ...row, mediaRefs: assignment.refs } : row
    }))
    setVariantMediaModal(null); requestVariantMediaSave()
    const message = `${group.name} görsel eşlemesi uygulandı: ${assignments.length} değer, ${matchingRowCount} varyant satırı.`
    setNotice(message); showFeedback('Görsel Eşlemesi Uygulandı', message, 'success')
  }

  return { generateVariants, clearVariants, requestVariantRowRemoval, confirmVariantRowRemoval, updateVariantRow, updateVariantMedia, openVariantBulkEdit, applyVariantBulkEdit, openVariantMediaPicker, openBulkVariantMediaPicker, applyVariantMediaSelection }
}
export type VariantActionsResult = ReturnType<typeof useVariantActions>
