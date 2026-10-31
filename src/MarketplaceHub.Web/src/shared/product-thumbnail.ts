// Marketplace CDNs link the full-size photo (Trendyol: about 145 KB, 1200 px). A thumbnail asks the CDN for a
// small copy instead (Trendyol /mnresize/w/h/, about 10 KB WebP), sharp on high-density screens too.
export function thumbnailUrl(src: string): string {
  try {
    const url = new URL(src)
    if (url.hostname === 'cdn.dsmcdn.com' && !url.pathname.startsWith('/mnresize/')) {
      url.pathname = '/mnresize/256/384' + url.pathname.replace(/\/{2,}/g, '/')
      return url.toString()
    }
  } catch { /* not an absolute address: used as it is */ }
  return src
}

// The addresses a thumbnail tries in turn: the small CDN copy, then the original, then the same for the catalog's
// photo (when the marketplace's address no longer loads). Empty when no picture is known: the frame shows the no-photo placeholder.
export function thumbnailSources(src: string | null | undefined, fallbackSrc?: string | null): string[] {
  const sources = [src, fallbackSrc].flatMap(value => value ? [thumbnailUrl(value), value] : [])
  return [...new Set(sources)]
}
