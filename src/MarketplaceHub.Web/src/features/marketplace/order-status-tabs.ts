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
  ['UNDELIVERED', 'Teslim Edilemedi'],
  ['MANUAL_REVIEW', 'İnceleme Gerekli'],
  // The warehouse queue the menu counter and the dashboard's "Bekleyen" card count: reached from those links only
  // (orderLinkOnlyTabs), not listed in the tab row.
  ['PENDING', 'Bekleyen İşler'],
  // Pending orders past their ship-by time: the dashboard's "Geciken Siparişler" card opens this tab.
  ['LATE', 'Geciken'],
] as const

// The tab row shows what fits and moves the rest into "Diğer" by itself (ViewTabsWithMore); these always go there.
export const orderStatusMoreTabs: string[] = []
// Shown only while a link (the dashboard cards, the menu counter) has opened them.
export const orderLinkOnlyTabs = ['PENDING']

const knownOrderStatusTabs = new Set<string>(orderStatusTabs.map(([value]) => value))

export function resolveOrderStatusTab(status: string | null | undefined) {
  const normalizedStatus = status?.trim().toUpperCase()
  if (!normalizedStatus) return 'ALL'
  // READY_TO_SHIP orders are listed with the processing ones.
  if (normalizedStatus === 'READY_TO_SHIP') return 'PROCESSING'
  return knownOrderStatusTabs.has(normalizedStatus) ? normalizedStatus : status || 'ALL'
}
