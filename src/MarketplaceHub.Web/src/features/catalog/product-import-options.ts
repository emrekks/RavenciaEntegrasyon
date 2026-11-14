export type ProductImportMode = 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY' | 'OPTIONS_ONLY'
export type ProductImportMethod = 'BULK' | 'SINGLE'

export type ProductImportOptionVisibility = {
  // "Mevcut Ürünleri Güncelle" is offered as a scope only when a selected platform can write something with it.
  showExistingMode: boolean
  // "Mevcut Ürün Bilgilerini Güncelle"
  showUpdateExisting: boolean
  // "Fiyatları Güncelle" / "Stokları Güncelle"
  showPriceStock: boolean
  // In a mixed selection: which of the selected platforms a choice applies to ("Yalnız Trendyol'da uygulanır.").
  updateExistingScopeNote: string | null
  priceStockScopeNote: string | null
  // What the selected platforms that only observe leave untouched.
  readOnlyNotes: string[]
  showArchived: boolean
  showPendingApproval: boolean
  showOptionsSection: boolean
}

const platformNames: Record<string, string> = { TRENDYOL: 'Trendyol', SHOPIFY: 'Shopify', HEPSIBURADA: 'Hepsiburada', N11: 'N11', PAZARAMA: 'Pazarama', PTTAVM: 'PttAVM' }
const locative: Record<string, string> = { TRENDYOL: "Trendyol'da", SHOPIFY: "Shopify'da" }

// What a product import really writes on a product the panel already holds (MarketplaceJobProcessor.UpsertCatalogProduct):
// only Trendyol takes the listing's content ("Mevcut Ürün Bilgilerini Güncelle"); Hepsiburada, n11, Pazarama, PTT AVM and
// Shopify run observe-only and keep the panel's card. Prices and stock are taken on Trendyol and Shopify only; the API
// drops them for Hepsiburada, n11, Pazarama and PTT AVM (MarketplaceSalesService.EnqueueProductSyncAsync).
export function productImportWritesContent(platformCode: string): boolean {
  return platformCode.trim().toUpperCase() === 'TRENDYOL'
}

export function productImportWritesPriceStock(platformCode: string): boolean {
  return ['TRENDYOL', 'SHOPIFY'].includes(platformCode.trim().toUpperCase())
}

function distinctCodes(platformCodes: string[]): string[] {
  return [...new Set(platformCodes.map(code => code.trim().toUpperCase()).filter(Boolean))]
}

function scopeNote(codes: string[], honours: (code: string) => boolean): string | null {
  const honouring = codes.filter(honours)
  if (!honouring.length || honouring.length === codes.length) return null
  const names = honouring.map(code => platformNames[code] ?? code)
  const last = locative[honouring[honouring.length - 1]] ?? names[names.length - 1]
  return `Yalnız ${[...names.slice(0, -1), last].join(' ve ')} uygulanır.`
}

export function productImportOptionVisibility(options: {
  method: ProductImportMethod
  mode: ProductImportMode
  platformCodes: string[]
  supportsPendingApproval: boolean
}): ProductImportOptionVisibility {
  const isBulk = options.method === 'BULK'
  const codes = distinctCodes(options.platformCodes)
  // Before a connection is chosen nothing is known yet: every choice stays on offer.
  const anyContent = !codes.length || codes.some(productImportWritesContent)
  const anyPriceStock = !codes.length || codes.some(productImportWritesPriceStock)
  if (isBulk && options.mode === 'OPTIONS_ONLY') {
    return {
      showExistingMode: anyPriceStock,
      showUpdateExisting: false,
      showPriceStock: false,
      updateExistingScopeNote: null,
      priceStockScopeNote: null,
      readOnlyNotes: [],
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    }
  }
  // A single product (Tek Ürün Aktarımı) offers the same choices as an update of existing products (owner, 5 Nov 2026).
  const updateScope = isBulk ? options.mode === 'EXISTING_ONLY' : true
  const showUpdateExisting = updateScope && anyContent
  const showPriceStock = updateScope && anyPriceStock
  const readOnlyNotes: string[] = []
  if (!isBulk || options.mode !== 'MAPPING_ONLY') {
    const observed = codes.filter(code => !productImportWritesPriceStock(code) || code === 'SHOPIFY' && !updateScope)
    if (observed.length) readOnlyNotes.push(`${observed.map(code => platformNames[code] ?? code).join(', ')}: panelde var olan ürünün bilgisi, fiyatı ve stoğu yazılmaz; ürün yalnız eşlenir, eksik varyantı ve boş desisi eklenir.`)
    if (updateScope && codes.includes('SHOPIFY')) readOnlyNotes.push('Shopify: panelde var olan ürünün bilgisi yazılmaz; yalnız fiyat ve stok seçenekleri uygulanır.')
  }
  // Every mode reads archived listings only when asked, Ürün Eşleme too (owner, 12 Nov 2026: "Arşiv ürünlerini dahil et
  // de olması lazım bunun gelişmiş seçeneklerinde"); the API no longer forces it on for mapping.
  const showArchived = true
  // The API reads listings awaiting approval only in Tam Katalog Taraması, Yeni Ürünleri Aktar and Ürün Eşleme
  // (MarketplaceSalesService.EnqueueProductSyncAsync drops it for Mevcut Ürünleri Güncelle), so it is offered there only.
  const showPendingApproval = isBulk && options.supportsPendingApproval && options.mode !== 'EXISTING_ONLY'

  const hasVisibleOptions = showUpdateExisting || showPriceStock || readOnlyNotes.length > 0 || showArchived || showPendingApproval

  return {
    showExistingMode: anyPriceStock,
    showUpdateExisting,
    showPriceStock,
    updateExistingScopeNote: showUpdateExisting ? scopeNote(codes, productImportWritesContent) : null,
    priceStockScopeNote: showPriceStock ? scopeNote(codes, productImportWritesPriceStock) : null,
    readOnlyNotes,
    showArchived,
    showPendingApproval,
    showOptionsSection: hasVisibleOptions
  }
}

