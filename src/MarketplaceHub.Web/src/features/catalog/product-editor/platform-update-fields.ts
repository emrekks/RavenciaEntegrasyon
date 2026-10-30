// "Platformlarda Güncelle": which product and sales fields go to which marketplace. The fields are picked in groups;
// each marketplace takes them in the requests its API has, and a request always carries all of its fields. A card
// therefore lists what really goes, including a field that was not picked but rides in the same request.

export type PlatformUpdateField = 'TITLE' | 'DESCRIPTION' | 'IMAGES' | 'ATTRIBUTES' | 'DESI' | 'VAT_RATE' | 'ORIGIN' | 'DELIVERY' | 'PRICE' | 'STOCK'
export type PlatformUpdateGroupKey = 'product' | 'sales'

export type PlatformUpdateGroup = { key: PlatformUpdateGroupKey; label: string; description: string; fields: { key: PlatformUpdateField; label: string }[] }

export const platformUpdateGroups: PlatformUpdateGroup[] = [
  {
    key: 'product',
    label: 'Ürün Bilgileri',
    description: 'Ürün kartının içeriği ve kargo bilgileri.',
    fields: [
      { key: 'TITLE', label: 'Ürün Adı' },
      { key: 'DESCRIPTION', label: 'Açıklama' },
      { key: 'IMAGES', label: 'Görseller' },
      { key: 'ATTRIBUTES', label: 'Kategori Özellikleri' },
      { key: 'DESI', label: 'Desi' },
      { key: 'VAT_RATE', label: 'KDV Oranı' },
      { key: 'ORIGIN', label: 'Menşei' },
      { key: 'DELIVERY', label: 'Teslimat Süresi' }
    ]
  },
  {
    key: 'sales',
    label: 'Satış Bilgileri',
    description: 'Paneldeki güncel fiyat ve stok; yalnız pazaryerindekinden farklı olan gider.',
    fields: [
      { key: 'PRICE', label: 'Fiyat' },
      { key: 'STOCK', label: 'Stok' }
    ]
  }
]

export const platformUpdateFieldKeys: PlatformUpdateField[] = platformUpdateGroups.flatMap(group => group.fields.map(field => field.key))
const productFieldKeys = new Set<PlatformUpdateField>(platformUpdateGroups[0].fields.map(field => field.key))
const fieldLabel = new Map(platformUpdateGroups.flatMap(group => group.fields.map(field => [field.key, field.label] as const)))

// One request of a marketplace: its fields always go together. extra: what else the request carries that is not a
// field of the dialog (shown on the card so nothing goes unsaid).
type PlatformUpdateRequest = { fields: PlatformUpdateField[]; extra?: string }
type PlatformUpdateProfile = { requests: PlatformUpdateRequest[]; note?: string }

// From the adapters (TrendyolWriteComposers, HepsiburadaProductUpdateComposer, PriceInventoryComposer):
// - Trendyol, approved product: content-bulk-update (title, description, images, attributes), variant-bulk-update
//   (desi, VAT rate, origin), delivery-info-bulk-update; price-and-inventory takes the price and the stock apart.
// - Hepsiburada: one product import update with all product information; the listing upload takes price and stock apart.
const profiles: Record<string, PlatformUpdateProfile> = {
  TRENDYOL: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES'] },
      { fields: ['DESI', 'VAT_RATE', 'ORIGIN'] },
      { fields: ['DELIVERY'] },
      { fields: ['PRICE'] },
      { fields: ['STOCK'] }
    ],
    note: 'Trendyol’un henüz onaylamadığı üründe ürün bilgisi seçimi tüm ürün kartını fiyat ve stokla birlikte gönderir.'
  },
  HEPSIBURADA: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'VAT_RATE'], extra: 'garanti süresi, barkod ve video' },
      { fields: ['PRICE'] },
      { fields: ['STOCK'] }
    ],
    note: 'Hepsiburada ürün bilgilerini tek pakette alır; menşei ve teslimat süresi gönderilmez.'
  }
}

const profileOf = (platformCode: string) => profiles[platformCode.trim().toUpperCase()]

export function platformUpdateFieldLabel(field: PlatformUpdateField) { return fieldLabel.get(field) ?? field }
export function platformUpdateNote(platformCode: string) { return profileOf(platformCode)?.note }

