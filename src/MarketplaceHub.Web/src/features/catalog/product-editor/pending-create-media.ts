// After "Ürünü Oluştur" a later step can fail (an image upload, a variant's images): the product exists and the editor
// moves to the product's page, which is a fresh editor. The new files not stored yet and the images each variant was
// given wait here, keyed by the new product's id, and the product page takes them back as unsaved changes, so the next
// Kaydet sends them instead of losing them with the navigation.

export type PendingCreateMedia<F extends { id: string }> = {
  files: F[]
  // Per saved variant id: the media refs ('url|…', 'file|<pending id>') its images were not written with.
  variantMediaRefs: Record<string, string[]>
}

const pendingByProductId = new Map<string, PendingCreateMedia<{ id: string }>>()

export function keepPendingCreateMedia<F extends { id: string }>(productId: string, pending: PendingCreateMedia<F>) {
  if (!pending.files.length && !Object.keys(pending.variantMediaRefs).length) return
  pendingByProductId.set(productId, pending)
}

// The media kept for the product, once: they leave the store as the product page takes them.
export function takePendingCreateMedia<F extends { id: string }>(productId: string): PendingCreateMedia<F> | undefined {
  const pending = pendingByProductId.get(productId) as PendingCreateMedia<F> | undefined
  pendingByProductId.delete(productId)
  return pending
}

// A variant's media refs as the product page takes them back: a file this save stored is its stored copy, a file still
// queued stays a pending file, and a file in neither (removed meanwhile) is left out.
export function pendingVariantMediaRefs(mediaRefs: readonly string[], storedUrlByFileId: ReadonlyMap<string, string>, queuedFileIds: ReadonlySet<string>): string[] {
  return mediaRefs.flatMap(ref => {
    if (!ref.startsWith('file|')) return [ref]
    const fileId = ref.slice(5)
    const storedUrl = storedUrlByFileId.get(fileId)
    if (storedUrl) return [`url|${storedUrl}`]
    return queuedFileIds.has(fileId) ? [ref] : []
  })
}
