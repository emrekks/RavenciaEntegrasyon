// The pictures of a save, after the product is written (WEB-SAYFALAR-10): the create page's gallery, the new files'
// uploads, each variant's pictures, every colour card's gallery from the one strip, the size chart marks, the family
// picture removals and the model's order. Uploads and media writes on one product stay one at a time: each raises the
// product's version, which the server checks.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { mediaImageKey, mediaRefsEqual } from '../product-media-editor'
import { mediaSaveProgressMessage, savedMediaOrder, storedProductMediaUrl, variantMediaReconcilePlan } from './product-media-save'
import { familyGalleryPlan, familyUploadTargets, type ColourRowPictures, type FamilyCardPictures } from './family-gallery-save'
import { pinSizeChartLast } from './size-chart-order'
import { removePendingFamilyMedia } from '../family-media-clear'
import { variantOwnerProductId } from '../variant-media-assignments'
import { isColorOptionName, type Product } from '../shared'
import type { PendingProductMediaFile, ProductFamilyMediaItem, VariantDraft } from './editor-types'

// A row by its stock code, as the saved product's variants are matched to the form's rows.
export const skuKey = (sku: string) => sku.trim().toLocaleUpperCase('tr-TR')
export const rowsBySku = (rows: VariantDraft[]) => new Map(rows.map(row => [skuKey(row.sku), row]))

// What the media step got done so far, kept by the caller: a step that stops halfway leaves the files it uploaded out of
// the queue and hands the rest to the product's page (pending-create-media.ts).
export type MediaStepProgress = {
  uploadedMediaFileIds: Set<string>
  storedUrlByFileId: Map<string, string>
  writtenVariantMediaIds: Set<string>
}

export const newMediaStepProgress = (): MediaStepProgress => ({ uploadedMediaFileIds: new Set(), storedUrlByFileId: new Map(), writtenVariantMediaIds: new Set() })

export type MediaStepInput = {
  editing: boolean
  product: Product
  rows: VariantDraft[]
  title: string
  // The create page's picture links (form.mediaUrls) and the new files waiting to be uploaded.
  mediaUrls: string[]
  mediaFiles: PendingProductMediaFile[]
  // The strip as it stands (url|… and file|… refs) and the create page's own link keys.
  mediaStrip: string[]
  draftMediaKeys: ReadonlySet<string>
  pendingRemovalMediaKeys: ReadonlySet<string>
  pendingFamilyMediaRemovals: ProductFamilyMediaItem[]
  familyMediaOrderDirty: boolean
  chartKey: string | null
  sizeChartUrl: string | null
  initialSizeChartUrl: string | null
  // Each saved variant's pictures as the edit form opened.
  initialVariantMediaRefs: Record<string, string[]>
  showProgress: (message: string) => void
  // Called right after the family picture removals and the model's order are written.
  onFamilyRemovalsWritten: () => void
  onFamilyOrderWritten: () => void
}

export type MediaStepResult = {
  galleryWriteCount: number
  cardColourLabels: string[]
  familyChartMarked: boolean
  removedFamilyMediaCount: number
  familyMediaChanged: boolean
}

const reconcile = (productId: string, variantId: string | null, items: Array<{ url: string; sortOrder: number }>, altText: string) =>
  hubApi('/files/product-media-reconcile', { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ productId, variantId, items, altText }) })

const urlItems = (urls: string[]) => urls.map((url, sortOrder) => ({ url, sortOrder }))

