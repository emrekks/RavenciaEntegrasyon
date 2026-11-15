// Picture assignments saved on their own on the edit page (WEB-SAYFALAR-9): Kaydet awaits the running one.
import { useEffect, useEffectEvent } from 'react'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { pinSizeChartLast } from './size-chart-order'
import { variantMediaReconcileRequests, variantOwnerProductId } from '../variant-media-assignments'
import { queryKeys } from '../../../shared/api/query-keys'
import { editorSnapshotWithSavedMedia } from '../product-editor-state'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import type { EditorDataResult } from './use-editor-data'
import type { EditorSeedResult } from './use-editor-seed'
import type { VariantRowDragResult } from './use-variant-row-drag'

export function useVariantMediaAutoSave(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult) {
  const { editProductId, client, submitting, mediaAutoSaveTick, setMediaAutoSaveTick, variantRows, chartKey, setSnapshotRequest, setSeedSnapshot, initialEditVariantMediaRefs, showFeedback, mediaAssignmentFormWasClean, mediaAutoSave, mediaAutoSaveInFlight, editorDirty, productToEdit, adoptOwnProductWrite } = c
  // Image assignments made in the window are saved on their own right away on the edit page: only the variants'
  // pictures, never the rest of the form (unsaved prices or stock stay unsaved). The effect runs after variantRows has
  // committed so it sees the new references. New variants and pictures still to upload wait for the product save.
  const saveVariantMediaAssignments = useEffectEvent(async () => {
    const product = productToEdit.data
    if (!mediaAutoSaveTick || !editProductId || !product) return
    // D5: the size chart stays every variant's last picture here too, as on Kaydet.
    const requests = variantMediaReconcileRequests(variantRows.map(row => ({ ...row, mediaRefs: pinSizeChartLast(row.mediaRefs, chartKey) })), initialEditVariantMediaRefs.current)
    if (requests === null || submitting) {
      showFeedback('Görsel Ataması Eklendi', 'Görsel ataması forma eklendi; ürünü kaydettiğinizde kaydedilir.', 'info')
      return
    }
    if (!requests.length) return
    const formWasClean = mediaAssignmentFormWasClean.current
    mediaAutoSaveInFlight.current = true
    try {
      for (const request of requests) {
        await hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId: variantOwnerProductId(product.variants, request.variantId, product.id), variantId: request.variantId, items: request.items, altText: `${product.title} · ${request.optionSignature}` }) })
        initialEditVariantMediaRefs.current = { ...initialEditVariantMediaRefs.current, [request.variantId]: request.items.map(item => `url|${item.url}`) }
      }
      // A form with no other edits stays clean; other unsaved edits are left for the Kaydet button, the snapshot moving
      // for the saved variants' pictures only (W1).
      if (formWasClean) setSnapshotRequest(value => value + 1)
      else {
        const savedRefs = new Map(variantRows.filter(row => requests.some(request => request.variantId === row.key)).map(row => [row.key, row.mediaRefs]))
        setSeedSnapshot(current => current == null ? current : editorSnapshotWithSavedMedia(current, { rowMediaRefs: (rowKey, references) => savedRefs.get(rowKey) ?? references }))
      }
      void client.invalidateQueries({ queryKey: queryKeys.products() })
      // The product the assignments left: a clean form refills from it, one with other unsaved edits stands on it.
      const saved = (await productToEdit.refetch()).data
      mediaAutoSaveInFlight.current = false
      adoptOwnProductWrite(saved)
      showFeedback('Görsel Ataması Kaydedildi', `${requests.length} varyantın görsel ataması kaydedildi.${formWasClean ? '' : ' Formdaki diğer değişiklikler henüz kaydedilmedi.'}`, 'success')
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Görsel ataması kaydedilemedi.'
      showFeedback('Görsel Ataması Kaydedilemedi', `Görsel ataması kaydedilemedi: ${message} Atama formda duruyor; ürünü kaydettiğinizde yeniden denenir.`, 'error')
    } finally {
      mediaAutoSaveInFlight.current = false
    }
  })
  useEffect(() => {
    const running = saveVariantMediaAssignments().finally(() => { if (mediaAutoSave.current === running) mediaAutoSave.current = null })
    mediaAutoSave.current = running
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mediaAutoSaveTick])
  function requestVariantMediaSave() {
    mediaAssignmentFormWasClean.current = !editorDirty
    setMediaAutoSaveTick(tick => tick + 1)
  }

  return { requestVariantMediaSave }
}
export type VariantMediaAutoSaveResult = ReturnType<typeof useVariantMediaAutoSave>
