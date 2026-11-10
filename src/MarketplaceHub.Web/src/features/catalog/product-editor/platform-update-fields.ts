// "Platformlarda Güncelle": which product and sales fields go to which marketplace. The fields are picked in groups;
// each marketplace takes them in the requests its API has, and a request always carries all of its fields. A card
// therefore lists what really goes, including a field that was not picked but rides in the same request.

export type PlatformUpdateField = 'TITLE' | 'DESCRIPTION' | 'IMAGES' | 'ATTRIBUTES' | 'WARRANTY' | 'DESI' | 'DELIVERY' | 'PRICE' | 'STOCK' | 'VAT_RATE'
export type PlatformUpdateGroupKey = 'product' | 'shipping' | 'sales'

// linked: what a group shows that cannot be picked on its own; it goes whenever `with` goes.
export type PlatformUpdateLinkedField = { label: string; with: PlatformUpdateField }
export type PlatformUpdateGroup = { key: PlatformUpdateGroupKey; label: string; fields: { key: PlatformUpdateField; label: string }[]; linked?: PlatformUpdateLinkedField[] }

export const platformUpdateGroups: PlatformUpdateGroup[] = [
  {
    key: 'product',
    label: 'Ürün Bilgileri',
    fields: [
      { key: 'TITLE', label: 'Ürün Adı' },
      { key: 'DESCRIPTION', label: 'Açıklama' },
      { key: 'IMAGES', label: 'Görseller' },
      { key: 'ATTRIBUTES', label: 'Kategori Özellikleri' },
      { key: 'WARRANTY', label: 'Garanti Süresi' }
    ],
    linked: [
      { label: 'Renk / Beden', with: 'ATTRIBUTES' },
      { label: 'Menşei', with: 'ATTRIBUTES' }
    ]
  },
  {
    key: 'shipping',
    label: 'Kargo ve Teslimat',
    fields: [
      { key: 'DESI', label: 'Desi' },
      { key: 'DELIVERY', label: 'Teslimat Süresi' }
    ]
  },
  {
    key: 'sales',
    label: 'Satış Bilgileri',
    fields: [
      { key: 'PRICE', label: 'Fiyat' },
      { key: 'STOCK', label: 'Stok' },
      { key: 'VAT_RATE', label: 'KDV Oranı' }
    ]
  }
]

export const platformUpdateFieldKeys: PlatformUpdateField[] = platformUpdateGroups.flatMap(group => group.fields.map(field => field.key))
const salesPartKeys = new Set<PlatformUpdateField>(['PRICE', 'STOCK'])
const fieldLabel = new Map(platformUpdateGroups.flatMap(group => group.fields.map(field => [field.key, field.label] as const)))

// One request of a marketplace: its fields always go together. sends: the dialog's fields as the card names them (list
// and sale price apart); extra: what the request also carries that is not a pick of the dialog.
type PlatformUpdateRequest = { fields: PlatformUpdateField[]; sends: string[]; extra?: string[] }
// wholeCard: the marketplace takes the product in one request (price and stock included), so whatever is picked goes as the
// product request and nothing goes to the separate price and stock send.
type PlatformUpdateProfile = { requests: PlatformUpdateRequest[]; note?: string; wholeCard?: boolean }