// The line under each bulk scope, saying what the run really does (MarketplaceJobProcessor.UpsertCatalogProduct): a full
// scan opens the listings the panel lacks and keeps every card it holds (only an empty desi is filled; on Trendyol a size
// the card lacks is not added but reported as "Eklenmeyen yeni beden"). Hepsiburada never opens a product.
export function productImportModeDescription(mode: ProductImportMode, platformCodes: string[]): string {
  const codes = distinctCodes(platformCodes)
  if (mode === 'FULL') {
    if (codes.length && codes.every(code => code === 'HEPSIBURADA')) return 'Tüm Hepsiburada ürünlerini okur ve panel ürünleriyle eşler; yeni ürün açmaz, panelde var olan ürün içeriği değişmez.'
    return `Pazar yerindeki tüm ürünleri okur ve panelde olmayanları ekler. Panelde var olan ürünlerin içeriği, fiyatı ve stoğu değişmez; yalnız boş desisi doldurulur.${!codes.length || codes.includes('TRENDYOL') ? ' Trendyol’da panelde olmayan yeni bedenler eklenmez, iş özetinde bildirilir.' : ''}`
  }
  if (mode === 'NEW_ONLY') return 'Panelde henüz bulunmayan ürünleri ve mevcut ürünlerin yeni bedenlerini ekler; mevcut ürünleri güncellemez.'
  if (mode === 'EXISTING_ONLY') return 'Yalnız panelde kayıtlı ürünleri işler; aşağıda seçtiğiniz bilgiler pazar yerindeki haline göre yenilenir.'
  if (mode === 'MAPPING_ONLY') return 'Panel ürünlerini pazar yerindeki karşılıklarıyla eşler; içerik, fiyat ve stok değişmez.'
  return ''
}

// The three choices of Mevcut Ürünleri Güncelle (and Tek Ürün Aktarımı), owner 12 Nov 2026: "Mevcut Ürün Bilgilerini
// Güncelle" takes every attribute and the model's new variants; without it, only the ticked prices and/or stock change.
export type ProductImportChoices = { updateExisting: boolean; updatePrices: boolean; updateStock: boolean }

// A Trendyol Mevcut Ürünleri Güncelle run with none ticked would change nothing; the start button waits for a choice
// (the API refuses it too: PRODUCT_SYNC_NOTHING_SELECTED).
export function productImportNeedsChoice(options: { method: ProductImportMethod; mode: ProductImportMode; hasTrendyol: boolean; showUpdateExisting: boolean } & ProductImportChoices): boolean {
  // The caller passes only the choices on offer (a hidden box counts as unticked).
  return options.method === 'BULK' && options.mode === 'EXISTING_ONLY' && options.hasTrendyol && options.showUpdateExisting
    && !options.updateExisting && !options.updatePrices && !options.updateStock
}

