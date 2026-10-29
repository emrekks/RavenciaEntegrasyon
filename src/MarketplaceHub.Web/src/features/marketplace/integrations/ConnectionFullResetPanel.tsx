// Verileri Sıfırla › Tam Sıfırlama: every record of the connection goes, issued invoices included, while its settings
// stay. The reset runs as a background job; the panel shows what it removes and, once it started, how far it is.
import { useEffect, useEffectEvent, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { UiIcon } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { failureNotice } from '../../../shared/notifications'
import { formatIstanbul } from '../../../shared/format-date'
import { connectionFullResetItems, connectionFullResetRunning, type ConnectionFullResetView } from '../connection-data-reset'
import type { Connection, IntegrationFeedback } from '../shared'

const number = (value: number) => value.toLocaleString('tr-TR')

export function connectionFullResetQueryKey(connectionId: string) {
  return ['connection-full-reset', connectionId] as const
}

export function fullResetStatusText(job: ConnectionFullResetView['job']) {
  if (!job) return null
  if (connectionFullResetRunning(job.status)) return `Sıfırlama sürüyor${job.progressPercent !== null ? ` · %${job.progressPercent}` : ''}: ${job.progressLabel ?? 'Sırada bekliyor'}`
  if (job.status === 'SUCCEEDED') return `Son tam sıfırlama ${formatIstanbul(job.createdAt)} tarihinde başlatıldı ve tamamlandı.`
  if (job.status === 'CANCELLED') return 'Son tam sıfırlama iptal edildi.'
  return `Son tam sıfırlama tamamlanamadı${job.progressLabel ? `: ${job.progressLabel}` : '.'}`
}

export function ConnectionFullResetPanel({ item, onFeedback, onFinished, onCancel }: { item: Connection; onFeedback: (feedback: IntegrationFeedback) => void; onFinished: () => void; onCancel: () => void }) {
  const client = useQueryClient()
  const [confirmation, setConfirmation] = useState('')
  const queryKey = connectionFullResetQueryKey(item.id)
  const view = useQuery({ queryKey, queryFn: () => hubApi<ConnectionFullResetView>(`/connections/${item.id}/full-reset`), staleTime: 0, refetchInterval: query => connectionFullResetRunning(query.state.data?.job?.status) ? 3000 : false })
  const job = view.data?.job ?? null
  const running = connectionFullResetRunning(job?.status)
  // When the reset this panel watched ends, the lists that showed the removed records are read again.
  const watched = useRef<string | null>(null)
  const finished = useEffectEvent(onFinished)
  useEffect(() => {
    if (!job) return
    if (running) watched.current = job.jobId
    else if (watched.current === job.jobId) { watched.current = null; finished() }
  }, [job, running])
  const start = useMutation({
    mutationFn: () => hubApi<ConnectionFullResetView>(`/connections/${item.id}/full-reset`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey(), 'If-Match': `"v${item.version}"` }, body: JSON.stringify({ confirmation }) }),
    onSuccess: result => { client.setQueryData(queryKey, result); setConfirmation(''); onFeedback({ kind: 'success', title: 'Tam Sıfırlama Başladı', message: `${item.displayName} bağlantısının verileri arka planda siliniyor; ilerlemeyi bu pencerede ve İşler ekranında izleyebilirsiniz.` }) },
    onError: reason => onFeedback(failureNotice(reason, 'Tam sıfırlama başlatılamadı.'))
  })
  const counts = view.data ? new Map(view.data.items.map(entry => [entry.scope, entry.count])) : undefined
  // Product links name products that are already counted (removed) or kept; they are not added to the total.
  const total = counts ? connectionFullResetItems.filter(entry => entry.scope !== 'PRODUCT_LINKS').reduce((sum, entry) => sum + (counts.get(entry.scope) ?? 0), 0) : undefined
  const kept = view.data?.keptProducts ?? 0
  const confirmed = confirmation.trim() === item.displayName
  const busy = start.isPending || running
  const status = fullResetStatusText(job)
  const notes: Array<{ warn?: boolean; text: string }> = [
    { warn: true, text: 'Bu bağlantının bütün verileri bu panelden kalıcı olarak silinir ve geri alınamaz. Eski finans ve kargo kayıtları pazaryerinin izin verdiği süreyle sınırlı olduğu için yeniden alınamayabilir.' },
    { warn: true, text: 'Kesilmiş faturalar da silinir. Faturalar E-Faturam’da ve GİB’de geçerli kalır; panelden silmek faturayı iptal etmez. Pazaryerine gönderilen fatura bağlantıları artık açılmaz.' },
    { warn: true, text: 'Siparişler yeniden çekildiğinde panel onları faturasız görür; aynı siparişe ikinci kez fatura kesmeyin.' },
    { text: `Pazaryerine ve E-Faturam’a hiçbir şey yazılmaz; ${item.displayName} hesabındaki kayıtlar olduğu gibi kalır ve sonraki eşitlemede yeniden alınabilir.` },
    { text: 'Bağlantı ayarları korunur: kimlik bilgileri, eşitleme, fatura, konum ve stok ayarları, eşleme şablonları, kargo eşleşmeleri ve bildirim aboneliği.' },
    ...(kept ? [{ text: `${number(kept)} ürün başka bir bağlantıda kullanıldığı ya da panelde oluşturulduğu için korunur; yalnız bu bağlantıyla bağı silinir.` }] : []),
    { text: 'Siparişlerin düşürdüğü stok geri eklenmez; aynı siparişler yeniden çekildiğinde stok ikinci kez düşmez.' },
    { text: 'Sıfırlama arka planda çalışır ve bağlantıda süren eşitlemelerin bitmesini bekler.' }
  ]
  return <div className="drl connection-full-reset">
    <div className="drl-list" role="group" aria-label="Tam sıfırlamada silinecek veriler">
      <section className="drl-group">
        <header><span><b>Bağlantının Bütün Verileri</b><small>Ayarlar dışında bu bağlantıya ait her kayıt silinir.</small></span></header>
        {connectionFullResetItems.map(entry => <div key={entry.scope} className="drl-row is-on is-locked">
          <i aria-hidden="true"><UiIcon name="check" size={13} /></i>
          <span className="drl-text"><b>{entry.label}</b><small>{entry.description}</small></span>
          <span className="drl-count">{counts?.has(entry.scope) ? <><strong className="num">{number(counts.get(entry.scope)!)}</strong>{entry.unit}</> : <span className="drl-count-none">{view.isError ? 'Sayı alınamadı' : 'Sayılıyor…'}</span>}</span>
        </div>)}
      </section>
    </div>
    <aside className="drl-receipt" aria-label="Tam sıfırlama özeti">
      <h3>Tam Sıfırlama</h3>
      {status && <p className={`connection-full-reset-status${running ? ' is-running' : ''}`} role="status">{status}</p>}
      <div className="drl-total"><span>Silinecek</span><span className="num">{total !== undefined ? `${number(total)} kayıt` : ''}</span></div>
      <div className="drl-notes">{notes.map((note, index) => <p key={index} className={note.warn ? 'is-warn' : undefined}><UiIcon name={note.warn ? 'alert' : 'check'} size={15} /><span>{note.text}</span></p>)}</div>
      <div className="drl-confirm">
        <label htmlFor={`full-reset-${item.id}-confirm`}>Onaylamak için <b>{item.displayName}</b> yazın</label>
        <span className={`drl-field${confirmed ? ' is-ok' : ''}`}><input id={`full-reset-${item.id}-confirm`} value={confirmation} onChange={event => setConfirmation(event.target.value)} disabled={busy} autoComplete="off" placeholder={item.displayName} />{confirmed && <UiIcon name="check" size={16} />}</span>
        <button type="button" className="destructive drl-go" onClick={() => start.mutate()} disabled={!confirmed || busy || !view.data}>{start.isPending ? 'Başlatılıyor…' : running ? 'Sıfırlama Sürüyor…' : 'Tam Sıfırla'}</button>
        <button type="button" className="secondary drl-cancel" onClick={onCancel} disabled={start.isPending}>{running ? 'Kapat' : 'Vazgeç'}</button>
      </div>
    </aside>
  </div>
}
