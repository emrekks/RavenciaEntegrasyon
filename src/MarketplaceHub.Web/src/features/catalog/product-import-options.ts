export type ProductImportMode = 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY' | 'OPTIONS_ONLY'
export type ProductImportMethod = 'BULK' | 'SINGLE'

export type ProductImportOptionVisibility = {
  showUpdateExisting: boolean
  showHepsiburadaReadOnlyNote: boolean
  showArchived: boolean
  showPendingApproval: boolean
  showOptionsSection: boolean
}

export function productImportOptionVisibility(options: {
  method: ProductImportMethod
  mode: ProductImportMode
  onlyHepsiburada: boolean
  hasHepsiburada: boolean
  supportsPendingApproval: boolean
}): ProductImportOptionVisibility {
  const isBulk = options.method === 'BULK'
  if (isBulk && options.mode === 'OPTIONS_ONLY') {
    return {
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: false,
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    }
  }
  // A single product (Tek Ürün Aktarımı) offers the same choices as an update of existing products (owner, 5 Nov 2026).
  const showUpdateExisting = (isBulk ? options.mode === 'EXISTING_ONLY' : true) && !options.onlyHepsiburada
  const showHepsiburadaReadOnlyNote = isBulk && options.hasHepsiburada && options.mode !== 'MAPPING_ONLY'
  // Every mode reads archived listings only when asked, Ürün Eşleme too (owner, 12 Nov 2026: "Arşiv ürünlerini dahil et
  // de olması lazım bunun gelişmiş seçeneklerinde"); the API no longer forces it on for mapping.
  const showArchived = true
  // The API reads listings awaiting approval only in Tam Katalog Taraması, Yeni Ürünleri Aktar and Ürün Eşleme
  // (MarketplaceSalesService.EnqueueProductSyncAsync drops it for Mevcut Ürünleri Güncelle), so it is offered there only.
  const showPendingApproval = isBulk && options.supportsPendingApproval && options.mode !== 'EXISTING_ONLY'

  const hasVisibleOptions = showUpdateExisting || showHepsiburadaReadOnlyNote || showArchived || showPendingApproval

  return {
    showUpdateExisting,
    showHepsiburadaReadOnlyNote,
    showArchived,
    showPendingApproval,
    showOptionsSection: hasVisibleOptions
  }
}

// The three choices of Mevcut Ürünleri Güncelle (and Tek Ürün Aktarımı), owner 12 Nov 2026: "Mevcut Ürün Bilgilerini
// Güncelle" takes every attribute and the model's new variants; without it, only the ticked prices and/or stock change.
export type ProductImportChoices = { updateExisting: boolean; updatePrices: boolean; updateStock: boolean }

// A Trendyol Mevcut Ürünleri Güncelle run with none ticked would change nothing; the start button waits for a choice
// (the API refuses it too: PRODUCT_SYNC_NOTHING_SELECTED).
export function productImportNeedsChoice(options: { method: ProductImportMethod; mode: ProductImportMode; hasTrendyol: boolean; showUpdateExisting: boolean } & ProductImportChoices): boolean {
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
export function productImportPriceStockLabel(updatePrices: boolean, updateStock: boolean): string {
  return ` · mevcut fiyatlar ${updatePrices ? 'güncellenecek' : 'korunacak'} · mevcut stoklar ${updateStock ? 'güncellenecek' : 'korunacak'}`
}

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
