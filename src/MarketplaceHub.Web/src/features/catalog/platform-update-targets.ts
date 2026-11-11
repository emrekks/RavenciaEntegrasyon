import type { MarketplaceConnection } from './shared'

// The marketplaces "Platformlarda Güncelle" can send an update to (the editor's Hızlı Güncelle and the product list's
// "Platformlara Güncelle"), and what each takes.
export const isProductUpdateConnection = (item: MarketplaceConnection) => ['TRENDYOL', 'HEPSIBURADA', 'SHOPIFY', 'PAZARAMA', 'N11', 'PTTAVM'].includes(item.platformCode.trim().toUpperCase()) && ['ACTIVE', 'VERIFIED'].includes(item.status.trim().toUpperCase())
export const productUpdateCapabilities = (platformCode: string) => {
  const code = platformCode.trim().toUpperCase()
  if (code === 'TRENDYOL') return { writable: true, content: true, categoryAttributes: true, detail: 'Ürün bilgileri, fiyat ve stok güncellenebilir.' }
  if (code === 'HEPSIBURADA') return { writable: true, content: true, categoryAttributes: true, detail: 'Ürün bilgileri, fiyat ve stok Hepsiburada yazma kurallarıyla güncellenebilir.' }
  if (code === 'SHOPIFY') return { writable: true, content: true, categoryAttributes: false, detail: 'Ürün kartı tek istekte güncellenir: ürün adı, açıklama, görseller, renk ve beden, fiyat ve stok birlikte gider.' }
  if (code === 'PAZARAMA') return { writable: true, content: false, categoryAttributes: false, detail: 'KDV oranı, fiyat ve stok güncellenebilir; ürün içeriği yalnız Pazarama panelinden değişir.' }
  if (code === 'N11') return { writable: true, content: true, categoryAttributes: true, detail: 'Açıklama, kategori özellikleri, KDV oranı, teslimat süresi, fiyat ve stok güncellenebilir; ürün adı, görseller ve marka N11 API ile değişmez.' }
  if (code === 'PTTAVM') return { writable: true, content: true, categoryAttributes: false, detail: 'Seçilen ürün bilgileri tek istekte gider; PttAVM’deki tüm varyantlar olduğu gibi eklenir. Fiyat ve stok birlikte gider.' }
  return { writable: false, content: false, categoryAttributes: false, detail: 'Bu platform için ürün güncelleme desteği bulunmuyor.' }
}

export function quickPlatformUpdateTargets(selectedIds: readonly string[], updateableIds: readonly string[]) {
  const updateable = new Set(updateableIds)
  return [...new Set(selectedIds)].filter(id => updateable.has(id))
}
