import { ApiRequestError } from '../../shared/api'

export class PublicationReadinessSourceError extends Error {
  readonly source: string
  readonly apiCode?: string
  readonly apiStatus?: number
  readonly failureKind: 'permission' | 'rate-limit' | 'remote' | 'network' | 'invalid-response' | 'unknown'

  constructor(source: string, cause: unknown) {
    const failureKind = cause instanceof ApiRequestError
      ? cause.status === 401 || cause.status === 403
        ? 'permission'
        : cause.status === 429
          ? 'rate-limit'
          : cause.status >= 500
            ? 'remote'
            : 'unknown'
      : cause instanceof TypeError && /fetch/i.test(cause.message)
        ? 'network'
        : cause instanceof Error && /json|response/i.test(cause.message)
          ? 'invalid-response'
          : 'unknown'
    const detail = cause instanceof ApiRequestError
      ? `${cause.message} (HTTP ${cause.status}${cause.code ? ` · kod ${cause.code}` : ''})`
      : cause instanceof TypeError && /fetch/i.test(cause.message)
        ? 'Ağ isteği yanıt vermedi; sunucuya erişim veya bağlantı kontrol edilmeli.'
        : cause instanceof Error && cause.message.trim()
          ? cause.message.trim()
          : 'Yanıtta ayrıntı bulunmayan bir hata oluştu.'
    super(`${source} okunamadı. ${detail}`)
    this.name = 'PublicationReadinessSourceError'
    this.source = source
    this.apiCode = cause instanceof ApiRequestError ? cause.code : undefined
    this.apiStatus = cause instanceof ApiRequestError ? cause.status : undefined
    this.failureKind = failureKind
  }
}

export async function readPublicationReadinessSource<T>(source: string, load: () => Promise<T>): Promise<T> {
  try {
    return await load()
  } catch (cause) {
    throw new PublicationReadinessSourceError(source, cause)
  }
}

export function publicationReadinessFailureDetail(platformName: string, error: unknown) {
  const source = error instanceof PublicationReadinessSourceError ? error.source : ''
  const sourceLabel = /değer|özellik/i.test(source) ? 'özellik bilgisi' : /kategori/i.test(source) ? 'kategori bilgisi' : 'gerekli bilgi'
  const failureKind = error instanceof PublicationReadinessSourceError ? error.failureKind : 'unknown'
  const nextStep = failureKind === 'permission'
    ? 'Entegrasyon erişim iznini kontrol edip bağlantıyı yenileyin.'
    : failureKind === 'rate-limit'
      ? 'Biraz bekleyip yeniden deneyin.'
      : failureKind === 'remote'
        ? 'Pazaryeri yanıt vermedi. Bağlantıyı yenileyip tekrar deneyin.'
        : failureKind === 'network'
          ? 'Sunucuya erişilemiyor. Bağlantıyı kontrol edip tekrar deneyin.'
          : failureKind === 'invalid-response'
            ? 'Pazaryerinden eksik ya da bozuk bilgi geldi. Bağlantıyı yenileyip tekrar deneyin.'
            : 'Bilgi alınamadı. Eşleştirmeleri kontrol edip tekrar deneyin.'
  return `${platformName} ${sourceLabel} alınamadı. ${nextStep}`
}