// The one line beside the start button that says what the run will change.
export function productImportChoiceSummary(method: ProductImportMethod, choices: ProductImportChoices): string {
  const { updateExisting, updatePrices, updateStock } = choices
  if (updateExisting) {
    const tail = updatePrices && updateStock ? ', fiyat ve stok' : updatePrices ? ' ve fiyat' : updateStock ? ' ve stok' : ''
    return `Tüm ürün bilgileri, yeni varyantlar${tail} güncellenecek${!updatePrices && !updateStock ? '; fiyat ve stok korunacak' : ''}`
  }
  if (updatePrices && updateStock) return 'Fiyat ve stok güncellenecek'
  if (updatePrices) return 'Sadece fiyat güncellenecek'
  if (updateStock) return 'Sadece stok güncellenecek'
  return method === 'SINGLE' ? 'Ürün panelde yoksa eklenir; panelde varsa değişmez' : 'En az bir seçenek işaretleyin'
}

// Prices and stock are two separate choices; each one only decides whether the marketplace value overwrites
// the value the panel already holds for an existing product.
export function productImportOverwriteConfirmation(updatePrices: boolean, updateStock: boolean): { title: string; message: string } | null {
  if (!updatePrices && !updateStock) return null
  const stockNote = ' Değişen stoklar, otomatik gönderimi açık olan diğer pazaryerlerine de gönderilir.'
  if (updatePrices && updateStock) {
    return {
      title: 'Panel Fiyat ve Stokları Değiştirilsin mi?',
      message: `Mevcut ürünlerin panel fiyatı ve stoğu pazaryerindeki değerlerle değiştirilecek. Panel ana stoktur; bunu yalnız pazaryerindeki değerlerin doğru olduğundan eminseniz yapın.${stockNote}`
    }
  }
  if (updatePrices) {
    return {
      title: 'Panel Fiyatları Değiştirilsin mi?',
      message: 'Mevcut ürünlerin panel fiyatları pazaryerindeki fiyatlarla değiştirilecek. Bunu yalnız pazaryerindeki fiyatların doğru olduğundan eminseniz yapın.'
    }
  }
  return {
    title: 'Panel Stokları Değiştirilsin mi?',
    message: `Mevcut ürünlerin panel stoğu pazaryerindeki stokla değiştirilecek. Panel ana stoktur; bunu yalnız pazaryerindeki stokların doğru olduğundan eminseniz yapın.${stockNote}`
  }
}

export type ProductImportStartedInput = {
  connectionCount: number
  scope: 'single' | 'full' | 'new' | 'mapping' | 'existing'
  includeArchived: boolean
  includeDrafts: boolean
  includePendingApproval: boolean
  /** Whether the run touches the panel's existing product data at all; false for new-only and mapping runs. */
  contentChoice: boolean | null
  /** The price and stock choices, when the run offers them. */
  priceStock: { updatePrices: boolean; updateStock: boolean } | null
}

const importScopeWords: Record<ProductImportStartedInput['scope'], string> = {
  single: 'seçtiğiniz ürün', full: 'tüm ürünler', new: 'yeni ürünler', mapping: 'ürün eşlemeleri', existing: 'mevcut ürünlerin bilgileri'
}

function joinWords(parts: string[]) {
  return parts.length > 1 ? `${parts.slice(0, -1).join(', ')} ve ${parts[parts.length - 1]}` : parts[0] ?? ''
}

/** The body of the "Ürün Aktarımı Başladı" toast, written as sentences instead of a chain of fragments. */
export function productImportStartedMessage(input: ProductImportStartedInput): string {
  const included = [input.includeArchived ? (input.includeDrafts ? 'arşivdekiler ve taslaklar' : 'arşivdekiler') : '', input.includePendingApproval ? 'onay bekleyenler' : ''].filter(Boolean)
  const where = input.connectionCount === 1 ? 'Mağazadaki' : `${input.connectionCount} mağazadaki`
  const first = `${where} ${importScopeWords[input.scope]}${included.length ? `, ${joinWords(included)} dahil,` : ''} panele aktarılıyor.`
  const kept: string[] = []
  if (input.contentChoice !== null) kept.push(`Paneldeki mevcut ürün bilgileri ${input.contentChoice ? 'güncellenecek' : 'değişmeyecek'}.`)
  if (input.priceStock) kept.push(`Mevcut fiyatlar ${input.priceStock.updatePrices ? 'güncellenecek' : 'korunacak'}, mevcut stoklar ${input.priceStock.updateStock ? 'güncellenecek' : 'korunacak'}.`)
  return [first, ...kept, 'İlerlemeyi İşlem Takibi’nden izleyebilirsiniz.'].join(' ')
}
