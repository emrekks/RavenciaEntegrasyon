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
  if (options.mode === 'OPTIONS_ONLY') {
    return {
      showUpdateExisting: false,
      showHepsiburadaReadOnlyNote: false,
      showArchived: false,
      showPendingApproval: false,
      showOptionsSection: false
    }
  }
  const showUpdateExisting = isBulk && options.mode === 'EXISTING_ONLY' && !options.onlyHepsiburada
  const showHepsiburadaReadOnlyNote = isBulk && options.hasHepsiburada && options.mode !== 'MAPPING_ONLY'
  const showArchived = isBulk && options.mode !== 'MAPPING_ONLY'
  const showPendingApproval = isBulk && options.supportsPendingApproval

  const hasVisibleOptions = showHepsiburadaReadOnlyNote || showArchived || showPendingApproval

  return {
    showUpdateExisting,
    showHepsiburadaReadOnlyNote,
    showArchived,
    showPendingApproval,
    showOptionsSection: hasVisibleOptions
  }
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
