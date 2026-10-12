export type CatalogStructureTotals = {
  products: number
  productsSkipped: number
  productsWithoutCategory: number
  categories: number
  categoriesCreated: number
  categoriesReused: number
  categoriesSeparated: number
  categoriesAlreadyMapped: number
  categoriesNeedReview: number
  attributesCreated: number
  valuesCreated: number
  mappingsCreated: number
}

export type CatalogStructureAttribute = { remoteName: string; panelName: string; role: string; createdDefinition: boolean; usedValueCount?: number; newValueCount?: number; newValues?: string[] }

export type CatalogStructureCategory = {
  externalId: string
  remoteName: string
  remotePath: string
  productCount: number
  decision: string
  panelName: string
  reason: string
  createdCategory: boolean
  newValues: number
  unmatchedFields?: string[]
  attributes: CatalogStructureAttribute[]
}

export type CatalogStructureReport = {
  mode: 'PREVIEW' | 'APPLY'
  platform: string
  generatedAt: string
  totals: CatalogStructureTotals
  categories: CatalogStructureCategory[]
  review: { kind: string; subject: string; reason: string }[]
  warnings: string[]
}

export type CatalogStructureRun = {
  id: string
  connectionId: string
  jobId: string | null
  mode: 'PREVIEW' | 'APPLY'
  status: 'QUEUED' | 'RUNNING' | 'COMPLETED' | 'FAILED'
  createdAt: string
  completedAt: string | null
  errorSummary: string | null
  report?: CatalogStructureReport | null
}

export const structureSupportedPlatforms = ['TRENDYOL', 'HEPSIBURADA'] as const

export function supportsCatalogStructure(platformCode: string | null | undefined) {
  return structureSupportedPlatforms.some(code => code === platformCode)
}

export function isCatalogStructureRunActive(run: Pick<CatalogStructureRun, 'status'> | null | undefined) {
  return run?.status === 'QUEUED' || run?.status === 'RUNNING'
}

export function catalogStructureDecisionLabel(decision: string) {
  switch (decision) {
    case 'CreateNew': return 'Yeni panel kategorisi'
    case 'ReuseExisting': return 'Mevcut kategoriye bağlanacak'
    case 'CreateSeparate': return 'Ayrı kategori (özellikler farklı)'
    case 'AlreadyMapped': return 'Zaten eşli'
    case 'NeedsReview': return 'İnceleme gerekli'
    default: return decision
  }
}

export function catalogStructureDecisionTone(decision: string): 'success' | 'info' | 'warning' | 'neutral' {
  if (decision === 'CreateNew' || decision === 'ReuseExisting') return 'success'
  if (decision === 'CreateSeparate') return 'info'
  if (decision === 'NeedsReview') return 'warning'
  return 'neutral'
}

export function catalogStructureSummary(totals: CatalogStructureTotals, mode: 'PREVIEW' | 'APPLY') {
  const verb = mode === 'APPLY' ? 'oluşturuldu' : 'oluşturulacak'
  return [
    { label: 'Taranan ürün', value: totals.products },
    { label: 'Kategori', value: totals.categories },
    { label: `Yeni kategori ${verb}`, value: totals.categoriesCreated },
    { label: 'Mevcut kategoriye bağlanan', value: totals.categoriesReused },
    { label: 'Ayrı tutulan', value: totals.categoriesSeparated },
    { label: 'İnceleme gereken', value: totals.categoriesNeedReview },
    { label: `Özellik ${verb}`, value: totals.attributesCreated },
    { label: `Değer ${verb}`, value: totals.valuesCreated },
    { label: `Eşleme ${verb}`, value: totals.mappingsCreated }
  ]
}

export function canApplyCatalogStructure(run: Pick<CatalogStructureRun, 'mode' | 'status'> | null | undefined, report: CatalogStructureReport | null | undefined) {
  if (!run || run.mode !== 'PREVIEW' || run.status !== 'COMPLETED' || !report) return false
  const writes = report.totals.categoriesCreated + report.totals.categoriesReused + report.totals.attributesCreated + report.totals.valuesCreated + report.totals.mappingsCreated
  return writes > 0
}

export function catalogStructureAttributeRoleLabel(role: string) {
  return role === 'OPTION' ? 'Ürün seçeneği' : 'Kategori özelliği'
}

export function catalogStructureNewValueCount(attribute: CatalogStructureAttribute) {
  return attribute.newValueCount ?? attribute.newValues?.length ?? 0
}

export function catalogStructureAttributeStatus(attribute: CatalogStructureAttribute) {
  const newValues = catalogStructureNewValueCount(attribute)
  if (attribute.createdDefinition) return `Yeni özellik${newValues > 0 ? ` · ${newValues} yeni değer` : ''}`
  return newValues > 0 ? `Mevcut özellik · ${newValues} yeni değer` : 'Mevcut özellik · yeni değer yok'
}

// Open the categories that will change something; collapse the ones that are already in sync.
export function catalogStructureCategoryNeedsAttention(category: CatalogStructureCategory) {
  return category.createdCategory
    || category.decision === 'NeedsReview'
    || category.decision === 'CreateSeparate'
    || category.attributes.some(attribute => attribute.createdDefinition || catalogStructureNewValueCount(attribute) > 0)
}
