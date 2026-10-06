import { describe, expect, it } from 'vitest'
import { ApiRequestError } from '../../shared/api'
import { publicationReadinessFailureDetail, readPublicationReadinessSource } from './publication-readiness-error'

describe('publication readiness source errors', () => {
  it('preserves the failed source, server message, HTTP status, and error code', async () => {
    const error = await readPublicationReadinessSource('Hepsiburada kategori özellik listesi', async () => {
      throw new ApiRequestError('Platform erişim izni reddetti.', 403, 'MARKETPLACE_FORBIDDEN')
    }).catch(reason => reason)

    expect(publicationReadinessFailureDetail('Hepsiburada', error)).toBe(
      'Hepsiburada zorunlu alan kontrolü tamamlanamadı. Kaynak: Hepsiburada kategori özellik listesi okunamadı. Platform erişim izni reddetti. (HTTP 403 · kod MARKETPLACE_FORBIDDEN)'
    )
  })

  it('reports a clear fallback when no diagnostic detail exists', () => {
    expect(publicationReadinessFailureDetail('Hepsiburada', null)).toBe(
      'Hepsiburada zorunlu alan kontrolü tamamlanamadı. Kaynak: Kontrol isteğinin hata ayrıntısı alınamadı.'
    )
  })
})
