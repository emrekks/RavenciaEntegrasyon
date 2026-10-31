// The save after "Ürünü Oluştur" uploads every new image once, on the product, and gives each variant the stored copy
// by reference: the same file used to be uploaded again for every variant that showed it (an 18-row Beden × Renk
// product sent 72 uploads of 18 files). The server already stores one file per content hash, so the saved media
// are the same either way.

export type ReconcileItem = { url: string; sortOrder: number }
export type PendingVariantUpload = { fileId: string; sortOrder: number }

// The address the reconcile endpoint accepts for an image already stored on this tenant's products.
export function storedProductMediaUrl(fileAssetId: string) {
  return `/api/v1/files/product-media/${fileAssetId}/content`
}

// A variant's media refs ('url|…' and 'file|<pending id>') as one reconcile list: a link as it is, a pending file
// by the stored copy its product upload made. A file with no stored copy is left for an upload of its own.
export function variantMediaReconcilePlan(mediaRefs: readonly string[], storedUrlByFileId: ReadonlyMap<string, string>, knownFileIds: ReadonlySet<string>) {
  const items: ReconcileItem[] = []
  const uploads: PendingVariantUpload[] = []
  mediaRefs.forEach((mediaRef, sortOrder) => {
    if (mediaRef.startsWith('url|')) items.push({ url: mediaRef.slice(4), sortOrder })
    else if (mediaRef.startsWith('file|')) {
      const fileId = mediaRef.slice(5)
      const storedUrl = storedUrlByFileId.get(fileId)
      if (storedUrl) items.push({ url: storedUrl, sortOrder })
      else if (knownFileIds.has(fileId)) uploads.push({ fileId, sortOrder })
    }
  })
  return { items, uploads }
}

// Runs the tasks at most `limit` at a time and keeps the results in input order; the first failure is thrown
// once the tasks already started have settled.
export async function mapWithConcurrency<T, R>(items: readonly T[], limit: number, run: (item: T, index: number) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length)
  let next = 0
  let failure: { reason: unknown } | undefined
  async function worker() {
    while (!failure && next < items.length) {
      const index = next++
      try { results[index] = await run(items[index], index) } catch (reason) { failure ??= { reason } }
    }
  }
  await Promise.all(Array.from({ length: Math.max(1, Math.min(limit, items.length)) }, () => worker()))
  if (failure) throw failure.reason
  return results
}

// "Görseller yükleniyor (5/18)…" while the save still has noticeable work left.
export function mediaSaveProgressMessage(step: 'upload' | 'variants' | 'prices', done: number, total: number) {
  const label = step === 'upload' ? 'Görseller yükleniyor' : step === 'variants' ? 'Varyant görselleri kaydediliyor' : 'Kanal fiyatları kaydediliyor'
  return `${label} (${done}/${total})…`
}
