// Where a quick price change on the products list goes: the variant's existing offer (at the version the list
// loaded), or a new offer on the channel the person picked. A variant with no offer is never priced on a channel the
// screen picked by itself.

export type QuickPriceTarget =
  | { kind: 'update'; offerId: string; offerVersion: number }
  | { kind: 'create'; connectionId: string }
  | { kind: 'blocked'; reason: string }

export function quickPriceTarget(variant: { offerId: string | null; offerVersion: number | null }, connectionId: string, connectionCount: number): QuickPriceTarget {
  if (variant.offerId) {
    if (variant.offerVersion == null) return { kind: 'blocked', reason: 'Fiyat sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }
    return { kind: 'update', offerId: variant.offerId, offerVersion: variant.offerVersion }
  }
  if (!connectionCount) return { kind: 'blocked', reason: 'İlk fiyat için aktif platform bağlantısı yok.' }
  if (!connectionId) return { kind: 'blocked', reason: 'İlk fiyat için önce Fiyat Kanalı seçin.' }
  return { kind: 'create', connectionId }
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
