// The lookup answers with a redirect to the marketplace photo; size=thumb makes that the CDN's small copy (Trendyol
// /mnresize/256/384/, MarketplaceEndpoints.ProductImageThumbnailUrl), for small frames. Other addresses stay as they are.
export function productImageThumbnailLookup(url: string) {
  if (!url.startsWith('/api/v1/orders/product-image?')) return url
  const [path, query] = url.split('?', 2)
  const params = new URLSearchParams(query)
  params.set('size', 'thumb')
  return `${path}?${params.toString()}`
}

export function productImageFallbackUrls(keys: Array<string | null | undefined>, connectionId?: string | null, productName?: string | null) {
  const urls = keys.flatMap(key => {
    const value = key?.trim()
    if (!value) return []
    const params = new URLSearchParams({ barcode: value })
    if (connectionId) params.set('connectionId', connectionId)
    if (productName?.trim()) params.set('productName', productName.trim())
    return [`/api/v1/orders/product-image?${params.toString()}`]
  })
  if (urls.length === 0 && productName?.trim()) {
    const params = new URLSearchParams({ productName: productName.trim() })
    if (connectionId) params.set('connectionId', connectionId)
    urls.push(`/api/v1/orders/product-image?${params.toString()}`)
  }
  return [...new Set(urls)]
}
