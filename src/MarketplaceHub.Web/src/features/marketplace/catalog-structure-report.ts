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
  difference?: CatalogStructureDifference | null
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
  responseFields?: string[]
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
    case 'CreateNew': return 'Yeni kategori'
    case 'ReuseExisting': return 'Bağlanacak'
    case 'CreateSeparate': return 'Ayrı kategori'
    case 'AlreadyMapped': return 'Eşleşmiş'
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

// The new values themselves sit in the next column, so the status only says new or existing.
export function catalogStructureAttributeStatus(attribute: CatalogStructureAttribute) {
  return attribute.createdDefinition ? 'Yeni özellik' : 'Mevcut özellik'
}

// Open the categories that will change something; collapse the ones that are already in sync.
export function catalogStructureCategoryNeedsAttention(category: CatalogStructureCategory) {
  return category.createdCategory
    || category.decision === 'NeedsReview'
    || category.decision === 'CreateSeparate'
    || category.attributes.some(attribute => attribute.createdDefinition || catalogStructureNewValueCount(attribute) > 0)
}

export type CatalogStructureDifference = { comparedWith: string; comparedSource: string; sharedCount: number; onlyInMarketplace: string[]; onlyInPanel: string[] }

export type CatalogStructureFilter = 'ALL' | 'CHANGES' | 'NEW' | 'SEPARATE' | 'REVIEW' | 'SYNCED'

export function catalogStructureChangesSomething(category: CatalogStructureCategory) {
  // Reports from before per-attribute details only carry the category-level new value count.
  return (category.decision !== 'AlreadyMapped' && category.decision !== 'NeedsReview')
    || category.newValues > 0
    || category.attributes.some(attribute => attribute.createdDefinition || catalogStructureNewValueCount(attribute) > 0)
}

export function matchesCatalogStructureFilter(category: CatalogStructureCategory, filter: CatalogStructureFilter) {
  switch (filter) {
    case 'CHANGES': return catalogStructureChangesSomething(category)
    case 'NEW': return category.decision === 'CreateNew'
    case 'SEPARATE': return category.decision === 'CreateSeparate'
    case 'REVIEW': return category.decision === 'NeedsReview'
    case 'SYNCED': return !catalogStructureChangesSomething(category) && category.decision !== 'NeedsReview'
    default: return true
  }
}

export function catalogStructureFilterCounts(categories: CatalogStructureCategory[]) {
  const filters: CatalogStructureFilter[] = ['ALL', 'CHANGES', 'NEW', 'SEPARATE', 'REVIEW', 'SYNCED']
  return Object.fromEntries(filters.map(filter => [filter, categories.filter(category => matchesCatalogStructureFilter(category, filter)).length])) as Record<CatalogStructureFilter, number>
}

export function searchCatalogStructureCategories(categories: CatalogStructureCategory[], query: string) {
  const text = query.trim().toLocaleLowerCase('tr-TR')
  if (!text) return categories
  return categories.filter(category => [category.remoteName, category.panelName, category.remotePath, ...category.attributes.flatMap(attribute => [attribute.panelName, attribute.remoteName, ...(attribute.newValues ?? [])])]
    .some(part => part.toLocaleLowerCase('tr-TR').includes(text)))
}

// Plain-language reason shown under each category.
export function catalogStructureDecisionExplanation(category: CatalogStructureCategory, platformLabel: string) {
  const difference = category.difference
  switch (category.decision) {
    case 'CreateNew': return 'Panelde bu adda kategori olmadığı için yeni panel kategorisi açılacak.'
    case 'ReuseExisting': return 'Aynı adlı panel kategorisiyle özellikleri birebir aynı; mevcut kategoriye bağlanacak.'
    case 'AlreadyMapped': return 'Bu kategori daha önce eşlenmiş; yalnızca eksik özellik değerleri eklenecek.'
    case 'NeedsReview': return category.reason || 'Otomatik karar verilemedi; elle incelenmesi gerekiyor.'
    case 'CreateSeparate':
      return difference
        ? `${difference.comparedSource} tarafındaki “${difference.comparedWith}” ile ${difference.sharedCount} ortak özelliği var; özellik kümeleri farklı olduğu için ayrı kategori olarak açılacak.`
        : category.reason || `${platformLabel} kategorisi ayrı panel kategorisi olarak açılacak.`
    default: return category.reason
  }
}

