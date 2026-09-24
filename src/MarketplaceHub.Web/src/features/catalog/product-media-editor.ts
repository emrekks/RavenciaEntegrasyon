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

export function mediaImageKey(value: string): string {
  const candidate = value.trim()
  try {
    const url = new URL(candidate)
    if (url.protocol === 'https:') return `${url.origin}${url.pathname.replace(/\/+$/u, '')}`.toLocaleLowerCase('en-US')
  } catch { /* Stored media uses a same-origin relative path. */ }
  return candidate.replace(/\/+$/u, '').toLocaleLowerCase('en-US')
}

export function uniqueMediaUrls(urls: string[]): string[] {
  const seen = new Set<string>()
  return urls.filter(url => {
    const key = mediaImageKey(url)
    if (!key || seen.has(key)) return false
    seen.add(key)
    return true
  })
}

export function mediaUrlsInPreferredOrder(currentUrls: string[], preferredUrls: string[]): string[] {
  const remaining = [...currentUrls]
  const ordered: string[] = []
  for (const preferred of preferredUrls) {
    const index = remaining.findIndex(url => mediaImageKey(url) === mediaImageKey(preferred))
    if (index < 0) continue
    ordered.push(remaining[index])
    remaining.splice(index, 1)
  }
  return [...ordered, ...remaining]
}

export function mediaRefsEqual(current: string[], original: string[]): boolean {
  return current.length === original.length && current.every((reference, index) => reference === original[index])
}

export function mediaRefsSameSet(current: string[], original: string[]): boolean {
  if (current.length !== original.length) return false
  const remaining = new Map<string, number>()
  for (const reference of original) remaining.set(reference, (remaining.get(reference) ?? 0) + 1)
  for (const reference of current) {
    const count = remaining.get(reference) ?? 0
    if (!count) return false
    if (count === 1) remaining.delete(reference)
    else remaining.set(reference, count - 1)
  }
  return remaining.size === 0
}

export function modelCodeForExistingVariant(inputValue: string, initialValue: string, existingValue: string | null | undefined): string | null {
  const requested = inputValue.trim()
  if (requested === initialValue.trim()) return existingValue?.trim() || null
  return requested || null
}
