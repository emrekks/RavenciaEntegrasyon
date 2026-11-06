import { platformName } from '../../reports/report-format'

// GET /reports/price-assumptions (PriceAssumptionsView, ReportsContracts.cs): what each store took of the owner's
// clothing sales over the last days, for the hints beside Fiyat Hesapla's Komisyon, Kargo and Hizmet Bedeli.
export type PriceAssumptionStore = {
  connectionId: string
  platformCode: string
  displayName: string
  orders: number
  sales: number
  commissionRate: number | null
  commissionRateBeforeVat: number | null
  takesCommission: boolean
  realCommissionOrders: number
  cargo: number | null
  cargoSource: 'invoice' | 'tariff' | 'assumption' | null
  serviceFee: number
  serviceFeeRate: number
  serviceFeeSource: 'tariff' | 'records'
  serviceFeeOrders: number
  azerbaijanServiceFee: number | null
  internationalServiceFeeRate: number | null
  paymentFeeRate: number | null
  paymentFeeFixed: number | null
}
export type PriceAssumptions = { from: string; to: string; days: number; clothingOnly: boolean; minimumOrders: number; stores: PriceAssumptionStore[] }

export const priceAssumptionsPath = '/reports/price-assumptions'

const twoDecimals = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const rate = (value: number) => `%${twoDecimals.format(value)}`
const money = (value: number) => `${twoDecimals.format(value)} ₺`
const day = (value: string) => new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`))

// The store's name in a hint: the platform's, with the store's own name when the owner has two of one platform.
function storeName(store: PriceAssumptionStore, stores: PriceAssumptionStore[]) {
  const platform = platformName(store.platformCode)
  return stores.filter(other => other.platformCode === store.platformCode).length > 1 ? `${platform} (${store.displayName})` : platform
}

export type HintRow = { store: string; value: string }

// Komisyon Oranı: the share of the sale each store's commission took, VAT included as it was charged (the rate to type).
export function commissionRows(data: PriceAssumptions): HintRow[] {
  return data.stores.map(store => {
    const name = storeName(store, data.stores)
    if (!store.takesCommission) {
      const card = store.paymentFeeRate !== null ? `; kartlı ödeme kesintisi ${rate(store.paymentFeeRate)}${store.paymentFeeFixed ? ` + ${money(store.paymentFeeFixed)}` : ''}` : ''
      return { store: name, value: `pazaryeri komisyonu yok${card}` }
    }
    if (store.commissionRate === null)
      return { store: name, value: `bilinmiyor: ${store.orders} satış, oran için en az ${data.minimumOrders} gerekir` }
    if (store.commissionRateBeforeVat !== null)
      return { store: name, value: `${rate(store.commissionRateBeforeVat)} + %20 KDV → ${rate(store.commissionRate)} girin (${store.orders} satış)` }
    return { store: name, value: `${rate(store.commissionRate)} (${store.orders} satış)` }
  })
}

// Kargo Ücreti: one parcel, VAT included.
export function cargoRows(data: PriceAssumptions): HintRow[] {
  return data.stores.map(store => ({
    store: storeName(store, data.stores),
    value: store.cargo === null ? 'bilinmiyor'
      : `${money(store.cargo)} (${store.cargoSource === 'invoice' ? 'kargo faturalarındaki güncel tarife' : store.cargoSource === 'assumption' ? 'varsayım' : 'pazaryerinin 2026 tarifesi, en sık tutar'})`
  }))
}

// Hizmet Bedeli: per order and as a share of the sale, VAT included.
export function serviceFeeRows(data: PriceAssumptions): HintRow[] {
  return data.stores.map(store => {
    const parts: string[] = []
    if (store.serviceFee > 0) parts.push(store.serviceFeeSource === 'records' ? `${money(store.serviceFee)} (muhasebe kayıtlarında ${store.serviceFeeOrders} siparişin ortalaması)` : money(store.serviceFee))
    if (store.serviceFeeRate > 0) parts.push(`satıştan ${rate(store.serviceFeeRate)}`)
    if (store.azerbaijanServiceFee !== null) parts.push(`Azerbaycan vitrininde ${money(store.azerbaijanServiceFee)}`)
    if (store.internationalServiceFeeRate !== null) parts.push(`yurt dışı vitrinlerde + satıştan ${rate(store.internationalServiceFeeRate)}`)
    return { store: storeName(store, data.stores), value: parts.length ? parts.join('; ') : 'yok' }
  })
}

// The line under each hint: the period and which sales the figures were measured on.
export function periodNote(data: PriceAssumptions) {
  const sales = data.clothingOnly ? 'giyim satışları' : 'giyim kategorileri ayırt edilemedi, tüm satışlar'
  return `Son ${data.days} gün (${day(data.from)} – ${day(data.to)}), ${sales}; raporlardaki hesapla aynı.`
}
