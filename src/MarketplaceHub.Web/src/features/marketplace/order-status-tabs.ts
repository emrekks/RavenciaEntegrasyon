import { statusLabel } from '../../shared/status-labels'

export const orderStatusTabs = [
  ['ALL', 'Tümü'],
  ['NEW', 'Yeni'],
  ['PROCESSING', 'İşleme Alınanlar'],
  ['SHIPPED', 'Kargoda'],
  ['DELIVERED', 'Teslim Edildi'],
  ['CANCELLED', 'İptal'],
  ['RESENT', 'Yeniden Gönderimler'],
  ['ON_HOLD', 'Askıda'],
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
  // A status no tab lists (a typo, an old link) opens Tümü instead of going to the API as typed.
  return knownOrderStatusTabs.has(normalizedStatus) ? normalizedStatus : 'ALL'
}

// One name per order state wherever an order shows it: the tab, the row's ring, the export and the Shopify status
// dialog (the tab said "Askıdaki" / "İnceleme Gerekli" where the export said "Beklemede" / "İnceleme Bekliyor"). The
// shared statusLabel keeps its names for jobs and the other records.
const orderStateNames: Record<string, string> = {
  ON_HOLD: 'Askıda',
  MANUAL_REVIEW: 'İnceleme Gerekli',
  PAYMENT_PENDING: 'Ödeme Bekleniyor',
  UNVERIFIED: 'Doğrulanmadı',
}

export function orderStatusLabel(status: string) {
  return orderStateNames[status.trim().toUpperCase()] ?? statusLabel(status)
}

// The marketplaces whose new order the panel takes into work: Trendyol, N11 and Pazarama at once
// (MarketplaceSalesService.ProcessShipmentInstantAsync), Hepsiburada by creating its package. PttAVM is refused there
// with SHIPMENT_ACTION_UNSUPPORTED and Shopify has no picking step, so neither shows "İşleme Al".
const takeIntoWorkPlatforms = new Set(['TRENDYOL', 'N11', 'PAZARAMA', 'HEPSIBURADA'])

export function canTakeOrderIntoWork(platformCode: string) {
  return takeIntoWorkPlatforms.has(platformCode.trim().toUpperCase())
}
