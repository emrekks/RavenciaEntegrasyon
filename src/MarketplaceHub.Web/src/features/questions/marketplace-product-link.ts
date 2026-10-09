const MARKETPLACE_SEARCH_BASES: Record<string, string> = {
  TRENDYOL: 'https://www.trendyol.com/sr?q=',
  HEPSIBURADA: 'https://www.hepsiburada.com/ara?q='
}

export function marketplaceProductSearchUrl(platformCode: string, barcode?: string | null, sku?: string | null, modelCode?: string | null, productName?: string | null): string | null {
  const base = MARKETPLACE_SEARCH_BASES[platformCode.trim().toUpperCase()]
  const query = [barcode, sku, modelCode, productName].find(value => value?.trim())?.trim()
  return base && query ? `${base}${encodeURIComponent(query)}` : null
}
