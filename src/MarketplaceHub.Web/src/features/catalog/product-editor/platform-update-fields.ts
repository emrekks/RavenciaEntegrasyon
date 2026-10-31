// "Platformlarda Güncelle": which product and sales fields go to which marketplace. The fields are picked in groups;
// each marketplace takes them in the requests its API has, and a request always carries all of its fields. A card
// therefore lists what really goes, including a field that was not picked but rides in the same request.

export type PlatformUpdateField = 'TITLE' | 'DESCRIPTION' | 'IMAGES' | 'ATTRIBUTES' | 'WARRANTY' | 'DESI' | 'ORIGIN' | 'DELIVERY' | 'PRICE' | 'STOCK' | 'VAT_RATE'
export type PlatformUpdateGroupKey = 'product' | 'shipping' | 'sales'

// linked: what a group shows that cannot be picked on its own; it goes whenever `with` goes.
export type PlatformUpdateLinkedField = { label: string; with: PlatformUpdateField; note: string }
export type PlatformUpdateGroup = { key: PlatformUpdateGroupKey; label: string; description: string; fields: { key: PlatformUpdateField; label: string }[]; linked?: PlatformUpdateLinkedField[]; hint?: string }

export const platformUpdateGroups: PlatformUpdateGroup[] = [
  {
    key: 'product',
    label: 'Ürün Bilgileri',
    description: 'Ürün kartının içeriği.',
    fields: [
      { key: 'TITLE', label: 'Ürün Adı' },
      { key: 'DESCRIPTION', label: 'Açıklama' },
      { key: 'IMAGES', label: 'Görseller' },
      { key: 'ATTRIBUTES', label: 'Kategori Özellikleri' },
      { key: 'WARRANTY', label: 'Garanti Süresi' }
    ],
    linked: [{ label: 'Renk / Beden', with: 'ATTRIBUTES', note: 'Varyant özellikleri kategori özellikleriyle aynı listede gider.' }]
  },
  {
    key: 'shipping',
    label: 'Kargo ve Teslimat',
    description: 'Kargo ücretini belirleyen desi, menşei ve teslimat süresi.',
    fields: [
      { key: 'DESI', label: 'Desi' },
      { key: 'ORIGIN', label: 'Menşei' },
      { key: 'DELIVERY', label: 'Teslimat Süresi' }
    ]
  },
  {
    key: 'sales',
    label: 'Satış Bilgileri',
    description: 'Paneldeki güncel fiyat ve stok yalnız pazaryerindekinden farklıysa gider; KDV oranı ürün bilgisiyle gider.',
    fields: [
      { key: 'PRICE', label: 'Fiyat' },
      { key: 'STOCK', label: 'Stok' },
      { key: 'VAT_RATE', label: 'KDV Oranı' }
    ],
    hint: 'Trendyol liste ve satış fiyatını birlikte alır; Hepsiburada yalnız satış fiyatını alır. Para birimi gönderilmez, fiyat TL gider. Otomatik fiyat-stok akışı açık olan mağazada panelde kaydedilen fiyat ve stok o akışla ayrıca da gider.'
  }
]

export const platformUpdateFieldKeys: PlatformUpdateField[] = platformUpdateGroups.flatMap(group => group.fields.map(field => field.key))
const salesPartKeys = new Set<PlatformUpdateField>(['PRICE', 'STOCK'])
const fieldLabel = new Map(platformUpdateGroups.flatMap(group => group.fields.map(field => [field.key, field.label] as const)))

// Information that every request carries to find the product, or that an update of a listed product never changes;
// shown as information, not as a pick.
export const platformUpdateFixedInformation: { label: string; note: string }[] = [
  { label: 'Barkod', note: 'Ürünü pazaryerinde bulmak için her istekte gider; bu pencereden değiştirilmez.' },
  { label: 'Stok Kodu', note: 'Trendyol’da Desi, KDV Oranı ve Menşei isteğiyle birlikte gider; Hepsiburada’da ürünü bulmak için (merchantSku) gider.' },
  { label: 'Marka, Kategori, Model Kodu', note: 'Yayındaki üründe güncellenmez. Trendyol bunları yalnız henüz onaylamadığı üründe tüm kartla alır; Hepsiburada güncellemesi hiç taşımaz.' },
  { label: 'Gönderilmeyenler', note: 'Kargo firması, ağırlık, para birimi ve video bu güncellemeyle hiçbir pazaryerine gitmez.' }
]

// One request of a marketplace: its fields always go together. sends: what the request carries, in the words the card
// uses (it names list and sale price apart, and what rides along that is not a pick of the dialog).
type PlatformUpdateRequest = { fields: PlatformUpdateField[]; sends: string[] }
type PlatformUpdateProfile = { requests: PlatformUpdateRequest[]; note?: string }

