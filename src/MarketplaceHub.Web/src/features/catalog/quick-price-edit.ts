// Where a quick price change on the products list goes: the offer the variant has on the channel the person picked
// (at the version the list loaded), or a new offer on that channel. The channel is always the person's choice; the
// screen never prices a channel it picked by itself.

export type QuickPriceOffer = {
  offerId: string; offerVersion: number | null
  listPrice?: number | null; salePrice?: number | null; currency?: string | null; vatRate?: number | null
  vatInclusion?: string | null; roundingMode?: string | null; safetyStock?: number | null; offerStatus?: string | null
}

export type QuickPriceTarget =
  | { kind: 'update'; offerId: string; offerVersion: number; offer: QuickPriceOffer }
  | { kind: 'create'; connectionId: string }
  | { kind: 'blocked'; reason: string }

type QuickPriceVariant = {
  platformStatuses?: ReadonlyArray<{ connectionId?: string | null; offerId?: string | null; offerVersion?: number | null; listPrice?: number | null; salePrice?: number | null; currency?: string | null; vatRate?: number | null; vatInclusion?: string | null; roundingMode?: string | null; safetyStock?: number | null; offerStatus?: string | null }>
}

// Marketplaces that take a sale price only (Hepsiburada's inventory upload and PTT AVM's price send carry no list
// price): their quick edit hides Liste Fiyatı, and the offer's list price is kept equal to its sale price.
const SALE_PRICE_ONLY_PLATFORMS = new Set(['HEPSIBURADA', 'PTTAVM'])
export function channelTakesListPrice(platformCode: string | null | undefined) {
  return !SALE_PRICE_ONLY_PLATFORMS.has((platformCode ?? '').trim().toUpperCase().replace(/\s/g, ''))
}

// The offer body a quick edit sends: the channel offer's own settings kept (its status too, so a passive offer is not
// switched on), the variant's for a channel that gets its first offer.
export function quickPriceBody(target: Extract<QuickPriceTarget, { kind: 'update' | 'create' }>, variant: { id: string; currency: string | null; vatRate: number | null; vatInclusion: string | null; roundingMode: string | null; safetyStock: number | null }, prices: { listPrice: number; salePrice: number }, reason: string) {
  const source = target.kind === 'update' ? target.offer : null
  return {
    connectionId: target.kind === 'create' ? target.connectionId : '', variantId: variant.id, listPrice: prices.listPrice, salePrice: prices.salePrice,
    currency: source?.currency || variant.currency || 'TRY', vatRate: source?.vatRate ?? variant.vatRate ?? 10, vatInclusion: source?.vatInclusion || variant.vatInclusion || 'INCLUDED',
    roundingMode: source?.roundingMode || variant.roundingMode || 'HALF_EVEN', safetyStock: source?.safetyStock ?? variant.safetyStock ?? 0, status: source?.offerStatus || 'ACTIVE', reason,
  }
}

// The variant's offer on one channel, read from its per-channel statuses; null when it has none there.
export function channelOffer(variant: QuickPriceVariant, connectionId: string): QuickPriceOffer | null {
  const status = variant.platformStatuses?.find(item => item.connectionId === connectionId && item.offerId)
  return status?.offerId ? { ...status, offerId: status.offerId, offerVersion: status.offerVersion ?? null } : null
}