// From the adapters (TrendyolWriteComposers, HepsiburadaProductUpdateComposer, PriceInventoryComposer):
// - Trendyol, approved product: content-bulk-update carries only the picked content fields (title, description,
//   images, attributes: Trendyol takes a partial update there, the attributes always as the whole list with colour,
//   size and Menşei), variant-bulk-update per barcode with only the picked desi or VAT rate (a
//   partial update; no stock code or origin, a null would delete the field), delivery-info-bulk-update (the product card's Teslimat Süresi); price-and-inventory takes the price (list and sale
//   together) and the stock apart. Trendyol takes no warranty.
// - Hepsiburada: one product update per hbSku with name, description, barcode, attributes, VAT, warranty, desi and
//   images; the listing shipping-info upload takes the dispatch time (DispatchTime, the card's Teslimat Süresi) alone;
//   inventory-uploads takes the sale price and the stock apart. (The update has a video slot, but the publication never
//   fills it, so no video goes.)
// - Shopify: the publication productSet again, under the handle the panel gave the product: title, description, images,
//   the colour/size options, the product type, price and stock of the whole model family in one request.
// - Pazarama: product/vatRate/bulk takes the VAT rate per barcode; it has no content update API. Price and stock go by
//   barcode in the price/stock send, which builds them together (each only when it differs from Pazarama's).
// - N11: one product-update task per stock code that carries only the picked fields (N11ProductUpdateComposer):
//   description, the category attributes (the whole list, brand and colour/size included, since N11 asks for every
//   mandatory one), the VAT rate and the preparing day with the connection's shipment template. Name, images, brand,
//   category, colour/size, barcode and desi cannot change through the N11 API. The price-stock task takes the list and
//   sale price and the stock together.
// - PttAVM: one UpdateProductsV3 request with the picked fields and every variant PttAVM holds (PttAvmProductUpdate).
// The API keeps the same table (ProductUpdateFields.SupportedBy).
const profiles: Record<string, PlatformUpdateProfile> = {
  TRENDYOL: {
    requests: [
      // One content-bulk-update, but with only the picked fields in it: each content field is a pick of its own.
      { fields: ['TITLE'], sends: ['Ürün Adı'] },
      { fields: ['DESCRIPTION'], sends: ['Açıklama'] },
      { fields: ['IMAGES'], sends: ['Görseller'] },
      { fields: ['ATTRIBUTES'], sends: ['Kategori Özellikleri'], extra: ['Renk / Beden', 'Menşei'] },
      // One variant-bulk-update with the barcode and only the picked field (Trendyol takes a partial update there and
      // deletes a field sent as null), so desi and VAT rate are picks of their own.
      { fields: ['DESI'], sends: ['Desi'] },
      { fields: ['VAT_RATE'], sends: ['KDV Oranı'] },
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
  },
  N11: {
    requests: [
      { fields: ['DESCRIPTION'], sends: ['Açıklama'] },
      { fields: ['ATTRIBUTES'], sends: ['Kategori Özellikleri'], extra: ['Marka', 'Renk / Beden'] },
      { fields: ['VAT_RATE'], sends: ['KDV Oranı'] },
      { fields: ['DELIVERY'], sends: ['Teslimat Süresi'], extra: ['Kargo Şablonu'] },
      { fields: ['PRICE', 'STOCK'], sends: ['Liste Fiyatı', 'Satış Fiyatı', 'Stok'] }
    ],
    note: 'N11 ürün adını, görselleri, markayı, kategoriyi, renk/bedeni, barkodu ve desiyi API ile güncellemez; bunlar N11 satıcı panelinden talep edilir. Kategori özellikleri marka ve renk/beden dahil tüm listeyle gider, N11 bunları değiştirmez. Fiyat ve stok birlikte gider.'
  },
  // PttAVM (PttAvmProductUpdate.cs): one UpdateProductsV3 request with only the picked fields (PttAVM keeps a field left
  // out), but always every variant PttAVM holds, or the upsert deletes the missing ones. No category attributes (the API
  // has none but colour and size). Price and stock go together in UpdateProductsStockPrice, for the whole model family.
  PTTAVM: {
    requests: [
      { fields: ['TITLE'], sends: ['Ürün Adı'], extra: ['Varyant Listesi'] },
      { fields: ['DESCRIPTION'], sends: ['Açıklama'], extra: ['Varyant Listesi'] },
      { fields: ['IMAGES'], sends: ['Görseller'], extra: ['Varyant Listesi'] },
      { fields: ['WARRANTY'], sends: ['Garanti Süresi'], extra: ['Varyant Listesi'] },
      { fields: ['DESI'], sends: ['Desi'], extra: ['Varyant Listesi'] },
      { fields: ['DELIVERY'], sends: ['Teslimat Süresi'], extra: ['Varyant Listesi'] },
      { fields: ['VAT_RATE'], sends: ['KDV Oranı'], extra: ['Varyant Listesi'] },
      { fields: ['PRICE', 'STOCK'], sends: ['Satış Fiyatı', 'Stok'] }
    ],
    note: 'PttAVM yalnız seçilen alanları alır; ürün varyantlıysa PttAVM’deki tüm varyantlar olduğu gibi (barkod, renk, beden, stok) isteğe eklenir, yoksa silinirdi. Kategori özellikleri gönderilmez; fiyat ve stok birlikte gider.'
  }
}

const profileOf = (platformCode: string) => profiles[platformCode.trim().toUpperCase()]

export function platformUpdateFieldLabel(field: PlatformUpdateField) { return fieldLabel.get(field) ?? field }

// The fields at least one of the marketplaces can take, in the dialog's order.
export function availablePlatformUpdateFields(platformCodes: string[]): PlatformUpdateField[] {
  const available = new Set(platformCodes.flatMap(code => profileOf(code)?.requests.flatMap(request => request.fields) ?? []))
  return platformUpdateFieldKeys.filter(field => available.has(field))
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
  // Picked fields this marketplace does not take: they stay out of its requests.
  skipped: PlatformUpdateField[]
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
    skipped: platformUpdateFieldKeys.filter(field => selection.has(field) && !going.has(field)),
    productFields: profile?.wholeCard ? fields : fields.filter(field => !salesPartKeys.has(field) && selection.has(field)),
    price: !profile?.wholeCard && going.has('PRICE'),
    stock: !profile?.wholeCard && going.has('STOCK')
  }
}

