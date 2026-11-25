import { ApiRequestError, clientTimeoutCode, type HubRequestInit } from '../../../shared/api'

// "Oluştur" sends one generate request per drawing under one Idempotency-Key (SizeChartDialog): a press after a dropped
// connection sends the same key, so the server replays the chart it drew, or answers that it is still drawing, instead
// of a second paid run; an answered error or a drawn chart renews the key. When the outcome is not known (the proxy's
// 502/503/504 without a problem body, a dropped connection, the server holding the key as unknown or in progress), the
// dialog asks the server for the newest drawn chart and shows it when this try drew one; otherwise it says so.
export const unknownSizeChartOutcomeMessage = 'Sunucudan yanıt alınamadı; beden tablosu oluşmadı ya da hâlâ hazırlanıyor. Biraz bekleyip Oluştur\'a yeniden basabilirsiniz; aynı deneme tamamlandıysa tablosu yeniden çizilmeden gösterilir.'

// A drawing takes 30 to 180 seconds, longer than a write's default limit (90 s): the panel waits for it. A press after
// the wait ran out sends the same key, so a chart the server finished meanwhile is shown, not drawn again.
export const sizeChartDrawTimeoutMs = 240_000
export const sizeChartTimeoutMessage = 'Çizim 4 dakika içinde yanıt vermedi; sonuç belirsiz. Birazdan tekrar bakın: tablo bu arada çizildiyse Oluştur\'a bastığınızda yeniden çizilmeden gösterilir.'

/** The generate request: the drawing's key and its own time limit. */
export function sizeChartGenerateInit(key: string, body: string, signal: AbortSignal): HubRequestInit {
  return { method: 'POST', headers: { 'Idempotency-Key': key }, body, signal, timeoutMs: sizeChartDrawTimeoutMs }
}

const unknownCodes = new Set(['IDEMPOTENCY_RESULT_UNKNOWN', 'IDEMPOTENCY_IN_PROGRESS', clientTimeoutCode])

export function isUnknownSizeChartOutcome(reason: unknown): boolean {
  if (reason instanceof ApiRequestError) return unknownCodes.has(reason.code ?? '') || (!reason.code && [502, 503, 504].includes(reason.status))
  return reason instanceof TypeError
}

export function sizeChartFailureMessage(reason: unknown): string {
  if (reason instanceof ApiRequestError && reason.code === clientTimeoutCode) return sizeChartTimeoutMessage
  if (isUnknownSizeChartOutcome(reason)) return unknownSizeChartOutcomeMessage
  return reason instanceof Error && reason.message ? reason.message : 'Beden tablosu oluşturulamadı.'
}

// The chart this try drew, read back after a lost answer: the newest generated picture, when it changed meanwhile.
export function chartDrawnMeanwhile(before: string | null | undefined, after: string | null | undefined): string | null {
  return after && after !== before ? after : null
}
