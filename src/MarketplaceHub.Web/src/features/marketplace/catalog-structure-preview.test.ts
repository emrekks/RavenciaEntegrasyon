import { describe, expect, it } from 'vitest'
import {
  catalogStructureDecisionExplanation,
  catalogStructureFilterCounts,
  catalogStructureNarrative,
  matchesCatalogStructureFilter,
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
