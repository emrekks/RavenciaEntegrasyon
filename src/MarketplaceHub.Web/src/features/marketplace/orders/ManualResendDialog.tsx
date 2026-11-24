import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { connectionQueries } from '../../../shared/api/query-keys'
import { notificationError } from '../../../shared/notifications'
import { PanelSelect } from '../../../shared/PanelSelect'
import { RrLogo, RrModal } from '../../../shared/ui/rr-components'
import { TitleHint } from '../../../shared/components'
import { formatDateTime } from '../../../shared/format-date'
import { useLacksElevatedRole } from '../../elevated-role'
import { isActiveMarketplaceConnection } from '../marketplace-platform-support'
import { displayOrderNumber, type Connection } from '../shared'
import { manualResendStatus, normalizeManualResendNumber, useManualResends } from './manual-resends'
import './manual-resends.css'

export const MANUAL_RESEND_NOTE = 'Kayıt pazaryerine hiçbir şey göndermez; stok ve fatura değişmez. Sipariş Yeniden Gönderimler\'de "Elle Eklendi" olarak görünür ve Kargoya Verildi Olarak İşaretle ile kapanır.'

/**
 * The Yeniden Gönderim gear's window (owner, 23 Nov 2026: "Platform seçtirsin ve sipariş no yazdırsın"): pick the
 * marketplace, type the order number, and the order enters Yeniden Gönderimler marked "Elle Eklendi". Below, the records
 * added so far with Kaldır. A count typed before (the old gear) is shown once as "Eski Hatırlatma: N" until cleared.
 * Adding and removing are for owners and administrators; other roles see the list only.
 */
export function ManualResendDialog({ legacyCount, onClearLegacy, onClose }: { legacyCount: number; onClearLegacy: () => Promise<void>; onClose: () => void }) {
  const lacksElevated = useLacksElevatedRole()
  const records = useManualResends()
  const connections = useQuery(connectionQueries.list<Connection>())
  const stores = (connections.data?.items ?? []).filter(isActiveMarketplaceConnection)
  const [connectionId, setConnectionId] = useState('')
  const [orderNumber, setOrderNumber] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ text: string; kind: 'error' | 'success' } | null>(null)
  const pickedStore = stores.some(store => store.id === connectionId) ? connectionId : stores.length === 1 ? stores[0]!.id : ''
  const run = async (work: () => Promise<string>, failure: string) => {
    setBusy(true); setMessage(null)
    try { setMessage({ text: await work(), kind: 'success' }) } catch (error) { setMessage({ text: notificationError(error, failure).message, kind: 'error' }) } finally { setBusy(false) }
  }
  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!pickedStore) { setMessage({ text: 'Platform seçin.', kind: 'error' }); return }
    if (!normalizeManualResendNumber(orderNumber)) { setMessage({ text: 'Sipariş numarasını yazın.', kind: 'error' }); return }
    void run(async () => {
      const created = await records.add(pickedStore, orderNumber)
      setOrderNumber('')
      return `${displayOrderNumber(created.orderNumber)} Yeniden Gönderimler'e eklendi.`
    }, 'Yeniden gönderim eklenemedi.')
  }
  return <RrModal crumb="Siparişler" label="Yeniden Gönderim Ekle" title={<>Yeniden Gönderim Ekle<TitleHint label="Yeniden Gönderim Ekle">{MANUAL_RESEND_NOTE}</TitleHint></>} desc="Platformu seçin, sipariş numarasını yazın" size="md" modalClass="rr-mresend-modal" onClose={onClose}
    foot={<button type="button" className="rr-btn rr-quiet" onClick={onClose}>Kapat</button>}>
    <div className="rr-mresend">
      {legacyCount > 0 && <div className="rr-mresend-legacy" role="note">
        <span><b>Eski Hatırlatma: {legacyCount.toLocaleString('tr-TR')}</b><small>Önceden elle yazılan sayı kayda çevrilemez; siparişleri aşağıdan ekleyip bu sayıyı temizleyin.</small></span>
        {!lacksElevated && <button type="button" className="rr-btn" disabled={busy} onClick={() => void run(async () => { await onClearLegacy(); return 'Eski hatırlatma temizlendi.' }, 'Eski hatırlatma temizlenemedi.')}>Temizle</button>}
      </div>}
      {!lacksElevated && <form className="rr-mresend-form" onSubmit={submit} noValidate>
        <div className="rr-fld rr-mresend-store"><small id="rr-mresend-store-label">Platform</small>
          <PanelSelect id="rr-mresend-store" ariaLabelledBy="rr-mresend-store-label" value={pickedStore} width={200} placeholder={connections.isPending ? 'Yükleniyor…' : 'Platform Seçin'} disabled={busy || stores.length === 0}
            onChange={value => { setConnectionId(value); setMessage(null) }}
            options={stores.map(store => ({ value: store.id, text: store.displayName, label: <span className="rr-mresend-opt"><RrLogo code={store.platformCode} />{store.displayName}</span> }))} />
        </div>
        <label className="rr-fld rr-mresend-number"><small>Sipariş No</small>
          <input className="rr-in" value={orderNumber} disabled={busy} inputMode="text" autoComplete="off" enterKeyHint="done" placeholder="Pazaryeri sipariş no" aria-invalid={message?.kind === 'error' ? true : undefined} onChange={event => { setOrderNumber(event.target.value); setMessage(null) }} />
        </label>
        <button type="submit" className="rr-btn rr-primary rr-mresend-add" disabled={busy}>Ekle</button>
      </form>}
      {message && <p className={`rr-mresend-msg${message.kind === 'error' ? ' is-error' : ''}`} role={message.kind === 'error' ? 'alert' : 'status'}>{message.text}</p>}
      <div className="rr-mresend-head"><span className="rr-h3">Elle Eklenenler</span><small>{records.items.length.toLocaleString('tr-TR')} Kayıt</small></div>
      {records.loading ? <p className="rr-mresend-empty">Yükleniyor…</p>
        : records.error ? <p className="rr-mresend-msg is-error" role="alert">{notificationError(records.error, 'Kayıtlar okunamadı.').message}</p>
        : records.items.length === 0 ? <p className="rr-mresend-empty">Elle eklenen yeniden gönderim yok.</p>
        : <ul className="rr-mresend-list">{records.items.map(record => <li key={record.id}>
          <RrLogo code={record.platformCode} />
          <span className="rr-mresend-main"><b className="rr-num">{displayOrderNumber(record.orderNumber)}</b><small>{record.connectionName} · {formatDateTime(record.createdAt)}</small></span>
          <span className={`rr-mresend-state${record.shippedAt ? ' is-shipped' : ''}`}>{manualResendStatus(record)}</span>
          {!lacksElevated && <button type="button" className="rr-btn rr-mresend-remove" disabled={busy} aria-label={`${displayOrderNumber(record.orderNumber)} kaydını kaldır`} onClick={() => void run(async () => { await records.remove(record.id); return `${displayOrderNumber(record.orderNumber)} Yeniden Gönderimler'den kaldırıldı.` }, 'Kayıt kaldırılamadı.')}>Kaldır</button>}
        </li>)}</ul>}
    </div>
  </RrModal>
}
