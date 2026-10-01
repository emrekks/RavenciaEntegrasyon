export function productImageFallbackUrls(keys: Array<string | null | undefined>, connectionId?: string | null) {
  const urls = keys.flatMap(key => {
    const value = key?.trim()
    if (!value) return []
    const params = new URLSearchParams({ barcode: value })
    if (connectionId) params.set('connectionId', connectionId)
    return [`/api/v1/orders/product-image?${params.toString()}`]
  })
  return [...new Set(urls)]
}
