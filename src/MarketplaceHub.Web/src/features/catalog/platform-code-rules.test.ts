import { describe, expect, it } from 'vitest'
import { describePlatformCodeIssues, platformCodeIssues, summarizeVariantCodeIssues, variantCodeFieldIssueText } from './platform-code-rules'

describe('variant code callout', () => {
  const platforms = ['TRENDYOL', 'SHOPIFY']
  const issuesFor = (rows: { key: string; barcode: string; sku: string }[]) => new Map(rows.map(row => [row.key, platformCodeIssues(platforms, row.barcode, row.sku)]))

  it('counts missing codes apart from broken rules and names platforms only for the rules', () => {
    const rows = [
      { key: 'a', barcode: '', sku: '' },
      { key: 'b', barcode: '', sku: 'RYP-02' },
      { key: 'c', barcode: 'BAD CODE', sku: 'RYP-03' },
      { key: 'd', barcode: 'RYP-04', sku: 'RYP-04' },
    ]
    expect(summarizeVariantCodeIssues(rows, issuesFor(rows))).toEqual({
      missingBarcodes: 2,
      missingSkus: 1,
      ruleRows: 1,
      rulePlatforms: ['Trendyol', 'Shopify'],
      title: '2 varyantta barkod eksik · 1 varyantta stok kodu eksik · 1 varyantın kodu platform kurallarına uymuyor',
    })
  })

  it('says only what is missing when no rule is broken, and nothing when all is well', () => {
    const rows = [{ key: 'a', barcode: '', sku: '' }]
    expect(summarizeVariantCodeIssues(rows, issuesFor(rows))).toMatchObject({ rulePlatforms: [], title: '1 varyantta barkod eksik · 1 varyantta stok kodu eksik' })
    expect(summarizeVariantCodeIssues([{ key: 'a', barcode: '', sku: 'X' }], new Map())).toBeNull()
  })

  it('words a red field as missing when empty and as the rule when filled', () => {
    const empty = platformCodeIssues(platforms, '', '')
    expect(variantCodeFieldIssueText(empty, 'barcode', '')).toBe('Barkod eksik')
    expect(variantCodeFieldIssueText(empty, 'sku', '')).toBe('Stok kodu eksik')
    expect(variantCodeFieldIssueText([], 'sku', ' ')).toBe('Stok kodu eksik')
    expect(variantCodeFieldIssueText([], 'barcode', '')).toBe('')
    expect(variantCodeFieldIssueText(platformCodeIssues(['TRENDYOL'], 'BAD CODE', 'X'), 'barcode', 'BAD CODE')).toContain('Trendyol:')
  })
})

describe('platform code rules', () => {
  it('lets a valid EAN-13 and a stock code through every platform', () => {
    expect(platformCodeIssues(['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY'], '8690000000012', 'KZK-001-S')).toEqual([])
  })

  it('names the platforms a barcode or stock code fails', () => {
    const issues = platformCodeIssues(['TRENDYOL', 'HEPSIBURADA', 'N11'], 'KZK 001', '')
    expect(issues.map(issue => `${issue.platformCode}:${issue.field}`)).toEqual(['TRENDYOL:barcode', 'TRENDYOL:sku', 'HEPSIBURADA:barcode', 'HEPSIBURADA:sku', 'N11:sku'])
    expect(describePlatformCodeIssues(issues, 'barcode')).toBe('Trendyol: barkod en fazla 40 karakter; yalnız harf, rakam, nokta, tire, alt çizgi · Hepsiburada: barkod en fazla 40 karakter; yalnız harf, rakam, nokta, tire, alt çizgi')
  })

  it('leaves the barcode optional where the platform does', () => {
    expect(platformCodeIssues(['N11', 'SHOPIFY'], '', 'KZK-001-S')).toEqual([])
    expect(platformCodeIssues(['PAZARAMA', 'PTTAVM'], '', 'KZK-001-S').map(issue => issue.platformCode)).toEqual(['PAZARAMA', 'PTTAVM'])
  })

  it('takes a model-code barcode for Hepsiburada as for Trendyol', () => {
    expect(platformCodeIssues(['TRENDYOL', 'HEPSIBURADA'], 'RYP004-01', 'RYP004-13')).toEqual([])
  })

  it('checks nothing for a platform it has no rules for', () => {
    expect(platformCodeIssues(['AMAZON'], '', '')).toEqual([])
  })
})
