import { formatDateTime } from '../../shared/format-date'
import { relativeAgeText } from '../marketplace/integrations/integration-helpers'

// "API Sağlığı": the six signals per connection as one cell each (a short value, a tone, the detail as a tooltip).
// Every figure comes from the API as stored; a signal the data cannot answer says "Ölçülmüyor" and why.

export type SignalAuth = { state: 'valid' | 'rejected' | 'unknown'; lastRejectedAt: string | null; lastRejectedCode: string | null; lastOkAt: string | null }
export type SignalLag = { stream: string; lagSeconds: number; expectedSeconds: number; level: 'ok' | 'warn' | 'fault' }
export type SignalErrorRate = { attempts: number; failed: number; remoteSide: number; ourSide: number; rateLimited: number; workflowWait: number }
export type SignalRateLimit = { refused24h: number; waitingJobs: number; pausedUntil: string | null; lastRunRefusals: number }
export type SignalQueue = { ageSeconds: number; jobType: string; pending: number }
export type SignalWebhook = { used: boolean; lastReceivedAt: string | null; healthy: boolean; lastOrderReadAt: string | null; fallbackReadSeconds: number | null }
export type ConnectionSignals = {
  connectionId: string
  platformCode: string
  displayName: string
  auth: SignalAuth
  lag: SignalLag | null
  errorRate: SignalErrorRate
  rateLimit: SignalRateLimit
  queue: SignalQueue | null
  webhook: SignalWebhook
}
export type ApiHealthSignals = { generatedAt: string; connections: ConnectionSignals[] }

export type SignalTone = 'ok' | 'warn' | 'bad' | 'off'
export type SignalCell = { text: string; tone: SignalTone; title: string }

const STREAM_NAMES: Record<string, string> = { ORDERS: 'Sipariş', RETURNS: 'İade', QUESTIONS: 'Soru' }

