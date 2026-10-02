export const orderStatusTabs = [
  ['ALL', 'Tümü'],
  ['NEW', 'Yeni'],
  ['UNVERIFIED', 'Doğrulanmadı'],
  ['PROCESSING', 'İşleme alınanlar'],
  ['SHIPPED', 'Kargoda'],
  ['DELIVERED', 'Teslim edildi'],
  ['CANCELLED', 'İptal'],
  ['RESENT', 'Yeniden Gönderimler'],
  ['ON_HOLD', 'Askıdaki'],
  ['PARTIALLY_CANCELLED', 'Kısmi iptal'],
] as const

export function resolveOrderStatusTab(status: string | null | undefined) {
  return status?.trim().toUpperCase() === 'PENDING' ? 'NEW' : status || 'ALL'
}
