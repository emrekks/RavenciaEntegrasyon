// "Platformlarda Güncelle": which product and sales fields go to which marketplace. The fields are picked in groups;
// each marketplace takes them in the requests its API has, and a request always carries all of its fields. A card
// therefore lists what really goes, including a field that was not picked but rides in the same request.

export type PlatformUpdateField = 'TITLE' | 'DESCRIPTION' | 'IMAGES' | 'ATTRIBUTES' | 'WARRANTY' | 'DESI' | 'DELIVERY' | 'PRICE' | 'STOCK' | 'VAT_RATE'
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
    linked: [
      { label: 'Renk / Beden', with: 'ATTRIBUTES', note: 'Renk ve beden kategori özellikleriyle aynı listede gider.' },
      { label: 'Menşei', with: 'ATTRIBUTES', note: 'Menşei bir kategori özelliğidir; Trendyol’da Desi ve KDV Oranı isteği de ilandaki menşeiyi taşır.' }
    ]
  },
  {
    key: 'shipping',
    label: 'Kargo ve Teslimat',
    description: 'Kargo ücretini belirleyen desi ve ürün kartındaki teslimat süresi.',
    fields: [
      { key: 'DESI', label: 'Desi' },
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
    hint: 'Trendyol liste ve satış fiyatını birlikte alır; Hepsiburada yalnız satış fiyatını alır. Para birimi gönderilmez, fiyat TL gider.'
  }
]

export const platformUpdateFieldKeys: PlatformUpdateField[] = platformUpdateGroups.flatMap(group => group.fields.map(field => field.key))
const salesPartKeys = new Set<PlatformUpdateField>(['PRICE', 'STOCK'])
const fieldLabel = new Map(platformUpdateGroups.flatMap(group => group.fields.map(field => [field.key, field.label] as const)))

// Information that every request carries to find the product, or that an update of a listed product never changes;
// shown as information, not as a pick.
export const platformUpdateFixedInformation: { label: string; note: string }[] = [
  { label: 'Barkod', note: 'Ürünü pazaryerinde bulmak için her istekte gider; bu pencereden değiştirilmez.' },
  { label: 'Stok Kodu', note: 'Trendyol’da Desi ve KDV Oranı isteğiyle birlikte gider; Hepsiburada’da ürünü bulmak için (merchantSku) gider.' },
  { label: 'Marka, Kategori, Model Kodu', note: 'Yayındaki üründe güncellenmez. Trendyol bunları yalnız henüz onaylamadığı üründe tüm kartla alır; Hepsiburada güncellemesi hiç taşımaz.' },
  { label: 'Gönderilmeyenler', note: 'Kargo firması, ağırlık, para birimi ve video bu güncellemeyle hiçbir pazaryerine gitmez.' }
]

// One request of a marketplace: its fields always go together. sends: the dialog's fields as the card names them (list
// and sale price apart); extra: what the request also carries that is not a pick of the dialog.
type PlatformUpdateRequest = { fields: PlatformUpdateField[]; sends: string[]; extra?: string[] }
// wholeCard: the marketplace takes the product in one request (price and stock included), so whatever is picked goes as the
// product request and nothing goes to the separate price and stock send.
type PlatformUpdateProfile = { requests: PlatformUpdateRequest[]; note?: string; wholeCard?: boolean }

