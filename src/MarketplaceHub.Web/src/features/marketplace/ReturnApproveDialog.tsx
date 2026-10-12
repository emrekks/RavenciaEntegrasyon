import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal } from '../../shared/components'
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

type Props = {
  open: boolean
  claimId: string
  version: number
  marketplaceName: string
  rows: ReturnApproveRow[]
  loading: boolean
  error: unknown
  // Hepsiburada decides the whole claim at once and asks whether it ends in a refund or an exchange.
  requiresFinalization?: boolean
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'info' | 'error') => void
}

// The approval dialog: pick the products to approve, optionally put them back in stock, confirm once.
export function ReturnApproveDialog({ open, claimId, version, marketplaceName, rows, loading, error, requiresFinalization = false, onClose, onDone }: Props) {
  const client = useQueryClient()
  const [selected, setSelected] = useState<string[]>([])
  const [addToStock, setAddToStock] = useState(false)
  const [finalizedWith, setFinalizedWith] = useState<'Refund' | 'Change' | null>(null)
  const rowKey = rows.map(row => row.id).join('|')

  useEffect(() => {
    setSelected(rows.map(row => row.id))
    setAddToStock(false)
    setFinalizedWith(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rowKey, open])

  const chosen = rows.filter(row => selected.includes(row.id))
  const stockBlocked = chosen.some(row => !row.hasInventoryMapping)
  const stockActive = addToStock && !stockBlocked
  const stockQuantity = chosen.reduce((sum, row) => sum + row.quantity, 0)

  const approve = useMutation({
    mutationFn: () => hubApi<{ status: string }>(`/returns/${claimId}/actions`, {
      method: 'POST',
      headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${version}"` },
      body: JSON.stringify({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], returnLineIds: chosen.map(row => row.id), finalizedWith: requiresFinalization ? finalizedWith : null, addToStock: stockActive })
    }),
    onSuccess: async () => {
      const stock = stockActive ? ` Stok ${stockQuantity.toLocaleString('tr-TR')} adet artırıldı.` : ''
      onDone(`İade kabulü ${marketplaceName}’a gönderildi.${stock}`, 'success')
      await client.invalidateQueries({ queryKey: ['return', claimId] })
      await client.invalidateQueries({ queryKey: ['returns'] })
      onClose()
    },
    onError: reason => onDone(reason instanceof Error ? reason.message : 'İade kararı başlatılamadı.', 'error')
  })

  const canSubmit = chosen.length > 0 && (!requiresFinalization || finalizedWith !== null)
  const toggle = (id: string) => setSelected(current => current.includes(id) ? current.filter(value => value !== id) : [...current, id])
  const allSelected = rows.length > 0 && selected.length === rows.length

  return <Modal
    open={open}
    title="Onaylamak istediğiniz ürünleri seçiniz"
    onClose={onClose}
    className="return-approve-modal"
    footer={<>
      <label className={`return-approve-stock${stockBlocked ? ' is-disabled' : ''}`} title={stockBlocked ? 'Seçilen ürünlerden biri yerel stok kaydıyla eşleşmiyor.' : undefined}>
        <input type="checkbox" checked={stockActive} disabled={stockBlocked || chosen.length === 0} onChange={event => setAddToStock(event.target.checked)} />
        <span>Stoğa ekle{stockActive && stockQuantity > 0 ? ` (+${stockQuantity.toLocaleString('tr-TR')})` : ''}</span>
      </label>
      <button type="button" className="secondary" onClick={onClose} disabled={approve.isPending}>Vazgeç</button>
      <button type="button" disabled={approve.isPending || loading || !canSubmit} onClick={() => approve.mutate()}>{approve.isPending ? 'Gönderiliyor…' : 'Onayla'}</button>
    </>}
  >
    {loading ? <Busy text="İade ürünleri yükleniyor…" /> : error ? <ErrorBox error={error} /> : rows.length === 0 ? <p className="return-approve-empty">Bu iadede işlem bekleyen ürün kalmadı.</p> : <div className="return-approve-table" role="table">
      <div className="return-approve-head" role="row">
        <span role="columnheader"><label className="return-approve-check"><input type="checkbox" checked={allSelected} disabled={requiresFinalization} onChange={() => setSelected(allSelected ? [] : rows.map(row => row.id))} aria-label="Tüm ürünleri seç" /><span>Bilgiler</span></label></span>
        <span role="columnheader">Adet</span>
      </div>
      {rows.map(row => {
        const isSelected = selected.includes(row.id)
        const facts = [['Stok Kodu', row.sku], ['Renk', row.color], ['Beden', row.size], ['Barkod', row.barcode], ['Model Kodu', row.modelCode]]
          .filter((fact): fact is [string, string] => Boolean(fact[1]))
          .map(([label, value]) => ({ label, value }))
        return <div className={`return-approve-row${isSelected ? ' is-selected' : ''}`} role="row" key={row.id}>
          <label className="return-approve-info" role="cell">
            <input type="checkbox" checked={isSelected} disabled={requiresFinalization} onChange={() => toggle(row.id)} aria-label={`${row.title} işleme dahil`} />
            <span className="reference-product-media return-approve-image">{row.imageUrl ? <img src={row.imageUrl} alt="" /> : <span className="reference-product-placeholder">↩</span>}<span className="quantity-bubble" role="img" aria-label={`${row.quantity} adet`} title={`${row.quantity} adet`}>{row.quantity}</span></span>
            <span className="return-approve-text"><strong>{row.title}</strong>{facts.map(fact => <small key={fact.label}>{fact.label}: {fact.value}</small>)}</span>
          </label>
          <span className="return-approve-quantity" role="cell"><span>{row.quantity} Adet</span></span>
        </div>
      })}
    </div>}
    {requiresFinalization && rows.length > 0 && <fieldset className="return-approve-kind"><legend>Kabul türü</legend><label><input type="radio" name="return-approve-kind" checked={finalizedWith === 'Refund'} onChange={() => setFinalizedWith('Refund')} /><span>İade</span></label><label><input type="radio" name="return-approve-kind" checked={finalizedWith === 'Change'} onChange={() => setFinalizedWith('Change')} /><span>Ürün değişimi</span></label><small>{marketplaceName} talebi tüm ürünler için birlikte kabul eder; kısmi ürün seçimi desteklenmez.</small></fieldset>}
  </Modal>
}
