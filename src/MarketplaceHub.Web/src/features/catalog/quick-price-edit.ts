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
