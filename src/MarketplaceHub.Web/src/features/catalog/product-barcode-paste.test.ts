import { describe, expect, it } from 'vitest'
import { barcodeClipboardIssue, parseBarcodeClipboardValues } from './product-barcode-paste'

describe('parseBarcodeClipboardValues', () => {
  it('reads line-separated values and the first cell of tabular rows', () => {
    expect(parseBarcodeClipboardValues(' 8690001 \tProduct A\r\n8690002\tProduct B\r\n'))
      .toEqual(['8690001', '8690002'])
  })

  it('preserves interior empty rows so pasted data cannot shift silently', () => {
    expect(parseBarcodeClipboardValues('8690001\n\n8690003\n')).toEqual(['8690001', '', '8690003'])
  })

  it('returns no entries for an empty clipboard', () => {
    expect(parseBarcodeClipboardValues(' \r\n\t\r\n')).toEqual([])
  })
})

describe('barcodeClipboardIssue', () => {
  it('accepts an exact, unique list', () => {
    expect(barcodeClipboardIssue(['8690001', '8690002'], 2)).toBeNull()
  })

  it('rejects mismatched counts and blank cells', () => {
    expect(barcodeClipboardIssue(['8690001'], 2)).toContain('tam 2 satır')
    expect(barcodeClipboardIssue(['8690001', ''], 2)).toContain('boş barkod')
  })

  it('rejects duplicate incoming or retained barcodes without changing rows', () => {
    expect(barcodeClipboardIssue(['8690001', '8690001'], 2)).toContain('Tekrar eden barkod')
    expect(barcodeClipboardIssue(['8690002'], 1, ['8690002'])).toContain('Tekrar eden barkod')
  })
})
