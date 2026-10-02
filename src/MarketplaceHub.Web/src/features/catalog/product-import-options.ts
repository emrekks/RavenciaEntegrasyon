export type ProductImportMode = 'FULL' | 'NEW_ONLY' | 'EXISTING_ONLY' | 'MAPPING_ONLY'
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
  const showUpdateExisting = isBulk && (options.mode === 'FULL' || options.mode === 'EXISTING_ONLY') && !options.onlyHepsiburada
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
