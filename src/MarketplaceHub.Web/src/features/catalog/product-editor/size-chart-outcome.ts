import { ApiRequestError } from '../../../shared/api'

// "Oluştur" sends one generate request per drawing under one Idempotency-Key (SizeChartDialog): a press after a dropped
// connection sends the same key, so the server replays the chart it drew, or answers that it is still drawing, instead
// of a second paid run; an answered error or a drawn chart renews the key. When the outcome is not known (the proxy's
// 502/503/504 without a problem body, a dropped connection, the server holding the key as unknown or in progress), the
// dialog asks the server for the newest drawn chart and shows it when this try drew one; otherwise it says so.
export const unknownSizeChartOutcomeMessage = 'Sunucudan yanıt alınamadı; beden tablosu oluşmadı ya da hâlâ hazırlanıyor. Biraz bekleyip Oluştur\'a yeniden basabilirsiniz; aynı deneme tamamlandıysa tablosu yeniden çizilmeden gösterilir.'

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
