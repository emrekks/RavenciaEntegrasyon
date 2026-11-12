import { describe, expect, it } from 'vitest'
import { ApiRequestError } from '../../../shared/api'
import { chartDrawnMeanwhile, isUnknownSizeChartOutcome, sizeChartFailureMessage, unknownSizeChartOutcomeMessage } from './size-chart-outcome'

describe('a size chart try whose answer was lost', () => {
  it('never shows the cryptic idempotency text: the key held as unknown or running reads as a plain retry hint', () => {
    for (const code of ['IDEMPOTENCY_RESULT_UNKNOWN', 'IDEMPOTENCY_IN_PROGRESS']) {
      const reason = new ApiRequestError('İşlemin sonucu kesinleşmedi; aynı anahtarla körlemesine tekrar çalıştırılamaz.', 409, code)
      expect(isUnknownSizeChartOutcome(reason)).toBe(true)
      expect(sizeChartFailureMessage(reason)).toBe(unknownSizeChartOutcomeMessage)
    }
  })

  it('treats the proxy\'s bare 502/503/504 and a dropped connection as unknown', () => {
    expect(isUnknownSizeChartOutcome(new ApiRequestError('İşlem tamamlanamadı (502).', 502))).toBe(true)
    expect(isUnknownSizeChartOutcome(new ApiRequestError('İşlem tamamlanamadı (504).', 504))).toBe(true)
    expect(isUnknownSizeChartOutcome(new TypeError('Failed to fetch'))).toBe(true)
  })

  it('shows the server\'s own Turkish reason when it answered (nothing was added)', () => {
    const reason = new ApiRequestError('Claude görsel dosyası üretmedi; görsel eklenmedi.', 502, 'AI_NO_IMAGE')
    expect(isUnknownSizeChartOutcome(reason)).toBe(false)
    expect(sizeChartFailureMessage(reason)).toBe('Claude görsel dosyası üretmedi; görsel eklenmedi.')
    expect(sizeChartFailureMessage(new ApiRequestError('Ölçüler geçersiz.', 422, 'SIZE_CHART_INVALID'))).toBe('Ölçüler geçersiz.')
  })

  it('shows the chart this try drew when the newest generated picture changed meanwhile', () => {
    expect(chartDrawnMeanwhile('/a.png', '/b.png')).toBe('/b.png')
    expect(chartDrawnMeanwhile(null, '/b.png')).toBe('/b.png')
    expect(chartDrawnMeanwhile('/a.png', '/a.png')).toBeNull()
    expect(chartDrawnMeanwhile('/a.png', null)).toBeNull()
  })
})
