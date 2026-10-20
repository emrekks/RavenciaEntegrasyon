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
  // Orders waiting for the payment approval and Hepsiburada orders not yet verified have a place of their own, so
  // every order of "Tümü" is in a tab.
  ['PAYMENT_PENDING', 'Ödeme Bekleyen'],
  ['UNVERIFIED', 'Doğrulanmadı'],
  // The warehouse queue the menu counter and the dashboard's "Bekleyen" card count (new, processing, ready to ship,
  // partially cancelled and manual review orders): their links open exactly that set.
  ['PENDING', 'Bekleyen İşler'],
  // Pending orders past their ship-by time: the dashboard's "Geciken Siparişler" card opens this tab.
  ['LATE', 'Geciken'],
] as const

// The tabs that leave the tab row for its "Diğer" menu.
export const orderStatusMoreTabs = ['PARTIALLY_CANCELLED', 'RESENT', 'ON_HOLD', 'UNDELIVERED', 'RETURN_IN_TRANSIT', 'RETURNED', 'MANUAL_REVIEW', 'PAYMENT_PENDING', 'UNVERIFIED', 'PENDING', 'LATE']

const knownOrderStatusTabs = new Set<string>(orderStatusTabs.map(([value]) => value))

export function resolveOrderStatusTab(status: string | null | undefined) {
  const normalizedStatus = status?.trim().toUpperCase()
  if (!normalizedStatus) return 'ALL'
  // READY_TO_SHIP orders are listed with the processing ones.
  if (normalizedStatus === 'READY_TO_SHIP') return 'PROCESSING'
  return knownOrderStatusTabs.has(normalizedStatus) ? normalizedStatus : status || 'ALL'
}