// What pressing "Panele uygula" will do, as short sentences.
export function catalogStructureNarrative(report: CatalogStructureReport, platformLabel: string) {
  const { totals } = report
  const lines: string[] = []
  const brandNew = totals.categoriesCreated - totals.categoriesSeparated
  if (brandNew > 0) lines.push(`${brandNew} yeni panel kategorisi açılacak.`)
  if (totals.categoriesSeparated > 0) lines.push(`${totals.categoriesSeparated} kategori, özellik kümeleri panelde aynı adlı kategoriden farklı olduğu için “(${platformLabel})” adıyla ayrı açılacak.`)
  if (totals.categoriesReused > 0) lines.push(`${totals.categoriesReused} kategori, özellikleri aynı olduğu için mevcut panel kategorisine bağlanacak.`)
  if (totals.categoriesAlreadyMapped > 0) lines.push(`${totals.categoriesAlreadyMapped} kategori zaten eşleşmiş.`)
  if (totals.attributesCreated > 0) lines.push(`${totals.attributesCreated} yeni özellik tanımı oluşturulacak.`)
  if (totals.valuesCreated > 0) lines.push(`Mevcut özelliklere ${totals.valuesCreated.toLocaleString('tr-TR')} yeni değer eklenecek (yalnızca ürünlerinizde geçenler).`)
  if (totals.mappingsCreated > 0) lines.push(`${totals.mappingsCreated.toLocaleString('tr-TR')} eşleme kaydı yazılacak.`)
  if (totals.categoriesNeedReview > 0) lines.push(`${totals.categoriesNeedReview} kategori incelemeye bırakıldı, uygulamada atlanacak.`)
  if (lines.length === 0) lines.push('Panelde yapılacak bir değişiklik yok.')
  return lines
}

export function catalogStructureAttributeChanged(attribute: CatalogStructureAttribute) {
  return attribute.createdDefinition || catalogStructureNewValueCount(attribute) > 0
}

// The "Değişenler" view lists only what changes; a category that is itself new keeps all its
// attributes because every one of them gets linked. Every other view lists all attributes.
export function catalogStructureVisibleAttributes(category: CatalogStructureCategory, filter: CatalogStructureFilter) {
  if (filter !== 'CHANGES') return category.attributes
  if (category.decision === 'CreateNew' || category.decision === 'CreateSeparate' || category.decision === 'ReuseExisting') return category.attributes
  return category.attributes.filter(catalogStructureAttributeChanged)
}

export function catalogStructureRunStats(report: CatalogStructureReport) {
  const applied = report.mode === 'APPLY'
  return [
    { label: 'Kategori', value: report.totals.categories },
    { label: applied ? 'Eklenen özellik' : 'Yeni özellik', value: report.totals.attributesCreated },
    { label: applied ? 'Eklenen değer' : 'Yeni değer', value: report.totals.valuesCreated },
    { label: applied ? 'Yazılan eşleme' : 'Yeni eşleme', value: report.totals.mappingsCreated }
  ]
}

export function catalogStructureRunStatusLabel(run: Pick<CatalogStructureRun, 'mode' | 'status'>) {
  const noun = run.mode === 'APPLY' ? 'Aktarım' : 'Önizleme'
  switch (run.status) {
    case 'COMPLETED': return run.mode === 'APPLY' ? 'Aktarım tamamlandı' : 'Önizleme hazır'
    case 'FAILED': return `${noun} başarısız`
    case 'RUNNING': return `${noun} çalışıyor`
    default: return `${noun} sırada`
  }
}

export function formatCatalogStructureRunTime(value: string | null | undefined) {
  if (!value) return ''
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString('tr-TR', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })
}
