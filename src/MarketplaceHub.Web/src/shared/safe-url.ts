// Addresses that reach the page from records or from the person: only web addresses become links or editor content.

/** The address when it is a well-formed http(s) URL, otherwise null (javascript:, data:, relative junk...). */
export function safeExternalUrl(value: string | null | undefined) {
  const text = value?.trim()
  if (!text || !/^https?:\/\//i.test(text)) return null
  try {
    const url = new URL(text)
    return url.protocol === 'https:' || url.protocol === 'http:' ? text : null
  } catch {
    return null
  }
}

/** Why an address typed for publication is not accepted (null when it is a well-formed https URL). */
export function httpsUrlProblem(value: string) {
  const text = value.trim()
  if (!text) return 'Adres girin.'
  if (!/^https:\/\//i.test(text)) return 'Adres https:// ile başlamalı.'
  try {
    const url = new URL(text)
    if (url.protocol !== 'https:' || !url.hostname.includes('.')) return 'Geçerli bir https adresi girin.'
    return null
  } catch {
    return 'Geçerli bir https adresi girin.'
  }
}
