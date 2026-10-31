import { useEffect } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll } from '../../../shared/api'
import { Busy, ErrorBox, FieldHelp, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { appendNotification, failureNotice, notificationPlatformName } from '../../../shared/notifications'
import { scopedStorageKey } from '../../../shared/storage-scope'
import { DateText, type IntegrationFeedback } from '../shared'
import { orderSourceState, priceStockProblemSignature, priceStockWriteState, type PriceStockConnectionMonitor, type PriceStockMonitor } from './integration-helpers'
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

// İşlem Takibi › API Sağlık: per connection, whether automatic price/stock writes are on, the last successful push, the
// queue, failed and partial pushes, offers held back as invalid, and the listing health counts; plus the emergency stop.
export function PriceStockMonitorPanel({ onFeedback }: { onFeedback: (feedback: IntegrationFeedback) => void }) {
  const client = useQueryClient()
  const monitor = useQuery({ queryKey: ['price-stock-monitor'], queryFn: () => hubPoll<PriceStockMonitor>('/price-stock-monitor'), refetchInterval: 60_000, refetchIntervalInBackground: false })
  const [confirm, confirmDialog] = useConfirmDialog()
  const rows = monitor.data?.connections
  useEffect(() => { if (rows) notifyNewProblems(rows) }, [rows])
  const stop = useMutation({
    mutationFn: () => hubApi<{ connectionsStopped: number; policiesDisabled: number; jobsCancelled: number }>('/price-stock-monitor/emergency-stop', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async result => {
      onFeedback({ kind: 'success', title: 'Otomatik Yazmalar Durduruldu', message: `${result.connectionsStopped} bağlantıda otomatik fiyat-stok gönderimi kapatıldı; ${result.jobsCancelled} gönderilmemiş otomatik iş iptal edildi. Panelden başlatılan gönderimler ve sonucu beklenen istekler sürer.` })
      await Promise.all([client.invalidateQueries({ queryKey: ['price-stock-monitor'] }), client.invalidateQueries({ queryKey: ['sync-policies'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: reason => onFeedback(failureNotice(reason, 'Otomatik yazmalar durdurulamadı.'))
  })
  const anyOpen = rows?.some(row => row.priceWriteEnabled || row.stockWriteEnabled) ?? false
  const confirmStop = async () => {
    const accepted = await confirm({ title: 'Tüm Otomatik Yazmalar Durdurulsun mu?', message: 'Her bağlantıda otomatik fiyat ve stok gönderimi kapatılır ve bekleyen fiyat-stok işleri iptal edilir. Yeniden açtığınızda kapalıyken yapılan değişiklikler gönderilmez; stok ve fiyatları elle kontrol etmeniz gerekir.', confirmLabel: 'Tümünü Durdur', destructive: true })
    if (accepted) stop.mutate()
  }
  return <section className="psm-panel" aria-labelledby="psm-title">{confirmDialog}
    <div className="psm-head"><h2 id="psm-title">Fiyat ve Stok Gönderimi<FieldHelp label="Fiyat ve Stok Gönderimi">Panel ana stoktur. Otomatik gönderim yalnız ana anahtar ile fiyat ve stok yazma akışları açıkken çalışır; değişiklik, bağlantının sipariş okuması tamamlandıktan (webhook sağlıklıysa bildirimler alındıktan) sonra gönderilir.</FieldHelp></h2>
      <button type="button" className="psm-stop" disabled={stop.isPending || !anyOpen} title={anyOpen ? undefined : 'Açık otomatik yazma yok.'} onClick={() => void confirmStop()}>{stop.isPending ? 'Durduruluyor…' : 'Tüm Otomatik Yazmaları Durdur'}</button>
    </div>
    {monitor.isLoading ? <Busy text="Gönderim durumu yükleniyor…" /> : monitor.isError ? <ErrorBox error={monitor.error} onRetry={() => void monitor.refetch()} /> : !rows?.length ? <p className="psm-empty">İzlenecek pazaryeri bağlantısı yok.</p> : <div className="psm-table" role="table" aria-label="Bağlantı bazında fiyat ve stok gönderimi">
      <div className="psm-row is-head" role="row"><span role="columnheader">Bağlantı</span><span role="columnheader">Otomatik Yazma</span><span role="columnheader">Sipariş Kaynağı</span><span role="columnheader">Son Başarılı</span><span role="columnheader">Kuyruk</span><span role="columnheader">Başarısız</span><span role="columnheader">Gönderilemeyen Teklif</span><span role="columnheader">Kayıp İlan</span></div>
      {rows.map(row => {
        const state = priceStockWriteState(row)
        const source = orderSourceState(row)
        const problems = row.failedPushes7d + row.manualReviewPushes
        return <div className="psm-row" role="row" key={row.connectionId}>
          <span role="cell"><b>{row.displayName}</b><small>{notificationPlatformName(row.platformCode) ?? row.platformCode}</small></span>
          <span role="cell" data-label="Otomatik Yazma"><span className={`psm-state is-${state.tone}`}><i aria-hidden="true" />{state.label}</span></span>
          <span role="cell" data-label="Sipariş Kaynağı"><span className={`psm-state is-${source.tone}`}><i aria-hidden="true" />{source.label}</span>{source.detail ? <small>{source.detail}</small> : null}{row.lastNotificationAt ? <small>Son bildirim <DateText value={row.lastNotificationAt} /></small> : null}</span>
          <span role="cell" data-label="Son Başarılı"><DateText value={row.lastSuccessfulPushAt} /></span>
          <span role="cell" data-label="Kuyruk">{row.queuedJobs}{row.waitingForOrderRead ? <small>{row.waitingForOrderRead} sipariş okuması bekliyor</small> : null}</span>
          <span role="cell" data-label="Başarısız" className={`psm-num${problems ? ' is-warn' : ''}`}>{problems}{row.manualReviewPushes ? <small>{row.manualReviewPushes} kısmi / inceleme</small> : null}</span>
          <span role="cell" data-label="Gönderilemeyen Teklif" className={`psm-num${row.invalidOffers ? ' is-warn' : ''}`}>{row.invalidOffers}</span>
          <span role="cell" data-label="Kayıp İlan">{row.notFoundLinks + row.rejectedLinks}{row.notFoundLinks + row.rejectedLinks ? <small>{row.notFoundLinks} bulunamadı · {row.rejectedLinks} reddedildi</small> : null}</span>
          {(row.latestFailure || row.invalidOfferSamples.length > 0) && <div className="psm-detail">
            {row.latestFailure && <span><strong>Son Hata:</strong> {row.latestFailure.errorSummary ?? row.latestFailure.errorCode ?? row.latestFailure.status}{row.latestFailure.at ? <> · <DateText value={row.latestFailure.at} /></> : null}</span>}
            {row.invalidOfferSamples.map((issue, index) => <span key={`${issue.code}:${index}`}><strong>Gönderilmedi:</strong> {issue.summary}</span>)}
          </div>}
        </div>
      })}
    </div>}
  </section>
}
