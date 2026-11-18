import { formatDateTime } from '../../shared/format-date'
import { relativeAgeText } from '../marketplace/integrations/integration-helpers'
import type { ApiAnswer } from '../../shared/api/api-answer'

// "API Sağlığı": the six signals per connection as one cell each (a short value, a tone, the detail as a tooltip).
// Every figure comes from the API as stored; a signal the data cannot answer says "Ölçülmüyor" and why.

type SignalsAnswer = ApiAnswer<'getApiHealthSignals'>
type ConnectionAnswer = SignalsAnswer['connections'][number]
// CONTRACT: the API declares the auth state and the lag level as strings; ApiHealthSignals.cs writes only these constants
// (AuthValid / AuthRejected / AuthUnknown, LevelOk / LevelWarn / LevelFault).
export type SignalAuth = Omit<ConnectionAnswer['auth'], 'state'> & { state: 'valid' | 'rejected' | 'unknown' }
export type SignalLag = Omit<NonNullable<ConnectionAnswer['lag']>, 'level'> & { level: 'ok' | 'warn' | 'fault' }
export type SignalErrorRate = ConnectionAnswer['errorRate']
export type SignalRateLimit = ConnectionAnswer['rateLimit']
export type SignalQueue = NonNullable<ConnectionAnswer['queue']>
export type SignalWebhook = ConnectionAnswer['webhook']
export type ConnectionSignals = Omit<ConnectionAnswer, 'auth' | 'lag'> & { auth: SignalAuth; lag: SignalLag | null }
export type ApiHealthSignals = Omit<SignalsAnswer, 'connections'> & { connections: ConnectionSignals[] }

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
    return { text: when ? `${when.replace(' önce', '')} önce reddedildi` : 'Reddedildi', tone: 'bad', title: `Pazaryeri anahtarı reddetti (kod ${auth.lastRejectedCode ?? 'bilinmiyor'}); sonrasında başarılı istek yok. Son reddedilme: ${exact(auth.lastRejectedAt)}. Son başarılı istek: ${exact(auth.lastOkAt)}.` }
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
  const split = `${rate.attempts.toLocaleString('tr-TR')} deneme: ${rate.remoteSide} pazaryeri tarafı (sunucu hatası, zaman aşımı, ağ) · ${rate.ourSide} bizim taraf (ret) · ${rate.rateLimited} kota sınırı${rate.workflowWait ? ` · ${rate.workflowWait} iş akışı beklemesi/diğer (orana katılmaz)` : ''}.`
  return { text: `%${(value * 100).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`, tone: value === 0 ? 'ok' : value >= 0.25 ? 'bad' : 'warn', title: split }
}

export function rateLimitCell(limit: SignalRateLimit, now = Date.now()): SignalCell {
  const paused = limit.pausedUntil && new Date(limit.pausedUntil).getTime() > now
  const detail = `Son 24 saatte ${limit.refused24h} deneme kota sınırına takılarak bitti · kota bekleyen iş: ${limit.waitingJobs} · son okumalarda bildirilen ret: ${limit.lastRunRefusals}${paused ? ` · kota duraklaması ${formatDateTime(limit.pausedUntil!)} saatine kadar` : ''}.`
  if (paused) return { text: 'Kota Bekliyor', tone: 'warn', title: detail }
  return { text: limit.refused24h.toLocaleString('tr-TR'), tone: limit.refused24h > 0 || limit.waitingJobs > 0 ? 'warn' : 'ok', title: detail }
}

export function queueCell(queue: SignalQueue | null): SignalCell {
  if (!queue) return { text: 'Yok', tone: 'off', title: 'Çalışmayı bekleyen ya da yeniden denenecek iş yok.' }
  return { text: durationText(queue.ageSeconds), tone: queue.ageSeconds > 3600 ? 'warn' : 'ok', title: `En eski çalışabilir iş (${queue.jobType}) ${durationText(queue.ageSeconds)}dir bekliyor; çalışmayı bekleyen toplam ${queue.pending} iş.` }
}