// From the adapters (TrendyolWriteComposers, HepsiburadaProductUpdateComposer, PriceInventoryComposer):
// - Trendyol, approved product: content-bulk-update (title, description, images, attributes with the variant's colour
//   and size), variant-bulk-update per barcode (stock code, desi, origin, VAT rate), delivery-info-bulk-update
//   (delivery duration); price-and-inventory takes the price (list and sale together) and the stock apart. Trendyol
//   takes no warranty.
// - Hepsiburada: one product update per hbSku with name, description, barcode, attributes (colour and size among them),
//   VAT, warranty, desi and images; inventory-uploads takes the sale price and the stock apart. No origin, no delivery.
const profiles: Record<string, PlatformUpdateProfile> = {
  TRENDYOL: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES'], sends: ['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Renk / Beden'] },
      { fields: ['DESI', 'VAT_RATE', 'ORIGIN'], sends: ['Desi', 'KDV Oranı', 'Menşei', 'Stok Kodu'] },
      { fields: ['DELIVERY'], sends: ['Teslimat Süresi'] },
      { fields: ['PRICE'], sends: ['Liste Fiyatı', 'Satış Fiyatı'] },
      { fields: ['STOCK'], sends: ['Stok'] }
    ],
    note: 'Trendyol’un henüz onaylamadığı üründe ürün bilgisi seçimi tüm ürün kartını gönderir: Marka, Kategori, Model Kodu, fiyat ve stok dahil.'
  },
  HEPSIBURADA: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'VAT_RATE'], sends: ['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Renk / Beden', 'Garanti Süresi', 'Desi', 'KDV Oranı', 'Barkod'] },
      { fields: ['PRICE'], sends: ['Satış Fiyatı'] },
      { fields: ['STOCK'], sends: ['Stok'] }
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
  // Every picked or riding field that goes, in the dialog's order (a picked field's whole request).
  fields: PlatformUpdateField[]
  // Fields that go only because they share a request with a picked one.
  bundled: PlatformUpdateField[]
  // Everything that goes, as the card lists it (request by request).
  sends: string[]
  // Sent to the API: the picked product fields this marketplace takes, and the price/stock parts.
  productFields: PlatformUpdateField[]
  price: boolean
  stock: boolean
}

export function platformUpdatePlan(platformCode: string, picked: Iterable<PlatformUpdateField>): PlatformUpdatePlan {
  const selection = new Set(picked)
  const requests = (profileOf(platformCode)?.requests ?? []).filter(request => request.fields.some(field => selection.has(field)))
  const going = new Set(requests.flatMap(request => request.fields))
  const fields = platformUpdateFieldKeys.filter(field => going.has(field))
  return {
    fields,
    bundled: fields.filter(field => !selection.has(field)),
    sends: [...new Set(requests.flatMap(request => request.sends))],
    productFields: fields.filter(field => !salesPartKeys.has(field) && selection.has(field)),
    price: going.has('PRICE'),
    stock: going.has('STOCK')
  }
}

export function platformUpdateSummary(plan: PlatformUpdatePlan) {
  if (!plan.sends.length) return 'Seçili alanlardan hiçbiri bu platforma gönderilmiyor'
  return plan.sends.join(', ')
}

// The old switch: on was every field, off was price and stock alone (also the quick update's pick).
export const defaultPlatformUpdateFields: PlatformUpdateField[] = [...platformUpdateFieldKeys]
export const priceStockPlatformUpdateFields: PlatformUpdateField[] = ['PRICE', 'STOCK']

export type PlatformUpdatePreferences = { fields: PlatformUpdateField[]; openGroups: PlatformUpdateGroupKey[] }
const platformUpdateGroupKeys = platformUpdateGroups.map(group => group.key)
export const defaultPlatformUpdatePreferences: PlatformUpdatePreferences = { fields: defaultPlatformUpdateFields, openGroups: platformUpdateGroupKeys }

// Remembered in this browser ("nasıl bırakılırsa öyle kalsın"): the panel has no per-user preference store yet.
export const platformUpdatePreferencesKey = 'ravencia.platformUpdate.preferences.v1'
// What the dialog offered before the stored record named what it knew (the first version): a field or group added
// since is not in the record, and starts picked (open) instead of reading as switched off.
const firstFieldKeys = ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'VAT_RATE', 'ORIGIN', 'DELIVERY', 'PRICE', 'STOCK']
const firstGroupKeys = ['product', 'sales']

export function readPlatformUpdatePreferences(storage: Pick<Storage, 'getItem'> | undefined = safeLocalStorage()): PlatformUpdatePreferences {
  try {
    const raw = storage?.getItem(platformUpdatePreferencesKey)
    if (!raw) return defaultPlatformUpdatePreferences
    const value = JSON.parse(raw) as Partial<Record<'fields' | 'openGroups' | 'knownFields' | 'knownGroups', unknown>>
    if (!Array.isArray(value.fields) || !Array.isArray(value.openGroups)) return defaultPlatformUpdatePreferences
    const known = (list: unknown, first: string[]) => Array.isArray(list) ? list as unknown[] : first
    const knownFields = known(value.knownFields, firstFieldKeys)
    const knownGroups = known(value.knownGroups, firstGroupKeys)
    // Unknown entries (a later version's fields) are dropped; a field the record did not know yet starts picked; the
    // rest keep the dialog's order.
    const fields = platformUpdateFieldKeys.filter(field => (value.fields as unknown[]).includes(field) || !knownFields.includes(field))
    const openGroups = platformUpdateGroupKeys.filter(key => (value.openGroups as unknown[]).includes(key) || !knownGroups.includes(key))
    return { fields, openGroups }
  } catch {
    return defaultPlatformUpdatePreferences
  }
}

export function writePlatformUpdatePreferences(preferences: PlatformUpdatePreferences, storage: Pick<Storage, 'setItem'> | undefined = safeLocalStorage()) {
  try {
    storage?.setItem(platformUpdatePreferencesKey, JSON.stringify({ fields: preferences.fields, openGroups: preferences.openGroups, knownFields: platformUpdateFieldKeys, knownGroups: platformUpdateGroupKeys }))
  } catch {
    // A private window or blocked storage: the pick still holds for this page.
  }
}

function safeLocalStorage(): Storage | undefined {
  try { return typeof window === 'undefined' ? undefined : window.localStorage } catch { return undefined }
}
