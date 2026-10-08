import { describe, expect, it } from 'vitest'
import { ApiRequestError } from '../../shared/api'
import { PublicationReadinessSourceError, publicationReadinessFailureDetail } from './publication-readiness-error'

describe('publication readiness errors', () => {
  it('replaces raw JSON parser details with a simple explanation and next step', () => {
    const error = new PublicationReadinessSourceError(
      'Hepsiburada kategori özellik listesi',
      new TypeError("Failed to execute 'json' on 'Response': Unexpected end of JSON input")
    )

    const detail = publicationReadinessFailureDetail('Hepsiburada', error)
    expect(detail).toContain('Hepsiburada özellik bilgisi alınamadı.')
    expect(detail).toContain('eksik ya da bozuk bilgi geldi')
    expect(detail).not.toContain('Unexpected end of JSON input')
    expect(detail).not.toContain('HTTP')
  })

  it('gives a direct remedy when marketplace access is denied', () => {
    const error = new PublicationReadinessSourceError(
      'Yerel kategori özellik eşlemeleri',
      new ApiRequestError('raw upstream details', 403, 'REFERENCE_ACCESS_DENIED')
    )

    expect(publicationReadinessFailureDetail('Trendyol', error)).toBe(
      'Trendyol özellik bilgisi alınamadı. Entegrasyon erişim iznini kontrol edip bağlantıyı yenileyin.'
    )
  })
})