export function webhookCell(webhook: SignalWebhook, now = Date.now()): SignalCell {
  if (!webhook.used) return { text: 'Yok', tone: 'off', title: 'Bu bağlantıda anlık bildirim (webhook) yok; siparişler zamanlanmış okumayla gelir.' }
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

// The detail window of a warning or fault cell: label-value rows from the data the cell is made of (nothing invented).
export type SignalKind = 'auth' | 'lag' | 'errorRate' | 'rateLimit' | 'queue' | 'webhook'
export type SignalDetail = { title: string; lead?: string; facts: Array<[string, string]> }
export const SIGNAL_LABELS: Record<SignalKind, string> = { auth: 'Anahtar / Yetki', lag: 'Geride Kalma', errorRate: 'Hata Oranı', rateLimit: 'Hız Sınırı', queue: 'Kuyruk Yaşı', webhook: 'Webhook' }

export function signalDetail(kind: SignalKind, row: ConnectionSignals, now = Date.now()): SignalDetail {
  const title = `${SIGNAL_LABELS[kind]} · ${row.displayName}${kind === 'errorRate' ? ' (son 24 saat)' : ''}`
  const count = (value: number) => value.toLocaleString('tr-TR')
  if (kind === 'auth') {
    const auth = row.auth
    return { title, lead: auth.state === 'rejected' ? 'Pazaryeri anahtarı reddetti; sonrasında başarılı istek yok.' : undefined, facts: [
      ['Durum', auth.state === 'valid' ? 'Geçerli' : auth.state === 'rejected' ? 'Reddedildi' : 'Bilinmiyor'],
      ['Son reddedilme', exact(auth.lastRejectedAt ?? null)], ['Son ret kodu', auth.lastRejectedCode ?? 'bilinmiyor'], ['Son başarılı istek', exact(auth.lastOkAt ?? null)]
    ] }
  }
  if (kind === 'lag') {
    const lag = row.lag
    if (!lag) return { title, facts: [['Durum', 'Okuma kaydı yok']] }
    return { title, lead: 'Uyarı beklenen sürenin 3 katından, arıza 10 katından sonra başlar.', facts: [
      ['Akış', STREAM_NAMES[lag.stream] ?? lag.stream], ['Son başarılı okuma', `${durationText(lag.lagSeconds)} önce`], ['Zamanlama', everyText(lag.expectedSeconds)], ['Seviye', lag.level === 'ok' ? 'Güncel' : lag.level === 'warn' ? 'Uyarı' : 'Arıza']
    ] }
  }
  if (kind === 'errorRate') {
    const rate = row.errorRate
    const value = failureRate(rate)
    return { title, lead: 'Oran = (pazaryeri tarafı + bizim taraf + kota sınırı) ÷ toplam deneme; iş akışı beklemesi orana katılmaz.', facts: [
      ['Toplam deneme', count(rate.attempts)], ['Pazaryeri tarafı (sunucu hatası, zaman aşımı, ağ)', count(rate.remoteSide)], ['Bizim taraf (ret)', count(rate.ourSide)], ['Kota sınırı', count(rate.rateLimited)], ['İş akışı beklemesi / diğer', count(rate.workflowWait ?? 0)],
      ['Hata oranı', value === null ? '—' : `%${(value * 100).toLocaleString('tr-TR', { maximumFractionDigits: 1 })}`]
    ] }
  }
  if (kind === 'rateLimit') {
    const limit = row.rateLimit
    const paused = limit.pausedUntil && new Date(limit.pausedUntil).getTime() > now
    return { title, facts: [
      ['Kotaya takılan deneme (24 sa)', count(limit.refused24h)], ['Kota bekleyen iş', count(limit.waitingJobs)], ['Son okumalarda bildirilen ret', count(limit.lastRunRefusals)], ['Kota duraklaması', paused ? `${formatDateTime(limit.pausedUntil!)} saatine kadar` : 'Yok']
    ] }
  }
  if (kind === 'queue') {
    const queue = row.queue
    if (!queue) return { title, facts: [['Durum', 'Çalışmayı bekleyen iş yok']] }
    return { title, lead: 'Bir saati aşan bekleme uyarı sayılır.', facts: [['En eski çalışabilir iş', queue.jobType], ['Bekleme süresi', durationText(queue.ageSeconds)], ['Çalışmayı bekleyen toplam iş', count(queue.pending)]] }
  }
  const webhook = row.webhook
  return { title, lead: webhook.used ? undefined : 'Bu bağlantıda anlık bildirim yok; siparişler zamanlanmış okumayla gelir.', facts: webhook.used ? [
    ['Bildirim durumu', webhook.healthy ? 'Sağlıklı' : 'Sağlıksız'], ['Son bildirim', exact(webhook.lastReceivedAt ?? null)], ['Son sipariş okuması', exact(webhook.lastOrderReadAt ?? null)], ['Yedek okuma', webhook.fallbackReadSeconds ? everyText(webhook.fallbackReadSeconds) : 'yok']
  ] : [['Durum', 'Kullanılmıyor']] }
}
