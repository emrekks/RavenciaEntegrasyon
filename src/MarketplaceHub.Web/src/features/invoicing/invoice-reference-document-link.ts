type InvoiceReferenceDocumentSource = {
  marketplaceInvoiceUrl?: string | null
  currentPlatformInvoiceUrl?: string | null
  platformInvoiceStatus?: string | null
}

export function platformInvoiceLinkUnavailable(item: InvoiceReferenceDocumentSource): boolean {
  return item.platformInvoiceStatus?.toUpperCase() === 'INVOICED'
    && !invoiceReferenceMarketplaceHref(item)
}

export function invoiceReferenceMarketplaceHref(item: InvoiceReferenceDocumentSource): string | null {
  if (item.platformInvoiceStatus?.toUpperCase() !== 'INVOICED') return null
  return safeInvoiceReferenceUrl(item.currentPlatformInvoiceUrl ?? item.marketplaceInvoiceUrl)
}

export function safeInvoiceReferenceUrl(candidate: string | null | undefined): string | null {
    const documentUrl = candidate?.trim()
    if (!documentUrl) return null
    try {
      const url = new URL(documentUrl)
      const host = url.hostname.toLowerCase().replace(/\.$/u, '')
      if (url.protocol === 'https:'
        && !url.username
        && !url.password
        && host !== 'localhost'
        && !host.endsWith('.local')
        && !host.endsWith('.internal')
        && !host.endsWith('.lan')
        && !isPrivateIpv4(host)
        && !host.startsWith('[')) return url.href
    } catch {
      // Invalid or unsafe URLs are never opened.
    }
  return null
}

function isPrivateIpv4(host: string): boolean {
  if (!/^\d{1,3}(?:\.\d{1,3}){3}$/u.test(host)) return false
  const octets = host.split('.').map(Number)
  if (octets.some(value => value > 255)) return true
  const [first, second] = octets
  return first === 0 || first === 10 || first === 127
    || first === 169 && second === 254
    || first === 172 && second >= 16 && second <= 31
    || first === 192 && second === 168
}
