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
] as const

export function resolveOrderStatusTab(status: string | null | undefined) {
  const normalizedStatus = status?.trim().toUpperCase()
  if (normalizedStatus === 'PENDING') return 'NEW'
  if (normalizedStatus === 'PAYMENT_PENDING') return 'NEW'
  if (normalizedStatus === 'UNVERIFIED') return 'ALL'
  return status || 'ALL'
}
