import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Modal } from '../../shared/components'
import { PlatformMark } from '../../shared/platform-mark'
import './return-bulk-approve.css'

export type BulkApproveItem = {
  id: string
  version: number
  orderNumber: string
  customerName: string
  platformCode: string
  platformName: string
  productCount: number
}

type Outcome = { id: string; ok: boolean; message: string }

type Props = {
  items: BulkApproveItem[]
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'info' | 'error') => void
}

// Approves the chosen returns one after another. Every return keeps its own idempotency key, so a
// retry can never approve the same claim twice, and one failure never stops the others.
export function ReturnBulkApproveDialog({ items: selectedItems, onClose, onDone }: Props) {
  const client = useQueryClient()
  // The list refreshes while the approvals run; keep the rows that were confirmed.
  const [items] = useState(selectedItems)
  const [addToStock, setAddToStock] = useState(false)
  const [running, setRunning] = useState(false)
  const [outcomes, setOutcomes] = useState<Outcome[]>([])
  const finished = !running && outcomes.length > 0
  const failed = outcomes.filter(outcome => !outcome.ok)
  const stockProducts = items.reduce((sum, item) => sum + item.productCount, 0)

  async function run() {
    setRunning(true)
    const results: Outcome[] = []
    for (const item of items) {
      try {
        await hubApi(`/returns/${item.id}/actions`, {
          method: 'POST',
          headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${item.version}"` },
          body: JSON.stringify({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], finalizedWith: item.platformCode.toUpperCase() === 'HEPSIBURADA' ? 'Refund' : null, addToStock })
        })
        results.push({ id: item.id, ok: true, message: 'Onaylandı' })
      } catch (reason) {
        results.push({ id: item.id, ok: false, message: reason instanceof Error ? reason.message : 'İade onaylanamadı.' })
      }
      setOutcomes([...results])
    }
    setRunning(false)
    await client.invalidateQueries({ queryKey: ['returns'] })
    const okCount = results.filter(outcome => outcome.ok).length
    const failCount = results.length - okCount
    onDone(failCount === 0
      ? `${okCount} iade kabulü pazar yerine gönderildi.${addToStock ? ' Stoklar artırıldı.' : ''}`
      : `${okCount} iade onaylandı, ${failCount} iade onaylanamadı. Ayrıntılar toplu onay penceresinde.`, failCount === 0 ? 'success' : 'error')
  }

  const outcomeFor = (id: string) => outcomes.find(outcome => outcome.id === id)

  return <Modal
    open
    title="Toplu İade Onayı"
    description={`${items.length} iade seçildi. Onayla dediğinizde her iade sırayla pazar yerine gönderilir.`}
    onClose={() => { if (!running) onClose() }}
    className="return-bulk-modal"
    footer={<>
      {!finished && <label className="return-bulk-stock"><button type="button" role="switch" aria-checked={addToStock} disabled={running} className={`return-approve-switch${addToStock ? ' is-on' : ''}`} onClick={() => setAddToStock(value => !value)}><i aria-hidden="true" /><span>Tümünü Stoğa Ekle</span></button></label>}
      {finished
        ? <button type="button" onClick={onClose}>Kapat</button>
        : <><button type="button" className="secondary" onClick={onClose} disabled={running}>Vazgeç</button><button type="button" disabled={running || items.length === 0} onClick={() => void run()}>{running ? `Gönderiliyor… (${outcomes.length}/${items.length})` : `${items.length} İadeyi Onayla`}</button></>}
    </>}
  >
    {!finished && addToStock && <p className="return-bulk-note">Onaylanan her iadenin ürünleri yerel stoğa eklenir (toplam {stockProducts.toLocaleString('tr-TR')} ürün satırı). Yerel stok kaydıyla eşleşmeyen bir iade onaylanmaz ve listede hata olarak görünür.</p>}
    {finished && <p className={`return-bulk-note${failed.length ? ' is-error' : ''}`}>{failed.length === 0 ? `${outcomes.length} iade onaylandı.` : `${outcomes.length - failed.length} iade onaylandı, ${failed.length} iade onaylanamadı.`}</p>}
    <div className="return-bulk-list" role="table">
      <div className="return-bulk-head" role="row"><span role="columnheader">Sipariş</span><span role="columnheader">Alıcı</span><span role="columnheader">Ürün</span><span role="columnheader">Durum</span></div>
      {items.map(item => {
        const outcome = outcomeFor(item.id)
        return <div className="return-bulk-row" role="row" key={item.id}>
          <span className="return-bulk-order" role="cell"><span className="return-bulk-logo"><PlatformMark code={item.platformCode} name={item.platformName} /></span><strong>{item.orderNumber}</strong></span>
          <span role="cell">{item.customerName}</span>
          <span role="cell">{item.productCount} Ürün</span>
          <span className={`return-bulk-status${outcome ? (outcome.ok ? ' is-ok' : ' is-error') : ''}`} role="cell" title={outcome?.message}>{outcome ? (outcome.ok ? 'Onaylandı' : outcome.message) : running ? 'Sırada' : 'Bekliyor'}</span>
        </div>
      })}
    </div>
  </Modal>
}
