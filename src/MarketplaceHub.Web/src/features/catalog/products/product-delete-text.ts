// The texts of a catalog delete: the confirmation names the publication and price/stock jobs it cancels, and the
// notice after it says how many records went and how many of those jobs were cancelled with them.
export type ProductDeleteResponse = { deletedCount?: number; cancelledJobCount?: number }

const count = (value: number) => value.toLocaleString('tr-TR')

export function productDeleteJobNote(pendingJobCount: number | undefined): string | null {
  if (!pendingJobCount) return null
  return `${count(pendingJobCount)} bekleyen yayın veya fiyat-stok işi iptal edilecek; pazar yerine bir şey gönderilmez.`
}

// The records a delete confirmation names, so the person sees what goes before confirming (owner, 1 Nov 2026: real
// products were deleted by mistake): the first ones by name and model code, the rest as "ve N ürün daha". A record
// the page cannot name is still counted.
export type ProductDeleteItem = { id: string; title: string; modelCode: string | null }
export function productDeleteListing(productIds: readonly string[], lookup: (id: string) => { title?: string | null; modelCode?: string | null; variants?: ReadonlyArray<{ modelCode?: string | null }> } | undefined, limit = 10): { shown: ProductDeleteItem[]; moreCount: number } {
  const shown = productIds.slice(0, limit).map(id => {
    const product = lookup(id)
    const modelCode = product?.modelCode?.trim() || product?.variants?.find(variant => variant.modelCode?.trim())?.modelCode?.trim() || null
    return { id, title: product?.title?.trim() || 'Adı okunamayan kayıt', modelCode }
  })
  return { shown, moreCount: Math.max(0, productIds.length - shown.length) }
}

export function productDeleteMoreText(moreCount: number) {
  return moreCount > 0 ? `ve ${count(moreCount)} ürün daha` : null
}

export function productDeleteResultMessage(responses: ProductDeleteResponse[], requestedCount: number): string {
  const deleted = responses.reduce((sum, response) => sum + (response.deletedCount ?? 0), 0) || requestedCount
  const cancelled = responses.reduce((sum, response) => sum + (response.cancelledJobCount ?? 0), 0)
  const records = deleted === 1 ? 'Ürün ve varyantları yerel katalogdan silindi.' : `${count(deleted)} katalog kaydı yerel katalogdan silindi.`
  return cancelled > 0 ? `${records} ${count(cancelled)} bekleyen iş iptal edildi.` : records
}
