import { useEffect, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll } from '../../../shared/api'
import { Busy, ErrorBox, TitleHint, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { appendNotification, failureNotice, notificationPlatformName } from '../../../shared/notifications'
import { scopedStorageKey } from '../../../shared/storage-scope'
import { formatDateTime } from '../../../shared/format-date'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import type { IntegrationFeedback } from '../shared'
import { orderSourceState, priceStockMetricTone, priceStockMonitorQueryKey, priceStockMonitorSummary, priceStockNeedsAttention, priceStockProblemSignature, priceStockWriteState, relativeAgeText, type PriceStockConnectionMonitor, type PriceStockMetricTone, type PriceStockMonitor } from './integration-helpers'
import './price-stock-monitor.css'

const notifiedKey = 'price-stock-monitor-notified'

function readNotified(): Record<string, string> {
  try { return JSON.parse(localStorage.getItem(scopedStorageKey(notifiedKey)) ?? '{}') as Record<string, string> } catch { return {} }
}

function writeNotified(value: Record<string, string>) {
  try { localStorage.setItem(scopedStorageKey(notifiedKey), JSON.stringify(value)) } catch { /* a private window keeps no history */ }
}

// A failed or refused price/stock push, or an offer the pipeline cannot send, raises one notification per change of
// the connection's problem set (not one per poll).
function notifyNewProblems(rows: PriceStockConnectionMonitor[]) {
  const notified = readNotified()
  let changed = false
  for (const row of rows) {
    const signature = priceStockProblemSignature(row)
    if (signature === (notified[row.connectionId] ?? null)) continue
    changed = true
    if (signature === null) { delete notified[row.connectionId]; continue }
    notified[row.connectionId] = signature
    const parts = [row.failedPushes7d ? `${row.failedPushes7d} başarısız gönderim` : '', row.manualReviewPushes ? `${row.manualReviewPushes} kısmi/inceleme bekleyen gönderim` : '', row.invalidOffers ? `${row.invalidOffers} gönderilemeyen teklif` : ''].filter(Boolean)
    appendNotification({
      title: 'Fiyat-Stok Gönderiminde Sorun',
      message: `${row.displayName}: ${parts.join(', ')}.${row.latestFailure?.errorSummary ? ` Son hata: ${row.latestFailure.errorSummary}` : ''}`,
      kind: 'error',
      platformCode: row.platformCode,
      connectionId: row.connectionId,
      code: row.latestFailure?.errorCode ?? null,
      jobId: row.latestFailure?.jobId ?? null,
      record: true,
      target: { href: '/jobs?sekme=api-saglik', label: 'API Sağlık’a Git' }
    })
  }
  if (changed) writeNotified(notified)
}

// A time as "12 dk önce", the exact Istanbul time on hover; "—" while it is unknown.
function Ago({ value, prefix }: { value: string | null | undefined; prefix?: string }) {
  const text = relativeAgeText(value)
  if (!value || !text) return <>{prefix ? null : '—'}</>
  return <time dateTime={value} title={formatDateTime(value)}>{prefix ? `${prefix} ` : ''}{text}</time>
}

function PlatformMark({ code }: { code: string }) {
  const source = platformLogoSource(code)
  return source ? <img className={`psm-logo ${platformLogoClass(code)}`} src={source} alt="" aria-hidden="true" /> : <span className="psm-logo is-empty" aria-hidden="true" />
}

function Metric({ label, value, tone, children }: { label: string; value: number; tone: PriceStockMetricTone; children?: ReactNode }) {
  return <span role="cell" data-label={label} className={`psm-m is-${tone}`}><b>{value.toLocaleString('tr-TR')}</b>{children}</span>
}

const writeOffHint = 'Otomatik fiyat ve stok gönderimi bu bağlantıda kapalı. Bu bir ayardır, hata değildir: panelde yapılan fiyat ve stok değişiklikleri pazaryerine kendiliğinden gönderilmez. Entegrasyon ayarlarından açılabilir.'

function writeTitle(label: string, tone: 'on' | 'partial' | 'off') {
  if (tone === 'on') return 'Fiyat ve stok değişiklikleri pazaryerine otomatik gönderiliyor.'
  if (tone === 'partial') return 'Fiyat ve stok yazma akışlarından yalnız biri açık; gönderimler ikisi de açılana kadar bekletiliyor.'
  return label.includes('Ana Anahtar') ? `${writeOffHint} Bağlantının ana anahtarı kapalı.` : writeOffHint
}

function sourceTitle(orderSource: string | undefined) {
  if (orderSource === 'WEBHOOK') return 'Yeni siparişler pazaryerinin bildirimiyle gelir; düzenli okuma yedek olarak daha seyrek çalışır.'
  if (orderSource === 'WEBHOOK_FALLBACK') return 'Pazaryeri bildirimleri sağlıklı görünmüyor; siparişler kısa aralıkla okunuyor.'
  if (orderSource === 'POLLING') return 'Bu bağlantıda bildirim yok; yeni siparişler düzenli aralıkla okunur.'
  return 'Sipariş kaynağı bildirilmedi.'
}

function SummaryStrip({ rows }: { rows: PriceStockConnectionMonitor[] }) {
  const summary = priceStockMonitorSummary(rows)
  const writeText = summary.writesOpen === 0 && summary.writesHeld === 0 ? 'Tümü Kapalı' : summary.writesOpen === summary.connections ? 'Tümü Açık' : [summary.writesOpen ? `${summary.writesOpen} Açık` : '', summary.writesHeld ? `${summary.writesHeld} Bekletiliyor` : '', summary.writesOff ? `${summary.writesOff} Kapalı` : ''].filter(Boolean).join(' · ')
  const sourceParts = [summary.webhook ? `${summary.webhook} Webhook` : '', summary.polling ? `${summary.polling} Düzenli Okuma` : '', summary.unknownSource ? `${summary.unknownSource} Bilinmiyor` : ''].filter(Boolean)
  return <div className="psm-summary" aria-label="Gönderim özeti">
    <div className="psm-stat"><span>Bağlantı</span><b>{summary.connections}</b></div>
    <div className="psm-stat"><span>Otomatik Yazma<TitleHint label="Otomatik Yazma">Otomatik fiyat ve stok gönderimi bilinçli bir ayardır; kapalı olması hata değildir. Kapalıyken panelde yapılan değişiklikler pazaryerine kendiliğinden gönderilmez. Durdur düğmesi açık olan tüm otomatik yazmaları tek seferde kapatır.</TitleHint></span><b>{writeText}</b></div>
    <div className={`psm-stat${summary.webhookTrouble ? ' is-warn' : ''}`}><span>Sipariş Kaynağı</span><b>{sourceParts.join(' · ') || '—'}</b>{summary.webhookTrouble ? <small>{summary.webhookTrouble} webhook sorunlu</small> : null}</div>
    <div className={`psm-stat ${summary.needsAttention ? 'is-warn' : 'is-good'}`}><span>Dikkat Gereken</span><b>{summary.needsAttention ? `${summary.needsAttention} Bağlantı` : 'Yok'}</b></div>
  </div>
}

function ConnectionRow({ row }: { row: PriceStockConnectionMonitor }) {
  const state = priceStockWriteState(row)
  const source = orderSourceState(row)
  const problems = row.failedPushes7d + row.manualReviewPushes
  const lost = row.notFoundLinks + row.rejectedLinks
  const platform = notificationPlatformName(row.platformCode) ?? row.platformCode
  const squash = (text: string) => text.toLocaleLowerCase('tr-TR').replace(/\s+/g, '')
  const showPlatform = !squash(row.displayName).includes(squash(platform))
  return <div className={`psm-row${priceStockNeedsAttention(row) ? ' needs-attention' : ''}`} role="row">
    <span role="cell" className="psm-conn"><PlatformMark code={row.platformCode} /><span><b>{row.displayName}</b>{showPlatform ? <small>{platform}</small> : null}</span></span>
    <span role="cell" className="psm-write" data-label="Otomatik Yazma"><span className={`psm-chip is-write-${state.tone}`} title={writeTitle(state.label, state.tone)}><i aria-hidden="true" />{state.label}</span></span>
    <span role="cell" className="psm-source" data-label="Sipariş Kaynağı">
      <span className={`psm-chip is-source-${source.label === 'Bilinmiyor' ? 'unknown' : source.tone}`} title={sourceTitle(row.orderSource)}><i aria-hidden="true" />{source.label}</span>
      {source.detail ? <small>{source.detail}</small> : null}
      {row.lastNotificationAt ? <small><Ago value={row.lastNotificationAt} prefix="Son bildirim" /></small> : null}
    </span>
    <span role="cell" className="psm-m psm-last" data-label="Son Başarılı"><Ago value={row.lastSuccessfulPushAt} /></span>
    <Metric label="Kuyruk" value={row.queuedJobs} tone={priceStockMetricTone(row.queuedJobs, 'queue')}>{row.waitingForOrderRead ? <small>{row.waitingForOrderRead} sipariş okuması bekliyor</small> : null}</Metric>
    <Metric label="Başarısız" value={problems} tone={priceStockMetricTone(problems, 'failed')}>{row.manualReviewPushes ? <small>{row.manualReviewPushes} kısmi / inceleme</small> : null}</Metric>
    <Metric label="Gönderilemeyen Teklif" value={row.invalidOffers} tone={priceStockMetricTone(row.invalidOffers, 'invalid')} />
    <Metric label="Kayıp İlan" value={lost} tone={priceStockMetricTone(lost, 'lost')}>{lost ? <small>{row.notFoundLinks} bulunamadı · {row.rejectedLinks} reddedildi</small> : null}</Metric>
    {(row.latestFailure || row.invalidOfferSamples.length > 0) && <div className="psm-detail">
      {row.latestFailure && <span><strong>Son Hata:</strong> {row.latestFailure.errorSummary ?? row.latestFailure.errorCode ?? row.latestFailure.status}{row.latestFailure.at ? <> · <Ago value={row.latestFailure.at} /></> : null}</span>}
      {row.invalidOfferSamples.map((issue, index) => <span key={`${issue.code}:${index}`}><strong>Gönderilmedi:</strong> {issue.summary}</span>)}
    </div>}
  </div>
}

// İşlem Takibi › API Sağlık: a summary strip, then per connection whether automatic price/stock writes are on, where
// orders come from, the last successful push, the queue, failed and partial pushes, offers held back as invalid, and the
// listing health counts; plus the emergency stop.
export function PriceStockMonitorPanel({ onFeedback }: { onFeedback: (feedback: IntegrationFeedback) => void }) {
  const client = useQueryClient()
  const monitor = useQuery({ queryKey: priceStockMonitorQueryKey, queryFn: () => hubPoll<PriceStockMonitor>('/price-stock-monitor'), refetchInterval: 60_000, refetchIntervalInBackground: false })
  const [confirm, confirmDialog] = useConfirmDialog()
  const rows = monitor.data?.connections
  useEffect(() => { if (rows) notifyNewProblems(rows) }, [rows])
  const stop = useMutation({
    mutationFn: () => hubApi<{ connectionsStopped: number; policiesDisabled: number; jobsCancelled: number }>('/price-stock-monitor/emergency-stop', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async result => {
      onFeedback({ kind: 'success', title: 'Otomatik Yazmalar Durduruldu', message: `${result.connectionsStopped} bağlantıda otomatik fiyat-stok gönderimi kapatıldı; ${result.jobsCancelled} gönderilmemiş otomatik iş iptal edildi. Panelden başlatılan gönderimler ve sonucu beklenen istekler sürer.` })
      await Promise.all([client.invalidateQueries({ queryKey: priceStockMonitorQueryKey }), client.invalidateQueries({ queryKey: ['sync-policies'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: reason => onFeedback(failureNotice(reason, 'Otomatik yazmalar durdurulamadı.'))
  })
  const anyOpen = rows?.some(row => row.priceWriteEnabled || row.stockWriteEnabled) ?? false
  const confirmStop = async () => {
    const accepted = await confirm({ title: 'Tüm Otomatik Yazmalar Durdurulsun mu?', message: 'Her bağlantıda otomatik fiyat ve stok gönderimi kapatılır ve bekleyen fiyat-stok işleri iptal edilir. Yeniden açtığınızda kapalıyken yapılan değişiklikler gönderilmez; stok ve fiyatları elle kontrol etmeniz gerekir.', confirmLabel: 'Tümünü Durdur', destructive: true })
    if (accepted) stop.mutate()
  }
  return <section className="psm-panel" aria-labelledby="psm-title">{confirmDialog}
    <div className="psm-head"><h2 id="psm-title">Fiyat ve Stok Gönderimi<TitleHint label="Fiyat ve Stok Gönderimi">Panel ana stoktur. Otomatik gönderim yalnız ana anahtar ile fiyat ve stok yazma akışları açıkken çalışır; değişiklik, bağlantının sipariş okuması tamamlandıktan (webhook sağlıklıysa bildirimler alındıktan) sonra gönderilir.</TitleHint></h2>
      <button type="button" className="psm-stop" disabled={stop.isPending || !anyOpen} title={anyOpen ? undefined : 'Açık otomatik yazma yok.'} onClick={() => void confirmStop()}>{stop.isPending ? 'Durduruluyor…' : 'Tüm Otomatik Yazmaları Durdur'}</button>
    </div>
    {monitor.isLoading ? <Busy text="Gönderim durumu yükleniyor…" /> : monitor.isError ? <ErrorBox error={monitor.error} onRetry={() => void monitor.refetch()} /> : !rows?.length ? <p className="psm-empty">İzlenecek pazaryeri bağlantısı yok.</p> : <>
      <SummaryStrip rows={rows} />
      <div className="psm-table" role="table" aria-label="Bağlantı bazında fiyat ve stok gönderimi">
        <div className="psm-row is-head" role="row"><span role="columnheader">Bağlantı</span><span role="columnheader">Otomatik Yazma</span><span role="columnheader">Sipariş Kaynağı</span><span role="columnheader">Son Başarılı</span><span role="columnheader">Kuyruk</span><span role="columnheader">Başarısız</span><span role="columnheader">Gönderilemeyen Teklif</span><span role="columnheader">Kayıp İlan</span></div>
        {rows.map(row => <ConnectionRow row={row} key={row.connectionId} />)}
      </div>
    </>}
  </section>
}
