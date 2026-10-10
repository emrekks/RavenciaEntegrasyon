type InvoiceReferenceDocumentSource = {
  invoiceId: string | null
  invoiceDocumentAvailable: boolean
  marketplaceInvoiceUrl?: string | null
}

export function invoiceReferenceDocumentHref(item: InvoiceReferenceDocumentSource): string | null {
  const marketplaceUrl = item.marketplaceInvoiceUrl?.trim()
  if (marketplaceUrl) {
    try {
      const url = new URL(marketplaceUrl)
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
      // An invalid or unsafe marketplace URL falls back to the stored PDF.
    }
  }

  return item.invoiceId && item.invoiceDocumentAvailable
    ? `/api/v1/invoices/${encodeURIComponent(item.invoiceId)}/documents/latest/content`
    : null
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
