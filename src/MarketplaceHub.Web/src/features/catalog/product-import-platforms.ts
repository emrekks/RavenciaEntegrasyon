type ProductImportConnectionLike = {
  platformCode: string
  status: string
}

const normalizedCode = (value: string) => value.trim().toUpperCase()

export function productImportConnection(item: ProductImportConnectionLike): boolean {
  return ['TRENDYOL', 'SHOPIFY', 'HEPSIBURADA'].includes(normalizedCode(item.platformCode))
    && ['ACTIVE', 'VERIFIED'].includes(normalizedCode(item.status))
}

export function productImportIdentityLabel(platformCodes: readonly string[]): string {
  const codes = [...new Set(platformCodes.map(normalizedCode))]
  if (codes.length !== 1) return 'ürün kimliği'
  if (codes[0] === 'SHOPIFY') return 'barkod veya stok kodu'
  if (codes[0] === 'HEPSIBURADA') return 'merchant SKU'
  return 'model kodu'
}

export function singleProductLookupLabel(platformCode: string): string {
  const code = normalizedCode(platformCode)
  if (code === 'SHOPIFY') return 'Shopify ürün linki veya barkod / stok kodu'
  if (code === 'HEPSIBURADA') return 'Hepsiburada ürün ID’si'
  return 'Trendyol ürün linki veya model kodu'
}