export async function runMediaStep(input: MediaStepInput, progress: MediaStepProgress): Promise<MediaStepResult> {
  const { editing, product, rows, title, mediaUrls, mediaFiles, chartKey, sizeChartUrl } = input
  const { storedUrlByFileId, uploadedMediaFileIds, writtenVariantMediaIds } = progress
  // The create page makes one card: its pictures go to it. The edit page holds the whole model: every colour card's
  // pictures are written from the one gallery (family-gallery-save.ts), the same whichever colour row was opened.
  if (!editing && mediaUrls.length > 0) await reconcile(product.id, null, urlItems(mediaUrls), title)
  const bySku = rowsBySku(rows)
  // Every colour card of the model after the save (a new colour's card included), oldest first.
  const familyCards: FamilyCardPictures[] = [...(product.familyGalleries ?? [{ productId: product.id, mediaUrls: product.mediaUrls ?? [], variantMediaUrls: [], sizeChartUrl: input.initialSizeChartUrl }])]
  for (const variant of product.variants) {
    const cardId = variant.productId || product.id
    if (!familyCards.some(card => card.productId === cardId)) familyCards.push({ productId: cardId, mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null })
  }
  const colourRows = (resolve: (ref: string) => string | null): ColourRowPictures[] => product.variants.flatMap(variant => {
    const row = bySku.get(skuKey(variant.sku))
    return row ? [{ productId: variant.productId || product.id, refs: pinSizeChartLast(row.mediaRefs, chartKey).flatMap(ref => resolve(ref) ?? []) }] : []
  })
  // Each new file is uploaded once, on the first card of its colours; the others reference its copy.
  const uploadTargets = editing ? familyUploadTargets(mediaFiles.map(item => item.id), familyCards, colourRows(ref => ref), product.id) : new Map<string, string>()
  const uploadedByCard = new Map<string, string[]>()
  for (const [fileIndex, pendingMedia] of mediaFiles.entries()) {
    input.showProgress(mediaSaveProgressMessage('upload', fileIndex, mediaFiles.length))
    const targetId = uploadTargets.get(pendingMedia.id) ?? product.id
    const sortOrder = editing ? (familyCards.find(card => card.productId === targetId)?.mediaUrls.length ?? 0) + (uploadedByCard.get(targetId)?.length ?? 0) : mediaUrls.length + fileIndex
    const data = new FormData(); data.set('file', pendingMedia.file); data.set('productId', targetId); data.set('mediaRole', sortOrder === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(Math.min(sortOrder, 999))); data.set('altText', title)
    const uploaded = await hubApi<ApiAnswer<'postFilesProductMedia'>>('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: data })
    if (uploaded?.fileAssetId) {
      storedUrlByFileId.set(pendingMedia.id, storedProductMediaUrl(uploaded.fileAssetId))
      uploadedByCard.set(targetId, [...(uploadedByCard.get(targetId) ?? []), storedProductMediaUrl(uploaded.fileAssetId)])
    }
    uploadedMediaFileIds.add(pendingMedia.id)
  }
  // The create page: the uploads above go after the saved pictures; a new file put before them takes its place now.
  if (!editing && storedUrlByFileId.size) {
    const productMediaOrder = savedMediaOrder([...input.mediaStrip.filter(ref => ref.startsWith('file|') || input.draftMediaKeys.has(mediaImageKey(ref.slice(4)))), ...mediaUrls.map(url => `url|${url}`)], storedUrlByFileId)
    const appendedMediaOrder = savedMediaOrder([...mediaUrls.map(url => `url|${url}`), ...mediaFiles.map(item => `file|${item.id}`)], storedUrlByFileId)
    if (!mediaRefsEqual(productMediaOrder, appendedMediaOrder)) await reconcile(product.id, null, urlItems(productMediaOrder), title)
  }
  const pendingFileIds = new Set(mediaFiles.map(item => item.id))
  const variantMediaWrites = product.variants.flatMap(variant => {
    const row = bySku.get(skuKey(variant.sku))
    if (!row) return []
    const originalMediaRefs = input.initialVariantMediaRefs[variant.id]
    if (editing && originalMediaRefs && mediaRefsEqual(pinSizeChartLast(row.mediaRefs, chartKey), originalMediaRefs)) return []
    return [{ variant, row: { ...row, mediaRefs: pinSizeChartLast(row.mediaRefs, chartKey) } }]
  })
  for (const [writeIndex, { variant, row }] of variantMediaWrites.entries()) {
    if (mediaFiles.length) input.showProgress(mediaSaveProgressMessage('variants', writeIndex, variantMediaWrites.length))
    const altText = `${title} · ${row.optionSignature}`
    const plan = variantMediaReconcilePlan(row.mediaRefs, storedUrlByFileId, pendingFileIds)
    const variantProductId = variantOwnerProductId(product.variants, variant.id, product.id)
    await reconcile(variantProductId, variant.id, plan.items, altText)
    for (const upload of plan.uploads) {
      const file = mediaFiles.find(item => item.id === upload.fileId)?.file
      if (file) { const data = new FormData(); data.set('file', file); data.set('productId', variantProductId); data.set('variantId', variant.id); data.set('mediaRole', upload.sortOrder === 0 ? 'PRIMARY' : 'GALLERY'); data.set('sortOrder', String(upload.sortOrder)); data.set('altText', altText); await hubApi('/files/product-media', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: data }) }
    }
    writtenVariantMediaIds.add(variant.id)
  }
  // Every colour card's gallery from the one strip: a picture goes to the colours it is assigned to, a new one without
  // a colour and the size chart to every colour, the rest stay where they are (family-gallery-save.ts).
  const familyStripUrls = savedMediaOrder(input.mediaStrip, storedUrlByFileId)
  let galleryWriteCount = 0
  let familyChartMarked = false
  const cardColourLabels: string[] = []
  if (editing) {
    const heldKeys = new Set(familyCards.flatMap(card => [...card.mediaUrls, ...card.variantMediaUrls]).map(mediaImageKey))
    const resolveRef = (ref: string) => ref.startsWith('url|') ? ref.slice(4) : ref.startsWith('file|') ? storedUrlByFileId.get(ref.slice(5)) ?? null : null
    const galleryPlan = familyGalleryPlan({
      strip: familyStripUrls,
      cards: familyCards,
      rows: colourRows(resolveRef),
      uploaded: uploadedByCard,
      newUrls: [...storedUrlByFileId.values(), ...familyStripUrls.filter(url => !heldKeys.has(mediaImageKey(url)))],
      removedKeys: input.pendingRemovalMediaKeys,
      chart: { url: sizeChartUrl, initialUrl: input.initialSizeChartUrl }
    })
    const colourOfCard = (productId: string) => {
      const variant = product.variants.find(item => (item.productId || product.id) === productId)
      const row = variant ? bySku.get(skuKey(variant.sku)) : undefined
      return Object.entries(row?.options ?? {}).find(([name, value]) => isColorOptionName(name) && value.trim())?.[1].trim() ?? 'renk kartı'
    }
    // A size chart made with "Kullan" is on no card yet and only the card it was made for may take it first; the other
    // colours then reference that copy.
    const galleryWrites = sizeChartUrl && !heldKeys.has(mediaImageKey(sizeChartUrl))
      ? [...galleryPlan.galleryWrites].sort((left, right) => Number(right.productId === product.id) - Number(left.productId === product.id))
      : galleryPlan.galleryWrites
    for (const write of galleryWrites) {
      await reconcile(write.productId, null, urlItems(write.urls), title)
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
  if (editing && input.pendingFamilyMediaRemovals.length) {
    removedFamilyMediaCount = await removePendingFamilyMedia(input.pendingFamilyMediaRemovals, mediaIds => hubApi<ApiAnswer<'deleteFilesProductMediaItems'>>('/files/product-media-items', {
      method: 'DELETE',
      headers: { 'Idempotency-Key': newIdempotencyKey() },
      body: JSON.stringify({ mediaIds })
    }))
    input.onFamilyRemovalsWritten()
  }
  // The model's order (the strip) on every card, after a move or any picture written above: each card numbers its own
  // rows 0, 1… when written, the family order ranks them again.
  let familyMediaChanged = false
  if (editing && (input.familyMediaOrderDirty || galleryWriteCount > 0 || mediaFiles.length > 0) && familyStripUrls.length) {
    const result = await hubApi<ApiAnswer<'putFilesProductMediaFamilyReorder'>>('/files/product-media-family-reorder', {
      method: 'PUT',
      headers: { 'Idempotency-Key': newIdempotencyKey() },
      body: JSON.stringify({ productId: familyCards[0]?.productId ?? product.id, urlsInOrder: familyStripUrls })
    })
    familyMediaChanged = result.changedProductCount > 0
    input.onFamilyOrderWritten()
  }
  return { galleryWriteCount, cardColourLabels, familyChartMarked, removedFamilyMediaCount, familyMediaChanged }
}

// What the result says about the pictures, in the order it said it: the size chart, the pictures, the removals, the order.
export function mediaStepParts(input: { editing: boolean; sizeChartChanged: boolean; sizeChartUrl: string | null; mediaUrlCount: number; mediaFileCount: number }, result: MediaStepResult) {
  const { editing } = input
  const chart: string[] = editing && input.sizeChartChanged ? [input.sizeChartUrl ? 'beden tablosu' : 'beden tablosu işareti kaldırıldı'] : result.familyChartMarked ? ['beden tablosu diğer renklere'] : []
  const rest: string[] = []
  if (editing ? result.galleryWriteCount > 0 || input.mediaFileCount > 0 : input.mediaUrlCount || input.mediaFileCount) rest.push(result.cardColourLabels.length ? `renk kartlarının görselleri (${result.cardColourLabels.join(', ')})` : 'görseller')
  if (result.removedFamilyMediaCount) rest.push(`modelden kaldırılan ${result.removedFamilyMediaCount} görsel`)
  if (result.familyMediaChanged && !result.galleryWriteCount) rest.push('modelin görsel sırası')
  return { chart, rest }
}
