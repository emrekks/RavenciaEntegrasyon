type ProductImportConnectionLike = {
  platformCode: string
  status: string
}

const normalizedCode = (value: string) => value.trim().toUpperCase()

export function productImportConnection(item: ProductImportConnectionLike): boolean {
  return ['TRENDYOL', 'SHOPIFY', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM'].includes(normalizedCode(item.platformCode))
    && ['ACTIVE', 'VERIFIED'].includes(normalizedCode(item.status))
}

export function singleProductLookupLabel(platformCode: string): string {
  const code = normalizedCode(platformCode)
  if (code === 'SHOPIFY') return 'Shopify ürün linki veya barkod / stok kodu'
  if (code === 'HEPSIBURADA') return 'Hepsiburada ürün ID’si'
  if (code === 'N11') return 'N11 model kodu (productMainId)'
  if (code === 'PAZARAMA') return 'Pazarama barkodu'
  if (code === 'PTTAVM') return 'PTT AVM barkodu'
  return 'Trendyol ürün linki veya model kodu'
}

// The hint under the single-product field speaks only of the selected platform.
export function singleProductLookupHint(platformCode: string): string {
  const code = normalizedCode(platformCode)
  if (code === 'SHOPIFY') return 'Shopify ürün sayfasının linkini ya da varyantın barkodunu veya stok kodunu girin.'
  if (code === 'HEPSIBURADA') return 'Hepsiburada ürün sayfasındaki ürün ID’sini girin.'
  if (code === 'N11') return 'N11 ürününün model kodunu (productMainId) girin.'
  if (code === 'PAZARAMA') return 'Pazarama ürününün barkodunu girin.'
  if (code === 'PTTAVM') return 'PTT AVM ürününün barkodunu girin.'
  return 'Trendyol ürün sayfasının linkini ya da model kodunu girin.'
}
