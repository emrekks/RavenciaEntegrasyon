import { ApiRequestError } from '../../shared/api'

export class PublicationReadinessSourceError extends Error {
  readonly apiCode?: string

  constructor(source: string, cause: unknown) {
    const detail = cause instanceof ApiRequestError
      ? `${cause.message} (HTTP ${cause.status}${cause.code ? ` · kod ${cause.code}` : ''})`
      : cause instanceof TypeError && /fetch/i.test(cause.message)
        ? 'Ağ isteği yanıt vermedi; sunucuya erişim veya bağlantı kontrol edilmeli.'
        : cause instanceof Error && cause.message.trim()
          ? cause.message.trim()
          : 'Yanıtta ayrıntı bulunmayan bir hata oluştu.'
    super(`${source} okunamadı. ${detail}`)
    this.name = 'PublicationReadinessSourceError'
    this.apiCode = cause instanceof ApiRequestError ? cause.code : undefined
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
  const sourceDetail = error instanceof Error && error.message.trim()
    ? error.message.trim()
    : 'Kontrol isteğinin hata ayrıntısı alınamadı.'
  return `${platformName} zorunlu alan kontrolü tamamlanamadı. Kaynak: ${sourceDetail}`
}
