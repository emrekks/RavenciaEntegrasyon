// Barcodes and stock codes of the variant table: from the barcode, generated from the model code, pasted.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { hubApi } from '../../../shared/api'
import { applyVariantCodesByKey, planVariantCodes, variantCodeCandidates, type VariantCodeTarget } from '../variant-generation'
import { describePlatformCodeIssues, platformCodeIssues } from '../platform-code-rules'
import { barcodeClipboardIssue, parseBarcodeClipboardValues } from '../product-barcode-paste'
import { VariantDraft, PendingVariantCodeChange } from './editor-types'
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

export function useVariantCodeActions(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult) {
  const { setNotice, operationToasts, form, setForm, variantRows, setVariantRows, setBarcodeSkuMenuOpen, setBarcodePasteMenuOpen, pendingVariantCodeGeneration, setPendingVariantCodeGeneration, variantRowsRef, showFeedback, productToEdit, codeRulePlatforms } = c
  function applyBarcodeToSku(mode: 'missing' | 'all') {
    const rowsWithBarcode = variantRows.filter(row => row.barcode.trim())
    if (!rowsWithBarcode.length) {
      const message = 'Stok kodu oluşturmak için önce en az bir barkod girin.'; setNotice(message); showFeedback('Barkod Gerekli', message, 'error'); setBarcodeSkuMenuOpen(false)
      return
    }
    const rowsToUpdate = rowsWithBarcode.filter(row => mode === 'all' || !row.sku.trim())
    if (!rowsToUpdate.length) {
      const message = 'Boş stok kodu bulunamadı; mevcut kodlar korunuyor.'; setNotice(message); showFeedback('Stok Kodları Korundu', message, 'info'); setBarcodeSkuMenuOpen(false)
      return
    }
    const targetKeys = new Set(rowsToUpdate.map(row => row.key))
    const finalSkuByKey = new Map(variantRows.map(row => [row.key, targetKeys.has(row.key) ? row.barcode.trim() : row.sku.trim()]))
    const skuOwners = new Map<string, string[]>()
    for (const [rowKey, sku] of finalSkuByKey) {
      if (!sku) continue
      const normalized = sku.toLocaleUpperCase('tr-TR')
      skuOwners.set(normalized, [...(skuOwners.get(normalized) ?? []), rowKey])
    }
    const conflictingKeys = new Set([...skuOwners.values()].filter(keys => keys.length > 1).flat())
    const safeRows = rowsToUpdate.filter(row => !conflictingKeys.has(row.key))
    if (!safeRows.length) {
      const message = 'Barkodlar uygulanamadı; stok kodlarında çakışma var. Önce tekrar eden barkodları düzeltin.'; setNotice(message); showFeedback('Stok Kodu Çakışması', message, 'error'); setBarcodeSkuMenuOpen(false)
      return
    }
    const safeKeys = new Set(safeRows.map(row => row.key))
    setVariantRows(rows => rows.map(row => safeKeys.has(row.key) ? { ...row, sku: row.barcode.trim() } : row))
    const skippedCount = rowsToUpdate.length - safeRows.length
    const message = skippedCount
      ? `${safeRows.length} varyanta uygulandı; ${skippedCount} satır çakışma nedeniyle korunuyor.`
      : `${safeRows.length} varyantın stok kodu barkoddan güncellendi.`
    setNotice(message); showFeedback('Stok Kodları Güncellendi', message, skippedCount ? 'info' : 'success'); setBarcodeSkuMenuOpen(false)
  }

  // Opens the confirmation for a column-menu choice; the rows change only after the user confirms it.
  // Generated codes are checked against the catalog first, so Kaydet does not refuse them: one request asks which
  // candidate numbers other variants already hold (the editor's own saved variants excluded), and a taken number moves
  // the row on to the next free one. Only a very crowded range asks again for the following numbers.
  async function planScannedVariantCodes(rows: VariantDraft[], target: VariantCodeTarget, mode: 'all' | 'missing') {
    const modelCode = form.modelCode
    const savedIds = new Set(productToEdit.data?.variants.map(variant => variant.id) ?? [])
    const excludeVariantIds = rows.map(row => row.key).filter(key => savedIds.has(key))
    const taken = new Set<string>()
    let scannedUpTo = 0
    for (let round = 0; round < 6; round++) {
      const nextUpTo = scannedUpTo + (round === 0 ? Math.min(1000, rows.length * 2 + 50) : 1000)
      const candidates = variantCodeCandidates(modelCode, scannedUpTo + 1, nextUpTo)
      const answer = await hubApi<ApiAnswer<'postCatalogVariantCodesTaken'>>('/catalog/variant-codes/taken', { method: 'POST', body: JSON.stringify({ codes: candidates, excludeVariantIds }) })
      for (const item of answer?.items ?? []) taken.add(item.code.trim().toLocaleUpperCase('tr-TR'))
      scannedUpTo = nextUpTo
      const plan = planVariantCodes(rows, target, mode, modelCode, code => taken.has(code.toLocaleUpperCase('tr-TR')), scannedUpTo)
      if (plan) return plan
    }
    throw new Error('Model kodunun numaralarının çoğu kullanılıyor; boş numara bulunamadı.')
  }

  async function requestVariantCodeChange(target: VariantCodeTarget, mode: PendingVariantCodeChange['mode']) {
    setBarcodePasteMenuOpen(false)
    setBarcodeSkuMenuOpen(false)
    const rows = variantRowsRef.current
    let codesByKey: Record<string, string> = {}
    let skipped = 0
    if (mode === 'clear') {
      codesByKey = Object.fromEntries(rows.filter(row => row[target].trim()).map(row => [row.key, '']))
    } else if (form.modelCode.trim() && rows.length && rows.length <= 1000 && (mode === 'all' || rows.some(row => !row[target].trim()))) {
      showFeedback('Kodlar Taranıyor', 'Panelde kullanılan kodlar kontrol ediliyor…', 'info', { persistent: true, key: 'variant-code-scan' })
      try {
        const plan = await planScannedVariantCodes(rows, target, mode)
        codesByKey = plan.codesByKey
        skipped = plan.skipped
        operationToasts.dismiss('variant-code-scan')
      } catch (reason) {
        const message = reason instanceof Error ? reason.message : 'Kodlar kontrol edilemedi.'
        setNotice(message); showFeedback('Kodlar Kontrol Edilemedi', `${message} Kod üretilmedi.`, 'error', { key: 'variant-code-scan' })
        return
      }
    }
    if (!Object.keys(codesByKey).length) {
      const label = target === 'barcode' ? 'barkod' : 'stok kodu'
      const message = mode === 'clear' ? `Temizlenecek ${label} yok.`
        : !form.modelCode.trim() ? 'Kod üretmek için önce model kodunu girin.'
        : mode === 'missing' ? `Eksik ${label} olan varyant yok.`
        : 'Kod üretimi için varyant sayısı 1-1000 arasında olmalıdır.'
      setNotice(message); showFeedback('Kod Değiştirilmedi', message, 'info')
      return
    }
    setPendingVariantCodeGeneration({ target, mode, codesByKey, rowCount: rows.length, skipped })
  }

  function confirmVariantCodeGeneration() {
    const request = pendingVariantCodeGeneration
    if (!request) return
    const rows = variantRowsRef.current
    const keys = Object.keys(request.codesByKey)
    const rowKeys = new Set(rows.map(row => row.key))
    const stale = rows.length !== request.rowCount || keys.some(key => !rowKeys.has(key))
      || (request.mode === 'missing' && rows.some(row => keys.includes(row.key) && row[request.target].trim()))
    if (stale) {
      const message = 'Varyantlar değiştiği için kodlara dokunulmadı. Menüyü yeniden açıp tekrar deneyin.'
      setNotice(message); showFeedback('Kod Değiştirilmedi', message, 'error'); setPendingVariantCodeGeneration(null)
      return
    }

    setVariantRows(current => applyVariantCodesByKey(current, request.target, request.codesByKey))
    const label = request.target === 'barcode' ? 'barkodu' : 'stok kodu'
    if (request.mode === 'clear') {
      const message = `${keys.length} varyantın ${label} temizlendi. Kalıcı olması için Kaydet’e basın.`
      setNotice(message); showFeedback('Kodlar Temizlendi', message, 'success'); setPendingVariantCodeGeneration(null)
      return
    }
    const firstRowKey = rows[0]?.key
    if (request.target === 'barcode' && firstRowKey && request.codesByKey[firstRowKey]) setForm(current => ({ ...current, barcode: request.codesByKey[firstRowKey] }))
    const firstCode = request.codesByKey[keys[0]]
    // A generated code that some active marketplace refuses is named.
    const ruleIssues = platformCodeIssues(codeRulePlatforms, request.target === 'barcode' ? firstCode : '', request.target === 'sku' ? firstCode : '')
    const ruleText = describePlatformCodeIssues(ruleIssues, request.target)
    const message = `${keys.length} varyantın ${label} model kodundan oluşturuldu; diğer kod alanı korundu.${request.mode === 'missing' ? ' Dolu kodlara dokunulmadı.' : ''} Kalıcı olması için Kaydet’e basın.${ruleText ? ` Uyarı: ${ruleText}.` : ''}`
    setNotice(message); showFeedback('Kodlar Oluşturuldu', message, ruleText ? 'info' : 'success'); setPendingVariantCodeGeneration(null)
  }

  async function pasteBarcodesFromClipboard(mode: 'missing' | 'all', target: VariantCodeTarget = 'barcode') {
    const label = target === 'barcode' ? 'barkod' : 'stok kodu'
    const title = target === 'barcode' ? 'Barkodlar' : 'Stok Kodları'
    const closeMenus = () => { setBarcodePasteMenuOpen(false); setBarcodeSkuMenuOpen(false) }
    if (typeof navigator.clipboard?.readText !== 'function') {
      const message = `Pano okuma bu tarayıcıda kullanılamıyor; ${target === 'barcode' ? 'barkodları' : 'stok kodlarını'} satırlara elle yapıştırın.`
      setNotice(message); showFeedback('Pano Kullanılamıyor', message, 'error'); closeMenus()
      return
    }

    let clipboardText: string
    try {
      clipboardText = await navigator.clipboard.readText()
    } catch {
      const message = 'Panoya erişilemedi. Tarayıcı pano iznini verip tekrar deneyin.'
      setNotice(message); showFeedback('Panoya Erişilemedi', message, 'error'); closeMenus()
      return
    }

    const rows = variantRowsRef.current
    const rowsToUpdate = mode === 'missing' ? rows.filter(row => !row[target].trim()) : rows
    if (!rowsToUpdate.length) {
      const message = mode === 'missing' ? `Eksik ${label} satırı bulunamadı.` : 'Önce varyant satırlarını oluşturun.'
      setNotice(message); showFeedback(target === 'barcode' ? 'Barkod Satırı Yok' : 'Stok Kodu Satırı Yok', message, 'info'); closeMenus()
      return
    }
    const values = parseBarcodeClipboardValues(clipboardText)
    const targetKeys = new Set(rowsToUpdate.map(row => row.key))
    const reservedValues = rows.filter(row => !targetKeys.has(row.key)).map(row => row[target].trim())
    const issue = barcodeClipboardIssue(values, rowsToUpdate.length, reservedValues, label)
    if (issue) {
      setNotice(issue); showFeedback(`${title} Yapıştırılamadı`, issue, 'error'); closeMenus()
      return
    }

    const valuesByKey = Object.fromEntries(rowsToUpdate.map((row, index) => [row.key, values[index].trim()]))
    setVariantRows(currentRows => applyVariantCodesByKey(currentRows, target, valuesByKey))
    const message = `${rowsToUpdate.length} varyantın ${target === 'barcode' ? 'barkodu' : 'stok kodu'} panodan yapıştırıldı.`
    setNotice(message); showFeedback(`${title} Yapıştırıldı`, message, 'success'); closeMenus()
  }

  return { applyBarcodeToSku, planScannedVariantCodes, requestVariantCodeChange, confirmVariantCodeGeneration, pasteBarcodesFromClipboard }
}
export type VariantCodeActionsResult = ReturnType<typeof useVariantCodeActions>
