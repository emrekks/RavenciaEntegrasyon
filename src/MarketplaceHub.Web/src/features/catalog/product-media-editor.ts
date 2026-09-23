const storedProductMediaPath = /^\/api\/v1\/files\/product-media\/[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\/content$/iu

export function isStoredProductMediaUrl(value: string): boolean {
  return storedProductMediaPath.test(value.trim())
}

export function publicProductMediaUrls(urls: string[]): string[] {
  return urls.filter(url => !isStoredProductMediaUrl(url))
}

export function reorderMediaUrls(urls: string[], sourceIndex: number, targetIndex: number): string[] {
  if (sourceIndex < 0 || targetIndex < 0 || sourceIndex >= urls.length || targetIndex >= urls.length || sourceIndex === targetIndex) return urls

  const next = [...urls]
  const [moved] = next.splice(sourceIndex, 1)
  next.splice(targetIndex, 0, moved)
  return next
}

export function mediaRefsEqual(current: string[], original: string[]): boolean {
  return current.length === original.length && current.every((reference, index) => reference === original[index])
}
