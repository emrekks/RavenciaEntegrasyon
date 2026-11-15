// The Görseller strip: adding files, the size chart, reordering (also by touch), removing own and family pictures.
import { type PointerEvent as ReactPointerEvent } from 'react'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { mediaImageKey, mediaRefsEqual, mediaUrlsInPreferredOrder, reorderItems } from '../product-media-editor'
import { PRODUCT_IMAGE_ACCEPT, toProductImage } from './product-image-file'
import { pinSizeChartLast, replaceSizeChart, sizeChartKey } from './size-chart-order'
import { historyStateWithCopyRemovedMedia, withCopyRemovedMedia } from '../product-copy-media-removals'
import { clearAllMediaFeedbackText, familyMediaClearPlan, withPendingFamilyMediaRemovals } from '../family-media-clear'
import { editorDataStamp, editorSeedKey, editorSnapshotWithSavedMedia } from '../product-editor-state'
import { Product } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { ProductFamilyMediaItem } from './editor-types'
import { scrollingParent } from './editor-helpers'
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

export function useEditorMediaActions(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult & VariantCodeActionsResult) {
  const { editProductId, client, duplicateProductId, copyRemovedMedia, setNotice, submitting, form, setVariantRows, initializedEditProductKey, mediaFiles, setMediaFiles, setDeletingProductMediaKey, mediaRemovalConfirmation, setMediaRemovalConfirmation, pendingFamilyMediaRemovals, setPendingFamilyMediaRemovals, setFamilyMediaOrder, setFamilyMediaOrderDirty, setDraggedMedia, setDragOverMedia, setMediaStripOrder, sizeChartUrl, setSizeChartUrl, initialSizeChartUrl, sizeChartBeforeFamilyRemoval, setSizeChartPicking, chartKey, setSnapshotRequest, setSeedSnapshot, mediaFileInputRef, initialEditMediaUrl, initialEditVariantMediaRefs, showFeedback, updateField, mediaTouch, suppressMediaClick, editorDirty, productToEdit, mediaUrls, adoptOwnProductWrite, standOnOwnWrite, visibleMediaUrls, mediaStrip, familyOnlyMediaItems, hasMediaTiles } = c
  // A WebP, or a JPEG/PNG over 6 MB, is turned into a JPEG here (product-image-file.ts) instead of being refused.
  async function handleMediaFiles(files: File[]) {
    const accepted: File[] = []
    const rejected: string[] = []
    let converted = 0
    for (const file of files) {
      const result = await toProductImage(file)
      if ('error' in result) { rejected.push(result.error); continue }
      if (result.converted) converted++
      accepted.push(result.file)
    }
    if (accepted.length) setMediaFiles(current => [...current, ...accepted.map(file => ({ id: crypto.randomUUID(), file }))])
    if (rejected.length) {
      const message = rejected.length === 1 ? rejected[0] : `${rejected.length} görsel eklenemedi. Dosya türü ve 6 MB sınırını kontrol edin.`
      setNotice(message); showFeedback('Görsel Eklenemedi', message, 'error')
    } else if (accepted.length) {
      const message = `${accepted.length} görsel seçildi.${converted ? ` ${converted} görsel JPEG'e çevrildi.` : ''}`; setNotice(message); showFeedback('Görsel Seçildi', message, 'info')
    }
  }
  // "Kullan" in the size chart window: the new chart replaces the previous one as the card's last picture and as the last
  // picture of every variant that has pictures of its own (a variant without any shows the card's, chart included).
  // Nothing goes to a marketplace here; Kaydet writes it.
  function applySizeChart(url: string) {
    const previousKey = chartKey
    const nextKey = sizeChartKey(url)
    updateField('mediaUrls', replaceSizeChart(form.mediaUrls.split(/\r?\n|[;|]/).map(item => item.trim()).filter(Boolean), previousKey, url, false).join('\n'))
    setVariantRows(rows => rows.map(row => row.mediaRefs.length === 0 ? row : { ...row, mediaRefs: replaceSizeChart(row.mediaRefs, previousKey, url, true) }))
    if (previousKey && previousKey !== nextKey) setFamilyMediaOrder(current => current.filter(item => mediaImageKey(item) !== previousKey))
    setSizeChartUrl(url)
    showFeedback('Beden Tablosu Eklendi', 'Beden tablosu son görsel olarak eklendi. Kalıcı olması ve pazaryerlerine gitmesi için kaydedin.', 'info')
  }
  // "Beden Tablosu Seç" and a tap on one of the card's own pictures (an older chart uploaded by hand): it becomes the
  // last picture everywhere. Marking another one moves the mark; nothing is ever recognised on its own.
  function markSizeChart(url: string | null) {
    setSizeChartPicking(false)
    if (url) setVariantRows(rows => rows.map(row => row.mediaRefs.length === 0 ? row : { ...row, mediaRefs: replaceSizeChart(row.mediaRefs, null, url, true, false) }))
    setSizeChartUrl(url)
    showFeedback(url ? 'Beden Tablosu İşaretlendi' : 'Beden Tablosu İşareti Kaldırıldı', url ? 'Görsel her varyantta son sıraya alındı. Kalıcı olması için kaydedin.' : 'Görsel artık sona sabitlenmiyor. Kalıcı olması için kaydedin.', 'info')
  }
  // The strip is one list: a new file moves among the saved pictures (and the family's) like any other tile.
  function reorderMedia(sourceIndex: number, targetIndex: number) {
    // The size chart cannot leave the end, and nothing can go after it.
    const next = pinSizeChartLast(reorderItems(mediaStrip, sourceIndex, targetIndex), chartKey)
    setDraggedMedia(null); setDragOverMedia(null)
    if (mediaRefsEqual(next, mediaStrip)) return
    if (next === mediaStrip) return
    setMediaStripOrder(next)
    const nextUrls = next.filter(ref => ref.startsWith('url|')).map(ref => ref.slice(4))
    const urlsMoved = !mediaRefsEqual(nextUrls, visibleMediaUrls)
    const filesMovedAmongPictures = next.some((ref, index) => ref.startsWith('file|') !== mediaStrip[index].startsWith('file|'))
    if (urlsMoved) {
      setFamilyMediaOrder(nextUrls)
      const orderedProductMediaUrls = mediaUrlsInPreferredOrder(mediaUrls, nextUrls)
      if (!mediaRefsEqual(orderedProductMediaUrls, mediaUrls)) updateField('mediaUrls', orderedProductMediaUrls.join('\n'))
    }
    if (urlsMoved || filesMovedAmongPictures) setFamilyMediaOrderDirty(Boolean(editProductId))
    const filesById = new Map(mediaFiles.map(item => [item.id, item]))
    const nextFiles = next.flatMap(ref => filesById.get(ref.startsWith('file|') ? ref.slice(5) : '') ?? [])
    if (!mediaRefsEqual(nextFiles.map(item => item.id), mediaFiles.map(item => item.id))) setMediaFiles(nextFiles)
    showFeedback('Görsel Sırası Değişti', editProductId && (urlsMoved || filesMovedAmongPictures) ? 'Görsel sırası diğer renklerle birlikte Kaydet’te uygulanacak.' : 'Görsel sırası güncellendi. Kalıcı olması için kaydedin.', 'info')
  }
  function startMediaTouch(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.pointerType === 'mouse' || submitting) return
    const target = event.target as Element
    const card = target.closest<HTMLElement>('[data-media-index]')
    if (!card || target.closest('button')) return
    const index = Number(card.dataset.mediaIndex)
    const state = { index, target: index, x: event.clientX, y: event.clientY, lastY: event.clientY, timer: 0, dragging: false, scroller: null } as NonNullable<typeof mediaTouch.current>
    state.timer = window.setTimeout(() => {
      state.dragging = true
      setDraggedMedia(index)
      setDragOverMedia(index)
      navigator.vibrate?.(12)
    }, 280)
    mediaTouch.current = state
  }
  function moveMediaTouch(event: ReactPointerEvent<HTMLDivElement>) {
    const state = mediaTouch.current
    if (!state) return
    if (!state.dragging) {
      // The pictures take no browser gestures on phones (they would cancel the hold), so a finger that moves before the
      // hold completes scrolls the page by hand.
      if (state.scroller || Math.hypot(event.clientX - state.x, event.clientY - state.y) > 8) {
        window.clearTimeout(state.timer)
        state.scroller ??= scrollingParent(event.currentTarget)
        state.scroller.scrollBy(0, state.lastY - event.clientY)
        state.lastY = event.clientY
      }
      return
    }
    const card = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-media-index]')
    if (!card) return
    state.target = Number(card.dataset.mediaIndex)
    setDragOverMedia(state.target)
  }
  function endMediaTouch() {
    const state = mediaTouch.current
    if (!state?.dragging) { cancelMediaTouch(); return }
    mediaTouch.current = null
    suppressMediaClick.current = true
    window.setTimeout(() => { suppressMediaClick.current = false }, 400)
    reorderMedia(state.index, state.target)
    setDraggedMedia(null); setDragOverMedia(null)
  }
  function cancelMediaTouch() {
    const state = mediaTouch.current
    if (!state) return
    window.clearTimeout(state.timer)
    mediaTouch.current = null
    if (state.dragging) { setDraggedMedia(null); setDragOverMedia(null) }
  }
  // An uploaded image not saved yet leaves the variants that were given it too; their image count kept it before.
  function removeUploadedMedia(id: string) {
    setMediaFiles(files => files.filter(file => file.id !== id))
    setVariantRows(rows => rows.map(row => row.mediaRefs.includes(`file|${id}`) ? { ...row, mediaRefs: row.mediaRefs.filter(reference => reference !== `file|${id}`) } : row))
  }
  // A copy has nothing saved to delete: the removal is written into the page's history entry at once, so it holds after
  // a reload, and the form stays clean when it had no other edits (nothing waits for Kaydet).
  function rememberCopyRemovedMedia(urls: string[]) {
    if (editProductId || !duplicateProductId || !urls.length) return
    copyRemovedMedia.current = withCopyRemovedMedia(copyRemovedMedia.current, urls)
    try { window.history.replaceState(historyStateWithCopyRemovedMedia(window.history.state, copyRemovedMedia.current), '') } catch { /* the in-memory list still holds */ }
    if (!editorDirty) setSnapshotRequest(value => value + 1)
  }
  // On the edit page the strip also shows the same model's other colours; clearing them is asked first, since Kaydet
  // removes them from those products too.
  function clearAllMedia(confirmed = false) {
    const plan = editProductId ? familyMediaClearPlan(familyOnlyMediaItems) : { removable: [], keptCount: 0 }
    if (!confirmed && plan.removable.length) {
      setMediaRemovalConfirmation({ kind: 'all', ownCount: mediaUrls.length + mediaFiles.length, familyItems: plan.removable, keptCount: plan.keptCount })
      return
    }
    if (plan.removable.length) setPendingFamilyMediaRemovals(current => withPendingFamilyMediaRemovals(current, plan.removable))
    const ownCount = mediaUrls.length + mediaFiles.length
    rememberCopyRemovedMedia(mediaUrls)
    setMediaFiles([])
    updateField('mediaUrls', '')
    setFamilyMediaOrder([])
    setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: [] })))
    // D5: with its picture gone the card has no size chart; Kaydet clears the mark.
    setSizeChartUrl(null)
    setDraggedMedia(null)
    setDragOverMedia(null)
    showFeedback('Görseller Temizlendi', duplicateProductId && !editProductId ? 'Görseller kopyadan kaldırıldı. Kaynak ürünün görselleri değişmez.' : clearAllMediaFeedbackText(ownCount, plan.removable.length, plan.keptCount), plan.keptCount ? 'error' : 'info')
  }
  async function removeProductMedia(url: string) {
    const targetKey = mediaImageKey(url)
    const savedMediaUrls = initialEditMediaUrl.current.split(/\r?\n|[;|]/u).map(item => item.trim()).filter(Boolean)
    const isPersisted = Boolean(editProductId && productToEdit.data && savedMediaUrls.some(item => mediaImageKey(item) === targetKey))
    const nextDraftMediaUrls = mediaUrls.filter(item => mediaImageKey(item) !== targetKey)
    const removeVariantReference = (reference: string) => !reference.startsWith('url|') || mediaImageKey(reference.slice(4)) !== targetKey
    // D5: the size chart's tile taken away takes the mark with it (Kaydet clears it on the server; a saved picture's
    // delete clears it there at once).
    const removesSizeChart = Boolean(chartKey) && chartKey === targetKey

    if (!isPersisted || !editProductId || !productToEdit.data) {
      if (removesSizeChart) setSizeChartUrl(null)
      updateField('mediaUrls', nextDraftMediaUrls.join('\n'))
      setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: row.mediaRefs.filter(removeVariantReference) })))
      if (duplicateProductId && !editProductId) {
        rememberCopyRemovedMedia([url])
        showFeedback('Görsel Kaldırıldı', 'Görsel kopyadan kaldırıldı. Kaynak ürünün görselleri değişmez.', 'success')
        return
      }
      showFeedback('Görsel Kaldırıldı', 'Görsel taslaktan kaldırıldı. Kalıcı olması için Kaydet’e basın.', 'info')
      return
    }

    // One saved image leaves at once, without a question (owner, 5 Nov 2026); clearing all images still asks.
    setDeletingProductMediaKey(targetKey)
    try {
      const product = productToEdit.data
      const result = await hubApi<{ deletedCount: number; version: number; productVersions: Record<string, number> }>('/files/product-media-item', {
        method: 'DELETE',
        headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${product.version}"` },
        body: JSON.stringify({ productId: product.id, url })
      })
      initialEditMediaUrl.current = savedMediaUrls.filter(item => mediaImageKey(item) !== targetKey).join('\n')
      initialEditVariantMediaRefs.current = Object.fromEntries(Object.entries(initialEditVariantMediaRefs.current).map(([variantId, references]) => [variantId, references.filter(removeVariantReference)]))
      updateField('mediaUrls', nextDraftMediaUrls.join('\n'))
      setVariantRows(rows => rows.map(row => ({ ...row, mediaRefs: row.mediaRefs.filter(removeVariantReference) })))
      setFamilyMediaOrder(current => current.filter(item => mediaImageKey(item) !== targetKey))
      if (removesSizeChart) {
        initialSizeChartUrl.current = null
        setSizeChartUrl(null)
      }
      // The deletion is saved already: a form that had no other edits stays "clean" afterwards; one with other edits
      // keeps them unsaved, its snapshot losing the removed picture only (W1).
      if (!editorDirty) setSnapshotRequest(value => value + 1)
      else setSeedSnapshot(current => current == null ? current : editorSnapshotWithSavedMedia(current, {
        mediaUrls: value => value.split(/\r?\n/u).filter(item => mediaImageKey(item) !== targetKey).join('\n'),
        rowMediaRefs: (_rowKey, references) => references.filter(removeVariantReference),
        familyMediaOrder: order => order.filter(item => mediaImageKey(item) !== targetKey),
        ...(removesSizeChart ? { sizeChartUrl: () => null } : {})
      }))

      const cachedProduct = client.getQueryData<Product>(queryKeys.product(product.id)) ?? product
      const cachedMediaUrls = (cachedProduct.mediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey)
      const productVersion = result.productVersions[product.id] ?? result.version
      const removalKey = editorSeedKey('edit', product.id, productVersion, editorDataStamp(cachedProduct.variants))
      if (editorDirty) standOnOwnWrite(removalKey)
      else initializedEditProductKey.current = removalKey
      client.setQueryData<Product>(queryKeys.product(product.id), {
        ...cachedProduct,
        version: productVersion,
        mediaUrls: cachedMediaUrls,
        primaryImageUrl: cachedProduct.primaryImageUrl && mediaImageKey(cachedProduct.primaryImageUrl) === targetKey ? cachedMediaUrls[0] ?? null : cachedProduct.primaryImageUrl,
        familyMediaUrls: (cachedProduct.familyMediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey),
        familyMediaItems: (cachedProduct.familyMediaItems ?? []).filter(item => mediaImageKey(item.url) !== targetKey),
        familyOrderedMediaUrls: cachedProduct.familyOrderedMediaUrls?.filter(item => mediaImageKey(item) !== targetKey),
        variants: cachedProduct.variants.map(variant => ({ ...variant, mediaUrls: (variant.mediaUrls ?? []).filter(item => mediaImageKey(item) !== targetKey) }))
      })
      adoptOwnProductWrite((await productToEdit.refetch()).data)
      await client.invalidateQueries({ queryKey: queryKeys.products() })
      showFeedback('Görsel Kaldırıldı', `${result.deletedCount} görsel bağlantısı kaldırıldı ve ürün yenilendi.`, 'success')
    } catch (reason) {
      showFeedback('Görsel Kaldırılamadı', reason instanceof Error ? reason.message : 'Görsel kaldırılamadı.', 'error')
    } finally {
      setDeletingProductMediaKey(null)
    }
  }
  function removeFamilyMedia(item: ProductFamilyMediaItem) {
    if (item.mediaIds.length === 0) {
      showFeedback('Görsel Kaldırılamadı', 'Görselin kaynak bağlantıları yüklenemedi. Sayfayı yenileyip tekrar deneyin.', 'error')
      return
    }
    // No question each time: the image leaves the strip now and is removed from its colour cards on Kaydet; the
    // action bar counts what waits and takes it back with Geri Al.
    setPendingFamilyMediaRemovals(current => withPendingFamilyMediaRemovals(current, [item]))
    // The size chart leaves with its picture (scan, 13 Nov 2026: a removed family chart stayed marked and went to every
    // variant); Geri Al marks it again.
    if (sizeChartUrl && mediaImageKey(item.url) === mediaImageKey(sizeChartUrl)) {
      sizeChartBeforeFamilyRemoval.current = sizeChartUrl
      setSizeChartUrl(null)
    }
    showFeedback('Aile Görseli Kaldırıldı', `Görsel şeritten kaldırıldı; Kaydet’e bastığınızda ${item.sourceProductTitles.length ? item.sourceProductTitles.join(', ') : 'diğer renk ürünleri'} kayıtlarından da kaldırılır. Vazgeçmek için alttaki Geri Al’ı kullanın.`, 'info')
  }
  function undoPendingFamilyMediaRemovals() {
    const count = pendingFamilyMediaRemovals.length
    setPendingFamilyMediaRemovals([])
    if (sizeChartBeforeFamilyRemoval.current) {
      setSizeChartUrl(sizeChartBeforeFamilyRemoval.current)
      sizeChartBeforeFamilyRemoval.current = null
    }
    if (count) showFeedback('Aile Görselleri Geri Alındı', `${count} renk ailesi görseli şeride geri getirildi.`, 'info')
  }
  function confirmMediaRemoval() {
    const confirmation = mediaRemovalConfirmation
    setMediaRemovalConfirmation(null)
    if (!confirmation) return
    if (confirmation.kind === 'all') clearAllMedia(true)
    else void removeProductMedia(confirmation.url)
  }
  const mediaFileInput = <input ref={mediaFileInputRef} type="file" accept={PRODUCT_IMAGE_ACCEPT} multiple tabIndex={hasMediaTiles ? -1 : undefined} aria-label="Ürün görsellerini dosya olarak seç" onChange={event => { void handleMediaFiles(Array.from(event.target.files ?? [])); event.currentTarget.value = '' }} />
  return { handleMediaFiles, applySizeChart, markSizeChart, reorderMedia, startMediaTouch, moveMediaTouch, endMediaTouch, cancelMediaTouch, removeUploadedMedia, rememberCopyRemovedMedia, clearAllMedia, removeProductMedia, removeFamilyMedia, undoPendingFamilyMediaRemovals, confirmMediaRemoval, mediaFileInput }
}
export type EditorMediaActionsResult = ReturnType<typeof useEditorMediaActions>
