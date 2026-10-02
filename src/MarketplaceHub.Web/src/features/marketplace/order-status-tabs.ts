export const orderStatusTabs = [
  ['ALL', 'Tümü'],
  ['NEW', 'Yeni'],
  ['PROCESSING', 'İşleme alınanlar'],
  ['SHIPPED', 'Kargoda'],
  ['DELIVERED', 'Teslim edildi'],
  ['CANCELLED', 'İptal'],
  ['RESENT', 'Yeniden Gönderimler'],
  ['ON_HOLD', 'Askıdaki'],
  ['PARTIALLY_CANCELLED', 'Kısmi iptal'],
] as const

export function resolveOrderStatusTab(status: string | null | undefined) {
  const normalizedStatus = status?.trim().toUpperCase()
  if (normalizedStatus === 'PENDING') return 'NEW'
  if (normalizedStatus === 'UNVERIFIED') return 'ALL'
  return status || 'ALL'
}