export function durationText(seconds: number): string {
  if (seconds < 60) return `${Math.max(0, Math.round(seconds))} sn`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} dk`
  if (seconds < 48 * 3600) return `${Math.floor(seconds / 3600)} sa`
  return `${Math.floor(seconds / 86400)} gün`
}

function everyText(seconds: number): string {
  return seconds % 60 === 0 ? `${seconds / 60} dk'da bir` : `${seconds} sn'de bir`
}

const exact = (value: string | null) => (value ? `${relativeAgeText(value) ?? ''} (${formatDateTime(value)})`.trim() : 'kayıt yok')

export function authCell(auth: SignalAuth, now = Date.now()): SignalCell {
  if (auth.state === 'rejected') {
    const when = relativeAgeText(auth.lastRejectedAt, now)
    return { text: when ? `${when.replace(' önce', '')} önce reddedildi` : 'Reddedildi', tone: 'bad', title: `Pazaryeri anahtarı reddetti (401/403, kod ${auth.lastRejectedCode ?? 'bilinmiyor'}); ardından başarılı bir istek yok. Son reddedilme: ${exact(auth.lastRejectedAt)}. Son başarılı istek: ${exact(auth.lastOkAt)}.` }
  }
  if (auth.state === 'valid') return { text: 'Geçerli', tone: 'ok', title: `Son başarılı istek: ${exact(auth.lastOkAt)}.${auth.lastRejectedAt ? ` Son 30 günde son reddedilme: ${exact(auth.lastRejectedAt)} (sonra düzeldi).` : ' Son 30 günde anahtar reddi yok.'} E-Faturam oturumu bu görünümde izlenmiyor.` }
  return { text: 'Bilinmiyor', tone: 'off', title: 'Son 30 günde bu bağlantı için ne başarılı ne reddedilen bir istek kaydı var.' }
}

export function lagCell(lag: SignalLag | null): SignalCell {
  if (!lag) return { text: '—', tone: 'off', title: 'Sipariş, iade ve soru okumalarının hiçbiri bu bağlantıda açık değil ya da henüz başarılı okuma kaydı yok.' }
  const stream = STREAM_NAMES[lag.stream] ?? lag.stream
  const detail = `${stream} okuması ${durationText(lag.lagSeconds)} önce başarılı oldu; zamanlama ${everyText(lag.expectedSeconds)}. Uyarı 3 katından, arıza 10 katından sonra.`
  if (lag.level === 'ok') return { text: 'Güncel', tone: 'ok', title: detail }
  return { text: `${stream} ${durationText(lag.lagSeconds)} geride`, tone: lag.level === 'fault' ? 'bad' : 'warn', title: detail }
}

export function failureRate(rate: SignalErrorRate): number | null {
  return rate.attempts === 0 ? null : (rate.remoteSide + rate.ourSide + rate.rateLimited) / rate.attempts
}

export function errorRateCell(rate: SignalErrorRate): SignalCell {
  const value = failureRate(rate)
  if (value === null) return { text: '—', tone: 'off', title: 'Son 24 saatte bu bağlantıda biten istek denemesi yok.' }
  const split = `${rate.attempts.toLocaleString('tr-TR')} deneme: ${rate.remoteSide} pazaryeri tarafı (5xx, zaman aşımı, ağ) · ${rate.ourSide} bizim taraf (4xx reddi) · ${rate.rateLimited} kota (429)${rate.workflowWait ? ` · ${rate.workflowWait} iş akışı beklemesi/diğer (orana katılmaz)` : ''}. HTTP kodu değil, denemenin hata kodu sınıflandırılır.`
  return { text: `%${(value * 100).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`, tone: value === 0 ? 'ok' : value >= 0.25 ? 'bad' : 'warn', title: split }
}

export function rateLimitCell(limit: SignalRateLimit, now = Date.now()): SignalCell {
  const paused = limit.pausedUntil && new Date(limit.pausedUntil).getTime() > now
  const detail = `Son 24 saatte ${limit.refused24h} deneme 429 ile bitti (denemenin içinde yeniden denenen 429'lar kaydedilmiyor) · kota bekleyen iş: ${limit.waitingJobs} · son okumalarda bildirilen ret: ${limit.lastRunRefusals}${paused ? ` · kota duraklaması ${formatDateTime(limit.pausedUntil!)} saatine kadar` : ''}.`
  if (paused) return { text: 'Kota bekliyor', tone: 'warn', title: detail }
  return { text: limit.refused24h.toLocaleString('tr-TR'), tone: limit.refused24h > 0 || limit.waitingJobs > 0 ? 'warn' : 'ok', title: detail }
}

export function queueCell(queue: SignalQueue | null): SignalCell {
  if (!queue) return { text: 'Yok', tone: 'off', title: 'Çalışmayı bekleyen ya da yeniden denenecek iş yok.' }
  return { text: durationText(queue.ageSeconds), tone: queue.ageSeconds > 3600 ? 'warn' : 'ok', title: `En eski çalışabilir iş (${queue.jobType}) ${durationText(queue.ageSeconds)}dir bekliyor; çalışmayı bekleyen toplam ${queue.pending} iş.` }
}

export function webhookCell(webhook: SignalWebhook, now = Date.now()): SignalCell {
  if (!webhook.used) return { text: 'Yok', tone: 'off', title: 'Bu bağlantıda bildirim (webhook) yok; siparişler zamanlanmış okumayla gelir.' }
  const read = `Son sipariş okuması: ${exact(webhook.lastOrderReadAt)}${webhook.fallbackReadSeconds ? ` (yedek okuma ${everyText(webhook.fallbackReadSeconds)})` : ''}.`
  const base = `Bildirim durumu zamanlayıcının kuralıyla ${webhook.healthy ? 'sağlıklı' : 'sağlıksız'}. ${read} Beklenen bildirim sıklığı Ölçülmüyor (pazaryeri sıklık bildirmiyor).`
  const text = relativeAgeText(webhook.lastReceivedAt, now) ?? 'Bildirim yok'
  return { text, tone: webhook.healthy ? 'ok' : 'warn', title: webhook.lastReceivedAt ? `Son bildirim: ${exact(webhook.lastReceivedAt)}. ${base}` : base }
}

export function signalCells(row: ConnectionSignals, now = Date.now()) {
  return {
    auth: authCell(row.auth, now),
    lag: lagCell(row.lag),
    errorRate: errorRateCell(row.errorRate),
    rateLimit: rateLimitCell(row.rateLimit, now),
    queue: queueCell(row.queue),
    webhook: webhookCell(row.webhook, now)
  }
}
