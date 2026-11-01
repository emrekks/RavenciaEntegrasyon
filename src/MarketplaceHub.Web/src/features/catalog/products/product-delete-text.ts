// The texts of a catalog delete: the confirmation names the publication and price/stock jobs it cancels, and the
// notice after it says how many records went and how many of those jobs were cancelled with them.
export type ProductDeleteResponse = { deletedCount?: number; cancelledJobCount?: number }

const count = (value: number) => value.toLocaleString('tr-TR')

export function productDeleteJobNote(pendingJobCount: number | undefined): string | null {
  if (!pendingJobCount) return null
  return `${count(pendingJobCount)} bekleyen yayın veya fiyat-stok işi iptal edilecek; pazar yerine bir şey gönderilmez.`
}

export function productDeleteResultMessage(responses: ProductDeleteResponse[], requestedCount: number): string {
  const deleted = responses.reduce((sum, response) => sum + (response.deletedCount ?? 0), 0) || requestedCount
  const cancelled = responses.reduce((sum, response) => sum + (response.cancelledJobCount ?? 0), 0)
  const records = deleted === 1 ? 'Ürün ve varyantları yerel katalogdan silindi.' : `${count(deleted)} katalog kaydı yerel katalogdan silindi.`
  return cancelled > 0 ? `${records} ${count(cancelled)} bekleyen iş iptal edildi.` : records
}
