import { ApiRequestError } from '../../shared/api'

// Owner, 6 Nov 2026 ("Tek Ürün ... silinmiyor"): the variant row's trash button only took the row off the table, so a
// saved variant came back after Kaydet. A saved row now goes through a confirmation and the save deletes it on the
// server (variantIdsToDelete); a refusal (order or stock history, a pending job) puts the row back with the reason.

type RemovableRow = {
  key: string
  stock?: number
  loadedStock?: number
  platformStatuses?: ReadonlyArray<{ platform: string; isLinked: boolean; isMatched?: boolean; offerId?: string | null }>
}

export type RemovedVariantRow<Row> = { row: Row; index: number }

const listFormat = (items: string[]) => items.length <= 1 ? items.join('') : `${items.slice(0, -1).join(', ')} ve ${items[items.length - 1]}`

// What the panel itself knows is tied to the saved variant; the server checks the rest (orders, stock movements, jobs).
export function variantRemovalTies(row: RemovableRow): string[] {
  const ties: string[] = []
  const platforms = row.platformStatuses ?? []
  const linked = [...new Set(platforms.filter(item => item.isLinked || item.isMatched).map(item => item.platform))]
  if (linked.length) ties.push(`${listFormat(linked)} pazaryeri bağlantısı panelden kaldırılır; pazaryerindeki ilana dokunulmaz.`)
  const offered = [...new Set(platforms.filter(item => item.offerId && !linked.includes(item.platform)).map(item => item.platform))]
  if (offered.length) ties.push(`${listFormat(offered)} platform fiyatı silinir.`)
  const stock = row.loadedStock ?? row.stock ?? 0
  if (stock > 0) ties.push(`Paneldeki ${stock.toLocaleString('tr-TR')} adet stoğu da silinir.`)
  return ties
}

export function variantRemovalConfirmText(row: RemovableRow) {
  const ties = variantRemovalTies(row)
  return ['Bu varyant Kaydet’te silinir.', ...ties, 'Siparişi veya stok hareketi olan varyantı sunucu silmez; o zaman satır geri gelir ve nedeni gösterilir.'].join(' ')
}

export const isVariantDeleteRefusal = (reason: unknown) => reason instanceof ApiRequestError && reason.code === 'VARIANT_DELETE_BLOCKED'

// The server refused the delete: the rows go back where they stood, unless the form holds them again.
export function restoreRemovedVariantRows<Row extends { key: string }>(rows: readonly Row[], removed: ReadonlyArray<RemovedVariantRow<Row>>): Row[] {
  const next = [...rows]
  for (const item of [...removed].sort((left, right) => left.index - right.index)) {
    if (next.some(row => row.key === item.row.key)) continue
    next.splice(Math.min(item.index, next.length), 0, item.row)
  }
  return next
}

export const VARIANT_DELETE_REFUSED_HINT = 'Varyant satırı geri getirildi. Satışını durdurmak için Satış Durumu’nu “Satışa Kapalı” yapıp kaydedebilirsiniz.'
