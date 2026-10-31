import { mediaImageKey } from './product-media-editor'

// A copy ("Ürün Kopyala") is not a product yet: an image removed from it is kept as a removed key in the page's history
// entry, so a reload or a reseed from the source product does not bring it back. The source product is never touched.

export function copyRemovedMediaKeys(routeState: unknown): string[] {
  const value = (routeState as { copyRemovedMediaKeys?: unknown } | null)?.copyRemovedMediaKeys
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item !== '') : []
}

export function withCopyRemovedMedia(current: readonly string[], urls: readonly string[]): string[] {
  return [...new Set([...current, ...urls.map(mediaImageKey).filter(Boolean)])]
}

export function withoutCopyRemovedMedia(urls: readonly string[], removedKeys: readonly string[]): string[] {
  if (!removedKeys.length) return [...urls]
  const removed = new Set(removedKeys)
  return urls.filter(url => !removed.has(mediaImageKey(url)))
}

export function withoutCopyRemovedMediaRefs(references: readonly string[], removedKeys: readonly string[]): string[] {
  if (!removedKeys.length) return [...references]
  const removed = new Set(removedKeys)
  return references.filter(reference => !reference.startsWith('url|') || !removed.has(mediaImageKey(reference.slice(4))))
}

// The router keeps a page's own state under `usr` in window.history.state; the other fields (key, idx) stay as they are.
export function historyStateWithCopyRemovedMedia(historyState: unknown, removedKeys: readonly string[]) {
  const current = historyState && typeof historyState === 'object' ? historyState as Record<string, unknown> : {}
  const usr = current.usr && typeof current.usr === 'object' ? current.usr as Record<string, unknown> : {}
  return { ...current, usr: { ...usr, copyRemovedMediaKeys: [...removedKeys] } }
}