// Whether the save before this send writes the marketplace's price offers: only when what was picked is held on the
// offer, the price, the stock (its safety stock) or the VAT rate, or the marketplace takes the whole card (Shopify). A send
// of pictures alone writes no price (live 10 Nov 2026, MZ052BOD: Görseller alone wrote 4 offers and warned of 5 unpriced
// variants); Hepsiburada's package carries the offer's VAT rate as it is.
const offerFieldKeys = new Set<PlatformUpdateField>(['PRICE', 'STOCK', 'VAT_RATE'])
export function platformUpdateWritesOffers(platformCode: string, picked: Iterable<PlatformUpdateField>) {
  const selection = new Set(picked)
  const plan = platformUpdatePlan(platformCode, selection)
  const wholeCard = Boolean(profileOf(platformCode)?.wholeCard)
  return plan.price || plan.stock || plan.fields.some(field => offerFieldKeys.has(field) && (wholeCard || selection.has(field)))
}

// How the toast names the product information that goes: the one picked field by its name ("görseller"), else
// "ürün bilgisi".
export function productInformationLabel(productFields: readonly PlatformUpdateField[]) {
  return productFields.length === 1 ? platformUpdateFieldLabel(productFields[0]).toLocaleLowerCase('tr-TR') : 'ürün bilgisi'
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
// With nothing saved yet, the window starts at what Hızlı Güncelle sends then: price and stock.
export const defaultPlatformUpdatePreferences: PlatformUpdatePreferences = { fields: priceStockPlatformUpdateFields, openGroups: platformUpdateGroupKeys }

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

// The stores picked last, kept the same way: the next open starts with them, not with every store. A store no longer
// updateable is dropped, and one added since is not picked; with no record yet, `first` is used.
export const platformUpdateStoresKey = 'ravencia.platformUpdate.stores.v1'

export function readPlatformUpdateStores(updateableIds: string[], first: string[], storage: Pick<Storage, 'getItem'> | undefined = safeLocalStorage()): string[] {
  const fallback = first.filter(id => updateableIds.includes(id))
  try {
    const raw = storage?.getItem(platformUpdateStoresKey)
    if (!raw) return fallback
    const value = JSON.parse(raw) as { selected?: unknown }
    if (!Array.isArray(value.selected)) return fallback
    return updateableIds.filter(id => (value.selected as unknown[]).includes(id))
  } catch {
    return fallback
  }
}

export function writePlatformUpdateStores(selected: string[], storage: Pick<Storage, 'setItem'> | undefined = safeLocalStorage()) {
  try {
    storage?.setItem(platformUpdateStoresKey, JSON.stringify({ selected }))
  } catch {
    // A private window or blocked storage: the pick still holds for this page.
  }
}

// Owner, 9 Nov 2026: Hızlı Güncelle sends at once, with no window, what the settings window (the button on its right)
// last saved: those fields to those stores. With no saved fields it sends price and stock; with no saved stores, the
// stores picked for the product. Only updateable stores go.
export type QuickPlatformUpdateChoice = { connectionIds: string[]; fields: PlatformUpdateField[] }

export function readQuickPlatformUpdateChoice(updateableIds: string[], fallbackStoreIds: string[], storage: Pick<Storage, 'getItem'> | undefined = safeLocalStorage()): QuickPlatformUpdateChoice {
  const saved = (key: string) => { try { return Boolean(storage?.getItem(key)) } catch { return false } }
  const fields = saved(platformUpdatePreferencesKey) ? readPlatformUpdatePreferences(storage).fields : priceStockPlatformUpdateFields
  const fallback = [...new Set(fallbackStoreIds)].filter(id => updateableIds.includes(id))
  const connectionIds = saved(platformUpdateStoresKey) ? readPlatformUpdateStores(updateableIds, fallback, storage) : fallback
  return { connectionIds, fields }
}

function safeLocalStorage(): Storage | undefined {
  try { return typeof window === 'undefined' ? undefined : window.localStorage } catch { return undefined }
}
