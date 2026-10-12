import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Modal } from '../../shared/components'
import { PlatformMark } from '../../shared/platform-mark'
import './return-bulk-approve.css'

export type BulkDecisionLine = {
  id: string
  title: string
  imageUrl: string | null
  quantity: number
  sku: string
  barcode: string | null
  color: string | null
  size: string | null
  modelCode: string | null
}

export type BulkDecisionItem = {
  id: string
  version: number
  orderNumber: string
  customerName: string
  orderedAt: string | null
  packageNumber: string | null
  platformCode: string
  platformName: string
  lines: BulkDecisionLine[]
}

type Outcome = { id: string; ok: boolean; message: string }
type RejectReason = { id: string; name: string; evidenceRequired: boolean }

type Props = {
  mode: 'APPROVE' | 'REJECT'
  items: BulkDecisionItem[]
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'info' | 'error') => void
}

// Sends one decision per selected return, one after another. Every return keeps its own idempotency
// key, so a retry can never decide the same claim twice, and one failure never stops the others.
export function ReturnBulkDecisionDialog({ mode, items: selectedItems, onClose, onDone }: Props) {
  const client = useQueryClient()
  // The list refreshes while the decisions run; keep the cards that were confirmed.
  const [items] = useState(selectedItems)
  const approving = mode === 'APPROVE'
  const [stockIds, setStockIds] = useState<string[]>([])
  const [reasonId, setReasonId] = useState('')
  const [explanation, setExplanation] = useState('')
  const [running, setRunning] = useState(false)
  const [outcomes, setOutcomes] = useState<Outcome[]>([])
  const finished = !running && outcomes.length > 0
  const failed = outcomes.filter(outcome => !outcome.ok)
  const allStock = items.length > 0 && stockIds.length === items.length
  const stockUnits = items.filter(item => stockIds.includes(item.id)).reduce((sum, item) => sum + item.lines.reduce((lineSum, line) => lineSum + line.quantity, 0), 0)
  const singlePlatform = new Set(items.map(item => item.platformCode.toUpperCase())).size === 1

  // Rejecting needs a reason the marketplace accepts without evidence files. The reasons come from the
  // first selected return; a reason a later return does not accept is reported on that return only.
  const reasons = useQuery({
    queryKey: ['return-reasons', 'bulk', items[0]?.id],
    queryFn: () => hubApi<RejectReason[]>(`/returns/${items[0]!.id}/rejection-reasons`),
    enabled: !approving && singlePlatform && items.length > 0,
    retry: false
  })
  const usableReasons = (reasons.data ?? []).filter(reason => !reason.evidenceRequired)
  const canReject = singlePlatform && reasonId !== '' && explanation.trim().length > 0 && explanation.trim().length <= 500
  const canSubmit = items.length > 0 && (approving || canReject)

  async function run() {
    setRunning(true)
    const results: Outcome[] = []
    for (const item of items) {
      try {
        await hubApi(`/returns/${item.id}/actions`, {
          method: 'POST',
          headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${item.version}"` },
          body: JSON.stringify(approving
            ? { action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], finalizedWith: item.platformCode.toUpperCase() === 'HEPSIBURADA' ? 'Refund' : null, addToStock: stockIds.includes(item.id) }
            : { action: 'REJECT', reasonCode: reasonId, explanation: explanation.trim(), evidenceAssetIds: [], finalizedWith: null })
        })
        results.push({ id: item.id, ok: true, message: approving ? 'Onaylandı' : 'Reddedildi' })
      } catch (reason) {
        results.push({ id: item.id, ok: false, message: reason instanceof Error ? reason.message : approving ? 'İade onaylanamadı.' : 'İade reddedilemedi.' })
      }
      setOutcomes([...results])
    }
    setRunning(false)
    await client.invalidateQueries({ queryKey: ['returns'] })
    const okCount = results.filter(outcome => outcome.ok).length
    const failCount = results.length - okCount
    const noun = approving ? 'onaylandı' : 'reddedildi'
    onDone(failCount === 0
      ? `${okCount} iade ${approving ? 'kabulü' : 'reddi'} pazar yerine gönderildi.${approving && stockIds.length > 0 ? ' Seçilen iadelerin stokları artırıldı.' : ''}`
      : `${okCount} iade ${noun}, ${failCount} iade ${approving ? 'onaylanamadı' : 'reddedilemedi'}. Ayrıntılar toplu işlem penceresinde.`, failCount === 0 ? 'success' : 'error')
  }

  const toggleStock = (id: string) => setStockIds(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id])

  return <Modal
    open
    title={approving ? 'Toplu İade Onayı' : 'Toplu İade Reddi'}
    description={`${items.length} iade seçildi. ${approving ? 'Onayla' : 'Reddet'} dediğinizde her iade sırayla pazar yerine gönderilir.`}
    onClose={() => { if (!running) onClose() }}
    className="return-bulk-modal"
    footer={<>
      {!finished && approving && <button type="button" role="switch" aria-checked={allStock} disabled={running} className={`return-approve-switch return-bulk-stock${allStock ? ' is-on' : ''}`} onClick={() => setStockIds(allStock ? [] : items.map(item => item.id))}><i aria-hidden="true" /><span>Tümünü Stoğa Ekle</span></button>}
      {!finished && approving && stockUnits > 0 && <span className="return-approve-stock-summary">Stoğa Eklenecek: +{stockUnits.toLocaleString('tr-TR')} adet</span>}
      {finished
        ? <button type="button" onClick={onClose}>Kapat</button>
        : <><button type="button" className="secondary" onClick={onClose} disabled={running}>Vazgeç</button><button type="button" className={approving ? undefined : 'destructive'} disabled={running || !canSubmit} onClick={() => void run()}>{running ? `Gönderiliyor… (${outcomes.length}/${items.length})` : approving ? `${items.length} İadeyi Onayla` : `${items.length} İadeyi Reddet`}</button></>}
    </>}
  >
    {!finished && !approving && !singlePlatform && <p className="return-bulk-note is-error">Toplu ret için tek bir pazar yerinin iadelerini seçin. Ret nedenleri pazar yerine göre değişir.</p>}
    {!finished && !approving && singlePlatform && <div className="return-bulk-reject">
      <label><span>Ret Nedeni</span>
        <select value={reasonId} onChange={event => setReasonId(event.target.value)} disabled={running || reasons.isLoading}>
          <option value="">{reasons.isLoading ? 'Nedenler yükleniyor…' : 'Ret nedeni seçin'}</option>
          {usableReasons.map(reason => <option key={reason.id} value={reason.id}>{reason.name}</option>)}
        </select>
      </label>
      <label><span>Açıklama</span>
        <textarea value={explanation} maxLength={500} rows={3} disabled={running} onChange={event => setExplanation(event.target.value)} placeholder="Müşteriye iletilecek ret açıklaması" />
        <small>{explanation.trim().length} / 500</small>
      </label>
      {reasons.isError && <p className="return-bulk-note is-error">Ret nedenleri alınamadı. Pencereyi kapatıp tekrar deneyin.</p>}
      {reasons.data && usableReasons.length === 0 && <p className="return-bulk-note is-error">Bu iadeler için kanıt dosyası gerektirmeyen bir ret nedeni yok. Bu iadeleri tek tek reddedin.</p>}
      {usableReasons.length > 0 && <p className="return-bulk-note">Kanıt dosyası gerektiren nedenler toplu retle kullanılamaz, bu yüzden listede görünmez.</p>}
    </div>}
    {finished && <p className={`return-bulk-note${failed.length ? ' is-error' : ''}`}>{failed.length === 0 ? `${outcomes.length} iade ${approving ? 'onaylandı' : 'reddedildi'}.` : `${outcomes.length - failed.length} iade ${approving ? 'onaylandı' : 'reddedildi'}, ${failed.length} iade ${approving ? 'onaylanamadı' : 'reddedilemedi'}.`}</p>}
    <div className="return-bulk-cards">
      {items.map(item => {
        const outcome = outcomes.find(entry => entry.id === item.id)
        const stockOn = stockIds.includes(item.id)
        return <article className="return-bulk-card" key={item.id}>
          <header>
            <span className="return-bulk-order"><span className="return-bulk-logo"><PlatformMark code={item.platformCode} name={item.platformName} /></span><strong>{item.orderNumber}</strong></span>
            <dl>
              <div><dt>Müşteri</dt><dd>{item.customerName}</dd></div>
              {item.orderedAt && <div><dt>Sipariş Tarihi</dt><dd>{new Date(item.orderedAt).toLocaleString('tr-TR')}</dd></div>}
              {item.packageNumber && <div><dt>Paket No</dt><dd>{item.packageNumber}</dd></div>}
            </dl>
            <span className={`return-bulk-status${outcome ? (outcome.ok ? ' is-ok' : ' is-error') : ''}`} title={outcome?.message}>{outcome ? (outcome.ok ? (approving ? 'Onaylandı' : 'Reddedildi') : outcome.message) : running ? 'Sırada' : 'Bekliyor'}</span>
          </header>
          <div className="return-bulk-products">
            {item.lines.map(line => {
              const facts = [['Stok Kodu', line.sku], ['Renk', line.color], ['Beden', line.size], ['Barkod', line.barcode], ['Model Kodu', line.modelCode]].filter((fact): fact is [string, string] => Boolean(fact[1]))
              return <div className="return-bulk-product" key={line.id}>
                <span className="reference-product-media return-approve-image">{line.imageUrl ? <img src={line.imageUrl} alt="" /> : <span className="reference-product-placeholder">↩</span>}<span className="quantity-bubble" role="img" aria-label={`${line.quantity} adet`} title={`${line.quantity} adet`}>{line.quantity}</span></span>
                <span className="return-approve-text"><strong>{line.title}</strong>{facts.map(([label, value]) => <small key={label}>{label}: {value}</small>)}</span>
              </div>
            })}
            {approving && <button type="button" role="switch" aria-checked={stockOn} disabled={running || finished} className={`return-approve-switch return-bulk-card-stock${stockOn ? ' is-on' : ''}`} onClick={() => toggleStock(item.id)}><i aria-hidden="true" /><span>Stoğa Ekle</span></button>}
          </div>
        </article>
      })}
    </div>
  </Modal>
}
