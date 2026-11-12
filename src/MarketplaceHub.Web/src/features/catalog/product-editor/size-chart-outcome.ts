import { ApiRequestError } from '../../../shared/api'

// "Oluştur" sends one generate request with a fresh Idempotency-Key per press. When its answer is lost (the proxy's
// 502/503/504 without a problem body, a dropped connection, or the server holding that key as unknown/in progress), the
// outcome is not known: the dialog asks the server for the newest drawn chart and shows it when this try drew one;
// otherwise it says so in plain words and the next press is a new request (never the same key again).
export const unknownSizeChartOutcomeMessage = 'Sunucudan yanıt alınamadı; beden tablosu oluşmadı ya da hâlâ hazırlanıyor. Biraz bekleyip Oluştur\'a yeniden basabilirsiniz; yeni deneme ayrı bir istek olarak çalışır.'

const unknownCodes = new Set(['IDEMPOTENCY_RESULT_UNKNOWN', 'IDEMPOTENCY_IN_PROGRESS'])

export function isUnknownSizeChartOutcome(reason: unknown): boolean {
  if (reason instanceof ApiRequestError) return unknownCodes.has(reason.code ?? '') || (!reason.code && [502, 503, 504].includes(reason.status))
  return reason instanceof TypeError
}

export function sizeChartFailureMessage(reason: unknown): string {
  if (isUnknownSizeChartOutcome(reason)) return unknownSizeChartOutcomeMessage
  return reason instanceof Error && reason.message ? reason.message : 'Beden tablosu oluşturulamadı.'
}

// The chart this try drew, read back after a lost answer: the newest generated picture, when it changed meanwhile.
export function chartDrawnMeanwhile(before: string | null | undefined, after: string | null | undefined): string | null {
  return after && after !== before ? after : null
}
