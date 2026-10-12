import { describe, expect, it } from 'vitest'
import {
  catalogStructureDecisionExplanation,
  catalogStructureFilterCounts,
  catalogStructureNarrative,
  catalogStructureRunStats,
  catalogStructureRunStatusLabel,
  formatCatalogStructureRunTime,
  matchesCatalogStructureFilter,
  catalogStructureVisibleAttributes,
  searchCatalogStructureCategories,
  type CatalogStructureCategory,
  type CatalogStructureReport
} from './catalog-structure-report'

const category = (overrides: Partial<CatalogStructureCategory> = {}): CatalogStructureCategory => ({
  externalId: '1', remoteName: 'Bluz', remotePath: 'Kadın > Bluz', productCount: 10, decision: 'AlreadyMapped', panelName: 'Bluz', reason: '', createdCategory: false, newValues: 0, attributes: [], ...overrides
})

const all = [
  category({ externalId: 'a', decision: 'CreateNew', createdCategory: true, remoteName: 'Şort', panelName: 'Şort' }),
  category({ externalId: 'b', decision: 'CreateSeparate', createdCategory: true, remoteName: 'Elbise', panelName: 'Elbise (Hepsiburada)' }),
  category({ externalId: 'c', decision: 'NeedsReview', remoteName: 'Etek' }),
  category({ externalId: 'd', decision: 'AlreadyMapped', remoteName: 'Gömlek' }),
  category({ externalId: 'e', decision: 'AlreadyMapped', remoteName: 'Hırka', attributes: [{ remoteName: 'Renk', panelName: 'Renk', role: 'OPTION', createdDefinition: false, newValueCount: 2, newValues: ['Mavi', 'Yeşil'] }] })
]

const report = (totals: Partial<CatalogStructureReport['totals']>): CatalogStructureReport => ({
  mode: 'PREVIEW', platform: 'HEPSIBURADA', generatedAt: '2026-10-12T10:00:00Z', categories: [], review: [], warnings: [],
  totals: { products: 100, productsSkipped: 0, productsWithoutCategory: 0, categories: 5, categoriesCreated: 0, categoriesReused: 0, categoriesSeparated: 0, categoriesAlreadyMapped: 0, categoriesNeedReview: 0, attributesCreated: 0, valuesCreated: 0, mappingsCreated: 0, ...totals }
})

describe('catalog structure preview', () => {
  it('counts and filters categories by what they change', () => {
    const counts = catalogStructureFilterCounts(all)
    expect(counts).toMatchObject({ ALL: 5, NEW: 1, SEPARATE: 1, REVIEW: 1, CHANGES: 3, SYNCED: 1 })
    expect(all.filter(item => matchesCatalogStructureFilter(item, 'SYNCED')).map(item => item.externalId)).toEqual(['d'])
    expect(all.filter(item => matchesCatalogStructureFilter(item, 'CHANGES')).map(item => item.externalId)).toEqual(['a', 'b', 'e'])
  })

  it('counts category-level new values of older reports as a change', () => {
    const older = category({ externalId: 'o', decision: 'AlreadyMapped', newValues: 4, attributes: [] })
    expect(matchesCatalogStructureFilter(older, 'CHANGES')).toBe(true)
    expect(matchesCatalogStructureFilter(older, 'SYNCED')).toBe(false)
  })

  it('searches names, attributes and new values with Turkish casing', () => {
    expect(searchCatalogStructureCategories(all, 'ELBİSE').map(item => item.externalId)).toEqual(['b'])
    expect(searchCatalogStructureCategories(all, 'yeşil').map(item => item.externalId)).toEqual(['e'])
    expect(searchCatalogStructureCategories(all, '  ')).toHaveLength(5)
  })

  it('explains a separate category with the compared panel category', () => {
    const separate = { ...all[1], difference: { comparedWith: 'Elbise', comparedSource: 'Trendyol', sharedCount: 3, onlyInMarketplace: ['Kol Tipi'], onlyInPanel: [] } }
    expect(catalogStructureDecisionExplanation(separate, 'Hepsiburada')).toContain('“Elbise”')
    expect(catalogStructureDecisionExplanation(separate, 'Hepsiburada')).toContain('3 ortak özelliği')
    expect(catalogStructureDecisionExplanation(all[0], 'Hepsiburada')).toContain('yeni panel kategorisi')
    expect(catalogStructureDecisionExplanation(all[2], 'Hepsiburada')).toContain('incelenmesi')
  })

  it('summarises what applying will do', () => {
    const lines = catalogStructureNarrative(report({ categoriesCreated: 24, categoriesSeparated: 20, attributesCreated: 9, valuesCreated: 123, mappingsCreated: 821 }), 'Hepsiburada')
    expect(lines[0]).toBe('4 yeni panel kategorisi açılacak.')
    expect(lines.some(line => line.includes('(Hepsiburada)'))).toBe(true)
    expect(lines.some(line => line.includes('123 yeni değer'))).toBe(true)
    expect(catalogStructureNarrative(report({}), 'Trendyol')).toEqual(['Panelde yapılacak bir değişiklik yok.'])
  })
})