// The fields at least one of the marketplaces can take, in the dialog's order.
export function availablePlatformUpdateFields(platformCodes: string[]): PlatformUpdateField[] {
  const available = new Set(platformCodes.flatMap(code => profileOf(code)?.requests.flatMap(request => request.fields) ?? []))
  return platformUpdateFieldKeys.filter(field => available.has(field))
}

export type PlatformUpdatePlan = {
  // Every field that goes, in the dialog's order (a picked field's whole request).
  sends: PlatformUpdateField[]
  // Fields that go only because they share a request with a picked one.
  bundled: PlatformUpdateField[]
  // What the requests also carry that is not a field of the dialog.
  extras: string[]
  // Sent to the API: the picked product fields this marketplace takes, and the price/stock parts.
  productFields: PlatformUpdateField[]
  price: boolean
  stock: boolean
}

export function platformUpdatePlan(platformCode: string, picked: Iterable<PlatformUpdateField>): PlatformUpdatePlan {
  const selection = new Set(picked)
  const requests = (profileOf(platformCode)?.requests ?? []).filter(request => request.fields.some(field => selection.has(field)))
  const going = new Set(requests.flatMap(request => request.fields))
  const sends = platformUpdateFieldKeys.filter(field => going.has(field))
  return {
    sends,
    bundled: sends.filter(field => !selection.has(field)),
    extras: requests.flatMap(request => request.extra ? [request.extra] : []),
    productFields: sends.filter(field => productFieldKeys.has(field) && selection.has(field)),
    price: going.has('PRICE'),
    stock: going.has('STOCK')
  }
}

export function platformUpdateSummary(plan: PlatformUpdatePlan) {
  if (!plan.sends.length) return 'Seçili alanlardan hiçbiri bu platforma gönderilmiyor'
  return [...plan.sends.map(platformUpdateFieldLabel), ...plan.extras].join(', ')
}

// The old switch: on was every field, off was price and stock alone (also the quick update's pick).
export const defaultPlatformUpdateFields: PlatformUpdateField[] = [...platformUpdateFieldKeys]
export const priceStockPlatformUpdateFields: PlatformUpdateField[] = ['PRICE', 'STOCK']

export type PlatformUpdatePreferences = { fields: PlatformUpdateField[]; openGroups: PlatformUpdateGroupKey[] }
export const defaultPlatformUpdatePreferences: PlatformUpdatePreferences = { fields: defaultPlatformUpdateFields, openGroups: ['product', 'sales'] }

// Remembered in this browser ("nasıl bırakılırsa öyle kalsın"): the panel has no per-user preference store yet.
export const platformUpdatePreferencesKey = 'ravencia.platformUpdate.preferences.v1'

export function readPlatformUpdatePreferences(storage: Pick<Storage, 'getItem'> | undefined = safeLocalStorage()): PlatformUpdatePreferences {
  try {
    const raw = storage?.getItem(platformUpdatePreferencesKey)
    if (!raw) return defaultPlatformUpdatePreferences
    const value = JSON.parse(raw) as Partial<Record<keyof PlatformUpdatePreferences, unknown>>
    if (!Array.isArray(value.fields) || !Array.isArray(value.openGroups)) return defaultPlatformUpdatePreferences
    // Unknown entries (a later version's fields) are dropped; the known ones keep the dialog's order.
    const fields = platformUpdateFieldKeys.filter(field => (value.fields as unknown[]).includes(field))
    const openGroups = platformUpdateGroups.map(group => group.key).filter(key => (value.openGroups as unknown[]).includes(key))
    return { fields, openGroups }
  } catch {
    return defaultPlatformUpdatePreferences
  }
}

export function writePlatformUpdatePreferences(preferences: PlatformUpdatePreferences, storage: Pick<Storage, 'setItem'> | undefined = safeLocalStorage()) {
  try {
    storage?.setItem(platformUpdatePreferencesKey, JSON.stringify({ fields: preferences.fields, openGroups: preferences.openGroups }))
  } catch {
    // A private window or blocked storage: the pick still holds for this page.
  }
}

function safeLocalStorage(): Storage | undefined {
  try { return typeof window === 'undefined' ? undefined : window.localStorage } catch { return undefined }
}
