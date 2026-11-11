import { describe, expect, it } from 'vitest'
import { describePlatformCodeIssues, platformCodeIssues, summarizeVariantCodeIssues, variantCodeDuplicates, variantCodeFieldIssueText, variantCodeFieldKey, variantCodeMatchKey } from './platform-code-rules'

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
      heldRows: 0,
      duplicateRows: 0,
      duplicateSkuRows: 0,
      rulePlatforms: ['Trendyol', 'Shopify'],
      title: '2 varyantta barkod eksik · 1 varyantta stok kodu eksik · 1 varyantın kodu platform kurallarına uymuyor',
    })
  })

  it('says only what is missing when no rule is broken, and nothing when all is well', () => {
    const rows = [{ key: 'a', barcode: '', sku: '' }]
    expect(summarizeVariantCodeIssues(rows, issuesFor(rows))).toMatchObject({ rulePlatforms: [], title: '1 varyantta barkod eksik · 1 varyantta stok kodu eksik' })
    expect(summarizeVariantCodeIssues([{ key: 'a', barcode: '', sku: 'X' }], new Map())).toBeNull()
  })

  it('names the card holding a code a refused save reported, and counts such rows apart', () => {
    const rows = [{ key: 'a', barcode: 'MZ021Y-CD-01', sku: 'MZ021Y-CD-01' }]
    expect(variantCodeFieldIssueText([], 'sku', 'MZ021Y-CD-01', 'İndigo Bluz')).toBe("MZ021Y-CD-01 · 'İndigo Bluz' ürününde kullanılıyor")
    expect(summarizeVariantCodeIssues(rows, new Map(), (field, code) => field === 'sku' && code === 'MZ021Y-CD-01')).toMatchObject({ heldRows: 1, title: '1 varyantın kodu başka üründe kullanılıyor' })
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

// Owner, 11.11.2026 (RYP003T): "aynı diye kırmızı yanmadı?" — a code two rows share turns both red as it is typed.
describe('codes two variant rows share', () => {
  it('reads codes as the server does: case, spaces and accents aside', () => {
    expect(variantCodeMatchKey(' ryp003t24 ')).toBe('RYP003T24')
    expect(variantCodeMatchKey('ryp003t 24')).toBe(variantCodeMatchKey('RYP003T-24'))
    expect(variantCodeMatchKey('Kırmızı-01')).toBe(variantCodeMatchKey('KIRMIZI 01'))
    expect(variantCodeMatchKey('RYP003T24')).not.toBe(variantCodeMatchKey('RYP003T-24'))
  })

  it('marks both rows holding the same barcode or the same stock code, each naming the other', () => {
    const rows = [
      { key: 'siyah', label: '2XL - Siyah', barcode: 'RYP003T24', sku: 'RYP003T24' },
      { key: 'murdum', label: '2XL - Mürdüm', barcode: 'ryp003t24 ', sku: 'RYP003T25' },
      { key: 'haki', label: '2XL - Haki', barcode: 'RYP003T26', sku: ' rYp003T25' },
      { key: 'empty', label: 'L - Haki', barcode: '', sku: '' },
      { key: 'empty2', label: 'XL - Haki', barcode: '', sku: '' },
    ]
    const duplicates = variantCodeDuplicates(rows)
    expect(duplicates.get(variantCodeFieldKey('murdum', 'barcode'))).toBe('2XL - Siyah')
    expect(duplicates.get(variantCodeFieldKey('siyah', 'barcode'))).toBe('2XL - Mürdüm')
    expect(duplicates.get(variantCodeFieldKey('haki', 'sku'))).toBe('2XL - Mürdüm')
    expect(duplicates.has(variantCodeFieldKey('siyah', 'sku'))).toBe(false)
    expect(duplicates.has(variantCodeFieldKey('empty', 'barcode'))).toBe(false)
    expect(variantCodeFieldIssueText([], 'barcode', 'RYP003T24', undefined, duplicates.get(variantCodeFieldKey('murdum', 'barcode'))))
      .toBe('RYP003T24 barkodu 2XL - Siyah varyantında da kullanılıyor')

    const summary = summarizeVariantCodeIssues(rows.slice(0, 3), new Map(), () => false, (rowKey, field) => duplicates.has(variantCodeFieldKey(rowKey, field)))
    expect(summary).toMatchObject({ duplicateRows: 3, duplicateSkuRows: 2, title: '3 varyantta yinelenen barkod/stok kodu' })
  })
})
