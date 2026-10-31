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
