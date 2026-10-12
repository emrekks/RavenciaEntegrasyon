import { describe, expect, it } from 'vitest'
import { canApplyCatalogStructure, catalogStructureAttributeRoleLabel, catalogStructureAttributeStatus, catalogStructureCategoryNeedsAttention, catalogStructureDecisionLabel, catalogStructureDecisionTone, catalogStructureSummary, isCatalogStructureRunActive, supportsCatalogStructure, type CatalogStructureAttribute, type CatalogStructureReport, type CatalogStructureTotals } from './catalog-structure-report'

const totals: CatalogStructureTotals = { products: 120, productsSkipped: 0, productsWithoutCategory: 0, categories: 4, categoriesCreated: 2, categoriesReused: 1, categoriesSeparated: 1, categoriesAlreadyMapped: 0, categoriesNeedReview: 0, attributesCreated: 5, valuesCreated: 30, mappingsCreated: 40 }
const report = (overrides: Partial<CatalogStructureTotals> = {}): CatalogStructureReport => ({ mode: 'PREVIEW', platform: 'HEPSIBURADA', generatedAt: '2026-10-12T10:00:00Z', totals: { ...totals, ...overrides }, categories: [], review: [], warnings: [] })

describe('catalog structure import presentation', () => {
  it('supports only Trendyol and Hepsiburada', () => {
    expect(supportsCatalogStructure('TRENDYOL')).toBe(true)
    expect(supportsCatalogStructure('HEPSIBURADA')).toBe(true)
    expect(supportsCatalogStructure('SHOPIFY')).toBe(false)
    expect(supportsCatalogStructure(undefined)).toBe(false)
  })

  it('treats queued and running runs as active', () => {
    expect(isCatalogStructureRunActive({ status: 'QUEUED' })).toBe(true)
    expect(isCatalogStructureRunActive({ status: 'RUNNING' })).toBe(true)
    expect(isCatalogStructureRunActive({ status: 'COMPLETED' })).toBe(false)
    expect(isCatalogStructureRunActive(undefined)).toBe(false)
  })

  it('labels decisions in Turkish and flags review items as warnings', () => {
    expect(catalogStructureDecisionLabel('CreateSeparate')).toContain('Ayrı kategori')
    expect(catalogStructureDecisionLabel('NeedsReview')).toBe('İnceleme gerekli')
    expect(catalogStructureDecisionTone('NeedsReview')).toBe('warning')
    expect(catalogStructureDecisionTone('CreateNew')).toBe('success')
  })

  it('words the summary as future for a preview and past for an applied run', () => {
    expect(catalogStructureSummary(totals, 'PREVIEW').find(item => item.label.startsWith('Yeni kategori'))?.label).toBe('Yeni kategori oluşturulacak')
    expect(catalogStructureSummary(totals, 'APPLY').find(item => item.label.startsWith('Yeni kategori'))?.label).toBe('Yeni kategori oluşturuldu')
  })

  it('allows applying only a completed preview that would change something', () => {
    expect(canApplyCatalogStructure({ mode: 'PREVIEW', status: 'COMPLETED' }, report())).toBe(true)
    expect(canApplyCatalogStructure({ mode: 'PREVIEW', status: 'RUNNING' }, report())).toBe(false)
    expect(canApplyCatalogStructure({ mode: 'APPLY', status: 'COMPLETED' }, report())).toBe(false)
    expect(canApplyCatalogStructure({ mode: 'PREVIEW', status: 'COMPLETED' }, report({ categoriesCreated: 0, categoriesReused: 0, attributesCreated: 0, valuesCreated: 0, mappingsCreated: 0 }))).toBe(false)
    expect(canApplyCatalogStructure({ mode: 'PREVIEW', status: 'COMPLETED' }, null)).toBe(false)
  })
})

describe('catalog structure attribute presentation', () => {
  const attribute = (overrides: Partial<CatalogStructureAttribute> = {}): CatalogStructureAttribute => ({ remoteName: 'Renk', panelName: 'Renk', role: 'OPTION', createdDefinition: false, usedValueCount: 3, newValues: [], ...overrides })

  it('describes whether an attribute is new and how many values it adds', () => {
    expect(catalogStructureAttributeStatus(attribute({ createdDefinition: true, newValues: ['Pamuk', 'Keten'] }))).toBe('Yeni özellik · 2 yeni değer')
    expect(catalogStructureAttributeStatus(attribute({ newValues: ['Mavi'] }))).toBe('Mevcut özellik · 1 yeni değer')
    expect(catalogStructureAttributeStatus(attribute())).toBe('Mevcut özellik')
    expect(catalogStructureAttributeStatus({ remoteName: 'x', panelName: 'x', role: 'ATTRIBUTE', createdDefinition: false })).toBe('Mevcut özellik')
  })

  it('names the attribute role', () => {
    expect(catalogStructureAttributeRoleLabel('OPTION')).toBe('Ürün seçeneği')
    expect(catalogStructureAttributeRoleLabel('ATTRIBUTE')).toBe('Kategori özelliği')
  })

  it('opens only the categories that change something', () => {
    const base = { externalId: '1', remoteName: 'Bluz', remotePath: '', productCount: 3, decision: 'AlreadyMapped', panelName: 'Bluz', reason: '', createdCategory: false, newValues: 0, attributes: [attribute()] }
    expect(catalogStructureCategoryNeedsAttention(base)).toBe(false)
    expect(catalogStructureCategoryNeedsAttention({ ...base, createdCategory: true })).toBe(true)
    expect(catalogStructureCategoryNeedsAttention({ ...base, decision: 'NeedsReview' })).toBe(true)
    expect(catalogStructureCategoryNeedsAttention({ ...base, attributes: [attribute({ newValues: ['Mavi'] })] })).toBe(true)
  })
})
