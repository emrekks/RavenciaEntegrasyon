export const orderStatusTabs = [
  ['ALL', 'Tümü'],
  ['NEW', 'Yeni'],
  ['PROCESSING', 'İşleme Alınanlar'],
  ['SHIPPED', 'Kargoda'],
  ['DELIVERED', 'Teslim Edildi'],
  ['CANCELLED', 'İptal'],
  ['RESENT', 'Yeniden Gönderimler'],
  ['ON_HOLD', 'Askıdaki'],
  ['PARTIALLY_CANCELLED', 'Kısmi İptal'],
  // Every order status has a tab (audit 2026-10-20 S13); these open from the "Diğer" menu.
  ['UNDELIVERED', 'Teslim Edilemedi'],
  ['RETURN_IN_TRANSIT', 'İade Yolunda'],
  ['RETURNED', 'İade Edildi'],
  ['MANUAL_REVIEW', 'İnceleme Gerekli'],
] as const

// The tabs that leave the tab row for its "Diğer" menu.
export const orderStatusMoreTabs = ['PARTIALLY_CANCELLED', 'RESENT', 'ON_HOLD', 'UNDELIVERED', 'RETURN_IN_TRANSIT', 'RETURNED', 'MANUAL_REVIEW']

export function resolveOrderStatusTab(status: string | null | undefined) {
  const normalizedStatus = status?.trim().toUpperCase()
  if (normalizedStatus === 'PENDING') return 'NEW'
  if (normalizedStatus === 'PAYMENT_PENDING') return 'NEW'
  if (normalizedStatus === 'UNVERIFIED') return 'ALL'
  return status || 'ALL'
}
