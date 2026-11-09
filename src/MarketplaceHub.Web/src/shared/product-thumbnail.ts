// Marketplace CDNs link the full-size photo (Trendyol: about 145 KB, 1200 px). A thumbnail asks the CDN for a
// small copy instead (Trendyol /mnresize/w/h/, about 10 KB WebP), sharp on high-density screens too.
// Our own uploaded photos (/api/v1/files/product-media/{id}/content, 0.2-3 MB originals) have 168x252 and
// 480x720 WebP copies on the server, asked for with ?w=168 (frames) or ?w=480 (the large preview).
// Hepsiburada's productImageUrlFormat carries a {size} token, but its documentation lists no sizes, so its
// addresses stay as the marketplace gave them.
export type OwnMediaWidth = 168 | 480

const OWN_MEDIA = /^(?:https?:\/\/[^/?#]+)?\/api\/v1\/files\/product-media\/[0-9a-f-]{36}\/content$/i

// The stored-photo address without a query, or null when the address is not one of ours.
function ownMediaBase(src: string): string | null {
  return OWN_MEDIA.test(src) ? src : null
}

export function thumbnailUrl(src: string, width: OwnMediaWidth = 168): string {
  const own = ownMediaBase(src)
  if (own) return `${own}?w=${width}`
  try {
    const url = new URL(src)
    if (url.hostname === 'cdn.dsmcdn.com' && !url.pathname.startsWith('/mnresize/')) {
      url.pathname = '/mnresize/256/384' + url.pathname.replace(/\/{2,}/g, '/')
      return url.toString()
    }
  } catch { /* not an absolute address: used as it is */ }
  return src
}

// For our own photos: both copies, so the browser picks by the frame's width and the screen's density.
export function thumbnailSrcSet(src: string): string | undefined {
  const own = ownMediaBase(src.replace(/\?w=\d+$/, ''))
  return own ? `${own}?w=168 168w, ${own}?w=480 480w` : undefined
}

// The large preview: the 480 px copy, or the original on a high-density screen (originals are 1024-1200 px wide).
export function previewSrcSet(src: string): string | undefined {
  const own = ownMediaBase(src)
  return own ? `${own}?w=480 480w, ${own} 1200w` : undefined
}

// The addresses a thumbnail tries in turn: the small CDN copy, then the original, then the same for the catalog's
// photo (when the marketplace's address no longer loads). Empty when no picture is known: the frame shows the no-photo placeholder.
export function thumbnailSources(src: string | null | undefined, fallbackSrc?: string | null): string[] {
  const sources = [src, fallbackSrc].flatMap(value => value ? [thumbnailUrl(value), value] : [])
  return [...new Set(sources)]
}
