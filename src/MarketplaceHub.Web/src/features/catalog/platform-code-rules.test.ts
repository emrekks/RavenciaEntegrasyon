import { describe, expect, it } from 'vitest'
import { describePlatformCodeIssues, isValidEan13, platformCodeIssues } from './platform-code-rules'

describe('platform code rules', () => {
  it('checks the EAN-13 check digit', () => {
    expect(isValidEan13('8690000000012')).toBe(true)
    expect(isValidEan13('8690000000011')).toBe(false)
    expect(isValidEan13('869000000001')).toBe(false)
    expect(isValidEan13('869000000001A')).toBe(false)
  })

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
