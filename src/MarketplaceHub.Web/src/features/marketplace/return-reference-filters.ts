export type ReturnReferenceFilterItem = {
  platformCode: string
  cargoProviderName: string | null
  reasonCode?: string | null
  reasonText: string | null
}

export function matchesReturnReferenceFilters(item: ReturnReferenceFilterItem, filters: { platforms: string[]; cargo: string; reason: string }) {
  const reason = item.reasonCode?.trim() || item.reasonText?.trim() || ''
  return (filters.platforms.length === 0 || filters.platforms.includes(item.platformCode))
    && (filters.cargo === 'ALL' || item.cargoProviderName?.trim() === filters.cargo)
    && (filters.reason === 'ALL' || reason === filters.reason)
}