describe('visible attributes per filter', () => {
  const changed = { remoteName: 'Renk', panelName: 'Renk', role: 'OPTION', createdDefinition: false, newValueCount: 2, newValues: ['Mavi', 'Yeşil'] }
  const unchanged = { remoteName: 'Cep', panelName: 'Cep', role: 'ATTRIBUTE', createdDefinition: false, newValueCount: 0, newValues: [] }

  it('lists only what changes in the changes view and everything elsewhere', () => {
    const mapped = category({ decision: 'AlreadyMapped', attributes: [changed, unchanged] })
    expect(catalogStructureVisibleAttributes(mapped, 'CHANGES').map(item => item.panelName)).toEqual(['Renk'])
    expect(catalogStructureVisibleAttributes(mapped, 'ALL')).toHaveLength(2)
    expect(catalogStructureVisibleAttributes(mapped, 'SYNCED')).toHaveLength(2)
  })

  it('keeps every attribute of a category that is itself new', () => {
    expect(catalogStructureVisibleAttributes(category({ decision: 'CreateSeparate', attributes: [changed, unchanged] }), 'CHANGES')).toHaveLength(2)
    expect(catalogStructureVisibleAttributes(category({ decision: 'CreateNew', attributes: [unchanged] }), 'CHANGES')).toHaveLength(1)
  })
})

describe('launcher run summary', () => {
  it('summarises a finished run with preview or applied wording', () => {
    const preview = catalogStructureRunStats(report({ categories: 24, attributesCreated: 9, valuesCreated: 123, mappingsCreated: 821 }))
    expect(preview.map(item => item.label)).toEqual(['Kategori', 'Yeni özellik', 'Yeni değer', 'Yeni eşleme'])
    expect(preview.map(item => item.value)).toEqual([24, 9, 123, 821])
    expect(catalogStructureRunStats({ ...report({}), mode: 'APPLY' })[2].label).toBe('Eklenen değer')
  })

  it('words run statuses and formats times', () => {
    expect(catalogStructureRunStatusLabel({ mode: 'PREVIEW', status: 'COMPLETED' })).toBe('Önizleme hazır')
    expect(catalogStructureRunStatusLabel({ mode: 'APPLY', status: 'COMPLETED' })).toBe('Aktarım tamamlandı')
    expect(catalogStructureRunStatusLabel({ mode: 'PREVIEW', status: 'RUNNING' })).toBe('Önizleme çalışıyor')
    expect(catalogStructureRunStatusLabel({ mode: 'APPLY', status: 'FAILED' })).toBe('Aktarım başarısız')
    expect(formatCatalogStructureRunTime(null)).toBe('')
    expect(formatCatalogStructureRunTime('not a date')).toBe('')
    expect(formatCatalogStructureRunTime('2026-10-12T17:34:00Z')).toContain('Ekim')
  })
})
