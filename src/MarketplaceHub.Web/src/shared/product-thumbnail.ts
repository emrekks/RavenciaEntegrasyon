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
