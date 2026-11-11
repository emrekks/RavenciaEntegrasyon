import { parseTrCount, parseTrNumber } from '../../../shared/tr-number'
import { hasAtMostTwoDecimals, priceFieldText, unreadablePriceMessage } from '../quick-price-edit'

// The product editor's Toplu Düzenle: the same window as the products list's, working on the editor's unsaved variant
// rows. Nothing is saved or sent anywhere: applying changes the rows, and the editor's Kaydet saves them as usual.
export type QuickEditDraftRow = { key: string; optionSignature: string; options: Record<string, string>; sku: string; barcode: string; stock: number; salePrice: number; listPrice: number; costPrice: number | null }
export type QuickEditDraftValues = { stock: string; listPrice: string; salePrice: string; costPrice: string }
export type QuickEditDraftChange = { key: string; stock: number; listPrice: number; salePrice: number; costPrice: number | null }
export type QuickEditDraftBulk = { listPrice: string; salePrice: string; costPrice: string; stockAction: 'SET' | 'ADD' | 'SUBTRACT'; stockAmount: string }

const priceText = (value: number | null) => value == null || !Number.isFinite(value) || value === 0 ? '' : priceFieldText(value)
// A row's cells as text: an empty price (0, a new row) and an empty cost stay empty, never "0".
export function quickEditDraftValues(row: QuickEditDraftRow): QuickEditDraftValues {
  return { stock: String(row.stock ?? 0), listPrice: priceText(row.listPrice), salePrice: priceText(row.salePrice), costPrice: row.costPrice == null ? '' : priceFieldText(row.costPrice) }
}

type Read = { ok: true; value: number | null } | { ok: false; message: string }
function readPrice(text: string, label: string, allowZero: boolean): Read {
  if (text.trim() === '') return { ok: true, value: null }
  const value = parseTrNumber(text)
  if (value == null) return { ok: false, message: `${label}: ${unreadablePriceMessage}` }
  if (value < 0 || (!allowZero && value === 0)) return { ok: false, message: `${label} ${allowZero ? 'negatif olamaz' : 'sıfırdan büyük olmalıdır'}.` }
  if (!hasAtMostTwoDecimals(value)) return { ok: false, message: `${label} en fazla iki ondalık basamak içerebilir.` }
  return { ok: true, value }
}
function readCount(text: string): Read {
  if (text.trim() === '') return { ok: true, value: null }
  const count = parseTrCount(text)
  if (!count.ok) return { ok: false, message: count.reason === 'fraction' ? 'Stok tam sayı olmalıdır.' : count.reason === 'negative' ? 'Stok negatif olamaz.' : 'Stok okunamadı; 1.250 gibi bir tam sayı girin.' }
  return { ok: true, value: count.value }
}

// The rows' new values: each row's cells as typed, then the bulk values on the selected rows (Liste, Satış and
// Maliyet replace; Stok is set, added or subtracted). A price is above 0 and the list price is not below the sale
// price; a list price left empty takes the sale price. A cost left empty stays unknown (null), never 0. The first
// problem stops it all (nothing is applied), named with the variant. Only the rows that changed are returned.
export function applyQuickEditDraft(rows: readonly QuickEditDraftRow[], values: Readonly<Record<string, QuickEditDraftValues>>, selected: ReadonlySet<string>, bulk: QuickEditDraftBulk, labelOf: (row: QuickEditDraftRow) => string = row => row.sku || row.key): { ok: true; changes: QuickEditDraftChange[] } | { ok: false; message: string } {
  const bulkList = readPrice(bulk.listPrice, 'Liste fiyatı', false); if (!bulkList.ok) return bulkList
  const bulkSale = readPrice(bulk.salePrice, 'Satış fiyatı', false); if (!bulkSale.ok) return bulkSale
  const bulkCost = readPrice(bulk.costPrice, 'Maliyet', true); if (!bulkCost.ok) return bulkCost
  const bulkAmount = readCount(bulk.stockAmount); if (!bulkAmount.ok) return bulkAmount
  const changes: QuickEditDraftChange[] = []
  for (const row of rows) {
    const cells = values[row.key] ?? quickEditDraftValues(row)
    const label = labelOf(row)
    const fail = (message: string) => ({ ok: false as const, message: `${label}: ${message}` })
    const stock = readCount(cells.stock); if (!stock.ok) return fail(stock.message)
    const list = readPrice(cells.listPrice, 'Liste fiyatı', false); if (!list.ok) return fail(list.message)
    const sale = readPrice(cells.salePrice, 'Satış fiyatı', false); if (!sale.ok) return fail(sale.message)
    const cost = readPrice(cells.costPrice, 'Maliyet', true); if (!cost.ok) return fail(cost.message)
    let nextStock = stock.value ?? row.stock
    let nextList = list.value
    let nextSale = sale.value
    let nextCost = cost.value
    if (selected.has(row.key)) {
      if (bulkSale.value != null) nextSale = bulkSale.value
      if (bulkList.value != null) nextList = bulkList.value
      if (bulkCost.value != null) nextCost = bulkCost.value
      if (bulkAmount.value != null) {
        nextStock = bulk.stockAction === 'ADD' ? nextStock + bulkAmount.value : bulk.stockAction === 'SUBTRACT' ? nextStock - bulkAmount.value : bulkAmount.value
        if (nextStock < 0) return fail('stok sıfırın altına inemez.')
      }
    }
    // A row left without prices keeps them empty (0) as it came: the editor's own checks ask for them on Kaydet.
    const finalSale = nextSale ?? (row.salePrice > 0 ? row.salePrice : 0)
    const finalList = nextList ?? (cells.listPrice.trim() === '' && finalSale > 0 ? finalSale : row.listPrice)
    if (finalSale > 0 && finalList > 0 && finalList < finalSale) return fail('liste fiyatı satış fiyatından küçük olamaz.')
    if (finalSale === 0 && finalList > 0) return fail('satış fiyatı girin; sıfır satış fiyatı kaydedilmez.')
    const change = { key: row.key, stock: nextStock, listPrice: finalList, salePrice: finalSale, costPrice: nextCost }
    if (change.stock !== row.stock || change.listPrice !== row.listPrice || change.salePrice !== row.salePrice || change.costPrice !== row.costPrice) changes.push(change)
  }
  return { ok: true, changes }
}
