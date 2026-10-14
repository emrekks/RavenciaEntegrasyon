export function shopifyStoreDisplayName(value: string): string {
  const trimmed = value.trim()
  if (!trimmed) return ''
  return trimmed.toLowerCase().endsWith('.myshopify.com') ? trimmed : `${trimmed}.myshopify.com`
}
