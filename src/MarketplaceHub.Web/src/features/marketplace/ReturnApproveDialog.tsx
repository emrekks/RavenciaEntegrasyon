import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal } from '../../shared/components'
import { PlatformMark } from '../../shared/platform-mark'
import './return-approve-dialog.css'

export type ReturnApproveRow = {
  id: string
  title: string
  imageUrl: string | null
  quantity: number
  sku: string
  barcode: string | null
  color: string | null
  size: string | null
  modelCode: string | null
  hasInventoryMapping: boolean
}

export type ReturnApproveOrderInfo = {
  platformCode: string
  platformName: string
  orderNumber: string
  customer: string
  orderedAt: string | null
  packageNumber: string | null
  claimCode: string
  reason: string | null
}

type Props = {
  open: boolean
  claimId: string
  version: number
  marketplaceName: string
  order: ReturnApproveOrderInfo
  rows: ReturnApproveRow[]
  loading: boolean
  error: unknown
  // Hepsiburada decides the whole claim at once and asks whether it ends in a refund or an exchange.
  requiresFinalization?: boolean
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'info' | 'error') => void
}

// The approval dialog: order summary on top, then one framed row per product with its own
// "put back in stock" switch, and a single confirmation.
export function ReturnApproveDialog({ open, claimId, version, marketplaceName, order, rows, loading, error, requiresFinalization = false, onClose, onDone }: Props) {
  const client = useQueryClient()
  const [selected, setSelected] = useState<string[]>([])
  const [stockIds, setStockIds] = useState<string[]>([])
  const [finalizedWith, setFinalizedWith] = useState<'Refund' | 'Change' | null>(null)
  const rowKey = rows.map(row => row.id).join('|')

  useEffect(() => {
    setSelected(rows.map(row => row.id))
    setStockIds([])
    setFinalizedWith(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rowKey, open])

  const chosen = rows.filter(row => selected.includes(row.id))
  const stockRows = chosen.filter(row => stockIds.includes(row.id) && row.hasInventoryMapping)
  const stockQuantity = stockRows.reduce((sum, row) => sum + row.quantity, 0)

  const approve = useMutation({
    mutationFn: () => hubApi<{ status: string }>(`/returns/${claimId}/actions`, {
      method: 'POST',
      headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${version}"` },
      body: JSON.stringify({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], returnLineIds: chosen.map(row => row.id), finalizedWith: requiresFinalization ? finalizedWith : null, stockLineIds: stockRows.map(row => row.id) })
    }),
    onSuccess: async () => {
      const stock = stockRows.length > 0 ? ` Stok ${stockQuantity.toLocaleString('tr-TR')} adet artırıldı.` : ''
      onDone(`İade kabulü ${marketplaceName}’a gönderildi.${stock}`, 'success')
      await client.invalidateQueries({ queryKey: ['return', claimId] })
      await client.invalidateQueries({ queryKey: ['returns'] })
      onClose()
    },
    onError: reason => onDone(reason instanceof Error ? reason.message : 'İade kararı başlatılamadı.', 'error')
  })

  const canSubmit = chosen.length > 0 && (!requiresFinalization || finalizedWith !== null)
  const toggle = (id: string) => setSelected(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id])
  const toggleStock = (id: string) => setStockIds(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id])
  const allSelected = rows.length > 0 && selected.length === rows.length
  const orderFacts = [
    ['Müşteri', order.customer],
    ['Sipariş tarihi', order.orderedAt ? new Date(order.orderedAt).toLocaleString('tr-TR') : null],
    ['Paket No', order.packageNumber],
    ['İade kodu', order.claimCode],
    ['İade sebebi', order.reason]
  ].filter((fact): fact is [string, string] => Boolean(fact[1]))

  return <Modal
    open={open}
    title="Onaylamak istediğiniz ürünleri seçiniz"
    onClose={onClose}
    className="return-approve-modal"
    footer={<>
      {stockRows.length > 0 && <span className="return-approve-stock-summary">Stoğa eklenecek: +{stockQuantity.toLocaleString('tr-TR')} adet</span>}
      <button type="button" className="secondary" onClick={onClose} disabled={approve.isPending}>Vazgeç</button>
      <button type="button" disabled={approve.isPending || loading || !canSubmit} onClick={() => approve.mutate()}>{approve.isPending ? 'Gönderiliyor…' : 'Onayla'}</button>
    </>}
  >
    <section className="return-approve-order" aria-label="Sipariş bilgileri">
      <span className="return-approve-order-id"><span className="return-approve-order-logo"><PlatformMark code={order.platformCode} name={order.platformName} /></span><strong>{order.orderNumber}</strong></span>
      <dl>{orderFacts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    </section>
    {loading ? <Busy text="İade ürünleri yükleniyor…" /> : error ? <ErrorBox error={error} /> : rows.length === 0 ? <p className="return-approve-empty">Bu iadede işlem bekleyen ürün kalmadı.</p> : <div className="return-approve-table" role="table">
      <div className="return-approve-head" role="row">
        <span role="columnheader"><label className="return-approve-check"><input type="checkbox" checked={allSelected} disabled={requiresFinalization} onChange={() => setSelected(allSelected ? [] : rows.map(row => row.id))} aria-label="Tüm ürünleri seç" /><span>Bilgiler</span></label></span>
        <span role="columnheader">Adet ve stok</span>
      </div>
      {rows.map(row => {
        const isSelected = selected.includes(row.id)
        const stockOn = isSelected && stockIds.includes(row.id) && row.hasInventoryMapping
        const facts = [['Stok Kodu', row.sku], ['Renk', row.color], ['Beden', row.size], ['Barkod', row.barcode], ['Model Kodu', row.modelCode]]
          .filter((fact): fact is [string, string] => Boolean(fact[1]))
        return <div className="return-approve-row" role="row" key={row.id}>
          <label className="return-approve-info" role="cell">
            <input type="checkbox" checked={isSelected} disabled={requiresFinalization} onChange={() => toggle(row.id)} aria-label={`${row.title} işleme dahil`} />
            <span className="reference-product-media return-approve-image">{row.imageUrl ? <img src={row.imageUrl} alt="" /> : <span className="reference-product-placeholder">↩</span>}<span className="quantity-bubble" role="img" aria-label={`${row.quantity} adet`} title={`${row.quantity} adet`}>{row.quantity}</span></span>
            <span className="return-approve-text"><strong>{row.title}</strong>{facts.map(([label, value]) => <small key={label}>{label}: {value}</small>)}</span>
          </label>
          <div className="return-approve-side" role="cell">
            <span className="return-approve-quantity">{row.quantity} Adet</span>
            <button type="button" role="switch" aria-checked={stockOn} disabled={!isSelected || !row.hasInventoryMapping} className={`return-approve-switch${stockOn ? ' is-on' : ''}`} onClick={() => toggleStock(row.id)} title={row.hasInventoryMapping ? undefined : 'Bu ürün yerel stok kaydıyla eşleşmiyor.'}>
              <i aria-hidden="true" />
              <span>Stoğa ekle</span>
            </button>
          </div>
        </div>
      })}
    </div>}
    {requiresFinalization && rows.length > 0 && <fieldset className="return-approve-kind"><legend>Kabul türü</legend><label><input type="radio" name="return-approve-kind" checked={finalizedWith === 'Refund'} onChange={() => setFinalizedWith('Refund')} /><span>İade</span></label><label><input type="radio" name="return-approve-kind" checked={finalizedWith === 'Change'} onChange={() => setFinalizedWith('Change')} /><span>Ürün değişimi</span></label><small>{marketplaceName} talebi tüm ürünler için birlikte kabul eder; kısmi ürün seçimi desteklenmez.</small></fieldset>}
  </Modal>
}