// From the adapters (TrendyolWriteComposers, HepsiburadaProductUpdateComposer, PriceInventoryComposer):
// - Trendyol, approved product: content-bulk-update (title, description, images, attributes with colour, size and
//   Menşei), variant-bulk-update per barcode (desi, VAT rate; the stock code and the listing's origin ride along),
//   delivery-info-bulk-update (the product card's Teslimat Süresi); price-and-inventory takes the price (list and sale
//   together) and the stock apart. Trendyol takes no warranty.
// - Hepsiburada: one product update per hbSku with name, description, barcode, attributes, VAT, warranty, desi and
//   images; the listing shipping-info upload takes the dispatch time (DispatchTime, the card's Teslimat Süresi) alone;
//   inventory-uploads takes the sale price and the stock apart. (The update has a video slot, but the publication never
//   fills it, so no video goes.)
// - Shopify: the publication productSet again, under the handle the panel gave the product: title, description, images,
//   the colour/size options, the product type, price and stock of the whole model family in one request.
// - Pazarama: product/vatRate/bulk takes the VAT rate per barcode; it has no content update API. Price and stock go by
//   barcode in the price/stock send, which builds them together (each only when it differs from Pazarama's).
// - n11 and PTT AVM have no product update in the panel yet: no field of the dialog goes there.
// The API keeps the same table (ProductUpdateFields.SupportedBy).
const profiles: Record<string, PlatformUpdateProfile> = {
  TRENDYOL: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES'], sends: ['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri'], extra: ['Renk / Beden', 'Menşei'] },
      { fields: ['DESI', 'VAT_RATE'], sends: ['Desi', 'KDV Oranı'], extra: ['Menşei', 'Stok Kodu'] },
      { fields: ['DELIVERY'], sends: ['Teslimat Süresi'] },
      { fields: ['PRICE'], sends: ['Liste Fiyatı', 'Satış Fiyatı'] },
      { fields: ['STOCK'], sends: ['Stok'] }
    ],
    note: 'Trendyol’un henüz onaylamadığı üründe ürün bilgisi seçimi tüm ürün kartını gönderir: Marka, Kategori, Model Kodu, fiyat ve stok dahil.'
  },
  HEPSIBURADA: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'VAT_RATE'], sends: ['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Garanti Süresi', 'Desi', 'KDV Oranı'], extra: ['Renk / Beden', 'Menşei', 'Barkod'] },
      { fields: ['DELIVERY'], sends: ['Teslimat Süresi'] },
      { fields: ['PRICE'], sends: ['Satış Fiyatı'] },
      { fields: ['STOCK'], sends: ['Stok'] }
    ],
    note: 'Hepsiburada ürün bilgilerini tek pakette alır; teslimat süresi (kargoya veriliş süresi) ayrı istekle gider.'
  },
  PAZARAMA: {
    requests: [
      { fields: ['VAT_RATE'], sends: ['KDV Oranı'] },
      { fields: ['PRICE', 'STOCK'], sends: ['Liste Fiyatı', 'Satış Fiyatı', 'Stok'] }
    ],
    note: 'Pazarama API ile ürün içeriği güncellenmez: ürün adı, açıklama ve görseller yalnız Pazarama’nın kendi panelinden değişir. Fiyat ve stok birlikte seçilir.'
  },
  SHOPIFY: {
    requests: [
      { fields: ['TITLE', 'DESCRIPTION', 'IMAGES', 'PRICE', 'STOCK'], sends: ['Ürün Adı', 'Açıklama', 'Görseller', 'Liste Fiyatı', 'Satış Fiyatı', 'Stok'], extra: ['Renk / Beden', 'Ürün Tipi'] }
    ],
    note: 'Shopify ürünü tek istekte alır: hangi alan seçilirse seçilsin model ailesinin tüm kartı (ürün adı, açıklama, görseller, renk ve beden, fiyat ve stok) birlikte gider.',
    wholeCard: true
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

export function platformTakesUpdateField(platformCode: string, field: PlatformUpdateField) {
  return profileOf(platformCode)?.requests.some(request => request.fields.includes(field)) ?? false
}

// The platform codes of a field that only some of the stores take (shown beside it: "Yalnız Trendyol"); empty when every
// store takes it or none does.
export function partialPlatformUpdateField(field: PlatformUpdateField, platformCodes: string[]): string[] {
  const takers = platformCodes.filter(code => platformTakesUpdateField(code, field))
  if (!takers.length || takers.length === platformCodes.length) return []
  return [...new Set(takers.map(code => code.trim().toUpperCase()))]
}

// The pick after the stores changed: a field no longer shown is dropped, a field shown only now comes in ticked, the
// rest stays as the user left it.
export function platformUpdateFieldsForStores(fields: PlatformUpdateField[], before: PlatformUpdateField[], after: PlatformUpdateField[]): PlatformUpdateField[] {
  const picked = new Set(fields.filter(field => after.includes(field)))
  for (const field of after) if (!before.includes(field)) picked.add(field)
  return platformUpdateFieldKeys.filter(field => picked.has(field))
}

export type PlatformUpdatePlan = {
  // Every picked or riding field of the dialog that goes, in the dialog's order (a picked field's whole request).
  fields: PlatformUpdateField[]
  // Fields that go only because they share a request with a picked one.
  bundled: PlatformUpdateField[]
  // The dialog's fields that go, as the card names them (request by request).
  sends: string[]
  // What the requests also carry that is not a pick of the dialog.
  extras: string[]
  // Sent to the API: the picked product fields this marketplace takes, and the price/stock parts.
  productFields: PlatformUpdateField[]
  price: boolean
  stock: boolean
}

export function platformUpdatePlan(platformCode: string, picked: Iterable<PlatformUpdateField>): PlatformUpdatePlan {
  const profile = profileOf(platformCode)
  const selection = new Set(picked)
  const requests = (profile?.requests ?? []).filter(request => request.fields.some(field => selection.has(field)))
  const going = new Set(requests.flatMap(request => request.fields))
  const fields = platformUpdateFieldKeys.filter(field => going.has(field))
  const sends = [...new Set(requests.flatMap(request => request.sends))]
  return {
    fields,
    bundled: fields.filter(field => !selection.has(field)),
    sends,
    extras: [...new Set(requests.flatMap(request => request.extra ?? []))].filter(label => !sends.includes(label)),
    productFields: profile?.wholeCard ? fields : fields.filter(field => !salesPartKeys.has(field) && selection.has(field)),
    price: !profile?.wholeCard && going.has('PRICE'),
    stock: !profile?.wholeCard && going.has('STOCK')
  }
}

export function platformUpdateSummary(plan: PlatformUpdatePlan) {
  if (!plan.sends.length) return 'Seçili alanlardan hiçbiri bu platforma gönderilmiyor'
  return [...plan.sends, ...plan.extras].join(', ')
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
// since is not in the record, and starts picked (open) instead of reading as switched off. ORIGIN has left the dialog.
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
    // Unknown entries (a later version's fields, a retired one) are dropped; a field the record did not know yet starts
    // picked; the rest keep the dialog's order.
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