export function quickPriceTarget(variant: QuickPriceVariant, connectionId: string, connectionCount: number): QuickPriceTarget {
  if (!connectionCount) return { kind: 'blocked', reason: 'Fiyat için aktif platform bağlantısı yok.' }
  if (!connectionId) return { kind: 'blocked', reason: 'Önce Fiyat Kanalı seçin.' }
  // Without the per-channel statuses it is unknown whether the channel already has an offer: a new one could clash.
  if (!variant.platformStatuses) return { kind: 'blocked', reason: 'Platform fiyatları okunamadı; listeyi yenileyip tekrar deneyin.' }
  const offer = channelOffer(variant, connectionId)
  if (!offer) return { kind: 'create', connectionId }
  if (offer.offerVersion == null) return { kind: 'blocked', reason: 'Fiyat sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }
  return { kind: 'update', offerId: offer.offerId, offerVersion: offer.offerVersion, offer }
}

// The prices a bulk edit sends for one variant's offer. A value left empty keeps the offer's own; an offer without a
// sale price (none yet on that channel) takes the list price typed, which every marketplace accepts
// (listPrice >= salePrice: Trendyol, n11, Pazarama and Shopify take them equal, Hepsiburada and PTT AVM send the sale
// price only). The caller warns about each defaulted variant. A sale price of zero is never sent.
export type QuickEditOfferPrices =
  | { ok: true; listPrice: number; salePrice: number; saleDefaulted: boolean }
  | { ok: false; message: string }

export function quickEditOfferPrices(current: { listPrice?: number | null; salePrice?: number | null }, list: number | null, sale: number | null): QuickEditOfferPrices {
  const currentSale = current.salePrice != null && current.salePrice > 0 ? current.salePrice : null
  const saleDefaulted = sale == null && currentSale == null && list != null
  const nextSale = sale ?? currentSale ?? list
  if (nextSale == null || !(nextSale > 0)) return { ok: false, message: 'satış fiyatı girin; sıfır satış fiyatı kaydedilmez.' }
  const nextList = list ?? current.listPrice ?? nextSale
  if (nextList < nextSale) return { ok: false, message: 'liste fiyatı satış fiyatından küçük olamaz.' }
  return { ok: true, listPrice: nextList, salePrice: nextSale, saleDefaulted }
}

// "Panel Ana Fiyatı" in the Fiyat Kanalı select: the product card's own price (the editor's Liste/Satış Fiyatı), not a
// marketplace offer. It is the preset choice because it sends nothing to a marketplace.
export const PANEL_PRICE_CHOICE = 'PANEL'

type PanelPriceSource = { defaultListPrice?: number | null; defaultSalePrice?: number | null }
// The variant's own price fields as the product list sends them: its first active channel offer's (oldest first).
type VariantOfferPriceSource = PanelPriceSource & { listPrice?: number | null; salePrice?: number | null }

// The panel price of a variant exactly as the product editor's variant table shows it (Fiyat / Liste Fiyatı): the
// variant's own, else the card's, else — for a card that never had a panel price, such as one imported from a
// marketplace — the variant's first active channel offer. Both screens use this, so they never show different values.
export function panelPrices(variant: VariantOfferPriceSource, product: PanelPriceSource): { listPrice: number | null; salePrice: number | null } {
  const salePrice = variant.defaultSalePrice ?? product.defaultSalePrice ?? variant.salePrice ?? null
  return { listPrice: variant.defaultListPrice ?? product.defaultListPrice ?? variant.listPrice ?? variant.salePrice ?? null, salePrice }
}

// True when no panel price is saved for the variant or its card, so the value shown is the first channel offer's.
export function panelPriceIsFallback(variant: VariantOfferPriceSource, product: PanelPriceSource) {
  const saved = (variant.defaultSalePrice ?? product.defaultSalePrice) != null || (variant.defaultListPrice ?? product.defaultListPrice) != null
  return !saved && (variant.salePrice != null || variant.listPrice != null)
}

// One price column of the quick edit matrix: the panel price first, then every marketplace connection in the order
// the panel lists them. A sale-price-only marketplace (Hepsiburada, PTT AVM) has a single Satış field.
export type QuickPriceColumn = { id: string; label: string; platformCode: string | null; takesListPrice: boolean }
export function quickPriceColumns(connections: ReadonlyArray<{ id: string; displayName?: string | null; platformCode?: string | null }>): QuickPriceColumn[] {
  return [
    { id: PANEL_PRICE_CHOICE, label: 'Panel Fiyatı', platformCode: null, takesListPrice: true },
    ...connections.map(connection => ({ id: connection.id, label: connection.displayName?.trim() || connection.platformCode || 'Kanal', platformCode: connection.platformCode ?? null, takesListPrice: channelTakesListPrice(connection.platformCode) })),
  ]
}

// The grid columns of a matrix row (and its header): select box, variant, stock, then one cell per price column.
export function quickEditGridTemplate(columns: ReadonlyArray<QuickPriceColumn>, showStock: boolean, showPrice: boolean) {
  return ['16px', 'minmax(150px, 1fr)', ...(showStock ? ['76px'] : []), ...(showPrice ? columns.map(column => column.takesListPrice ? '164px' : '86px') : [])].join(' ')
}

// Every query that shows a variant's panel price, channel prices or stock: the products list with its summary, counts
// and low-stock details (['products', …]) and the product editor (['product', id]). A quick edit save invalidates all
// of them so an open editor shows the same values.
export const QUICK_EDIT_SYNC_KEYS: ReadonlyArray<readonly string[]> = [['products'], ['product']]

// The list and sale price the per-variant fields show for the choice: the panel's, the chosen channel's offer, or none.
export function quickInlinePrices(choice: string, variant: QuickPriceVariant & VariantOfferPriceSource, product: PanelPriceSource): { listPrice: number | null; salePrice: number | null } {
  if (choice === PANEL_PRICE_CHOICE) return panelPrices(variant, product)
  const offer = choice ? channelOffer(variant, choice) : null
  return { listPrice: offer?.listPrice ?? null, salePrice: offer?.salePrice ?? null }
}

// What the choice changes, said under the select so it is never a guess.
export function quickPriceChoiceHelp(choice: string, channelName?: string | null): string {
  if (choice === PANEL_PRICE_CHOICE) return "Ürün kartındaki ana fiyat (Liste/Satış Fiyatı) değişir; pazaryerlerindeki fiyatlar değişmez ve pazaryerine hiçbir şey gönderilmez. Ana fiyat, ürün bir kanalda ilk kez yayınlanırken (Platformlarda Yayınla) ve ürün düzenleyicide 'Ana fiyatla satılır' kanallar kaydedilirken kullanılır."
  if (choice) return `Yalnız ${channelName || 'seçilen kanalın'} fiyatı değişir; panel ana fiyatı ve diğer kanalların fiyatı değişmez. Bu kanalda fiyatı olmayan varyantın ilk fiyatı oluşturulur.`
  return 'Fiyatın nereye yazılacağını seçin: Panel Ana Fiyatı ya da tek bir pazaryeri kanalı.'
}

// The group label of the per-variant price fields.
export function quickPriceChoiceLabel(choice: string, channelName?: string | null): string {
  if (choice === PANEL_PRICE_CHOICE) return 'Panel Fiyatı'
  return choice ? channelName || 'Kanal Fiyatı' : 'Fiyat'
}

// Where a panel price change goes: the variant, at the version the list loaded.
export type PanelPriceTarget = { kind: 'panel'; variantId: string; version: number } | { kind: 'blocked'; reason: string }
export function panelPriceTarget(variant: { id: string; version?: number | null }): PanelPriceTarget {
  if (variant.version == null) return { kind: 'blocked', reason: 'Varyant sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }
  return { kind: 'panel', variantId: variant.id, version: variant.version }
}

// The list and sale price typed into the two per-variant fields, checked as the server checks them. An empty list
// price takes the sale price.
export function inlinePrices(list: string, sale: string): { ok: true; listPrice: number; salePrice: number } | { ok: false; message: string } {
  const salePrice = Number(sale)
  if (sale.trim() === '' || !Number.isFinite(salePrice) || salePrice <= 0) return { ok: false, message: 'Satış fiyatı sıfırdan büyük olmalıdır.' }
  const listPrice = list.trim() === '' ? salePrice : Number(list)
  if (!Number.isFinite(listPrice) || listPrice < salePrice) return { ok: false, message: 'Liste fiyatı satış fiyatından küçük olamaz.' }
  if (!hasAtMostTwoDecimals(listPrice) || !hasAtMostTwoDecimals(salePrice)) return { ok: false, message: 'Fiyat en fazla iki ondalık basamak içerebilir.' }
  return { ok: true, listPrice, salePrice }
}

export function hasAtMostTwoDecimals(value: number) {
  return Math.abs(Math.round(value * 100) - value * 100) < 1e-6
}
