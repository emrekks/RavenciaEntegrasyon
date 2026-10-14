import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal } from '../../shared/components'
import { PlatformMark } from '../../shared/platform-mark'
import { integrationNotificationTarget, isExternalWriteMessage, type NotificationTarget } from '../../shared/notifications'
import { allocateStock } from './return-stock-allocation'
import './return-approve-dialog.css'

export type ReturnApproveRow = {
  id: string
  orderLineId: string
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
  connectionId: string | null
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
  // Hepsiburada decides the whole claim at once: every product is approved together as a refund.
  wholeClaim?: boolean
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'info' | 'error', details?: Array<{ label: string; value: string; tone?: 'success' | 'error' }>, target?: NotificationTarget | null) => void
}

// One product with every return line (claim item) the marketplace opened for it. A product returned
// three times has three lines, and the dropdown picks how many of them are approved.
type ProductGroup = { key: string; rows: ReturnApproveRow[] }

function groupByProduct(rows: ReturnApproveRow[]): ProductGroup[] {
  const groups = new Map<string, ReturnApproveRow[]>()
  for (const row of rows) groups.set(row.orderLineId || row.id, [...(groups.get(row.orderLineId || row.id) ?? []), row])
  return Array.from(groups, ([key, items]) => ({ key, rows: items }))
}

const unitCount = (rows: ReturnApproveRow[]) => rows.reduce((sum, row) => sum + row.quantity, 0)

// The approval dialog: order summary on top, then one framed row per product with a unit dropdown
// and its own "put back in stock" switch, and a single confirmation.
export function ReturnApproveDialog({ open, claimId, version, marketplaceName, order, rows, loading, error, wholeClaim = false, onClose, onDone }: Props) {
  const client = useQueryClient()
  const groups = useMemo(() => groupByProduct(rows), [rows])
  const [selected, setSelected] = useState<string[]>([])
  const [counts, setCounts] = useState<Record<string, number>>({})
  const [stockKeys, setStockKeys] = useState<string[]>([])
  // How many units of a product go back to stock (defaults to every approved unit).
  const [stockUnits, setStockUnits] = useState<Record<string, number>>({})
  const groupSignature = groups.map(group => `${group.key}:${group.rows.length}`).join('|')

  useEffect(() => {
    setSelected(groups.map(group => group.key))
    setCounts(Object.fromEntries(groups.map(group => [group.key, group.rows.length])))
    setStockKeys([])
    setStockUnits({})
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [groupSignature, open])

  // The lines that will actually be approved: the first N of each selected product.
  const chosenByGroup = groups
    .filter(group => selected.includes(group.key))
    .map(group => ({ group, rows: group.rows.slice(0, Math.max(1, Math.min(counts[group.key] ?? group.rows.length, group.rows.length))) }))
  const chosenRows = chosenByGroup.flatMap(entry => entry.rows)
  const stockEntries = chosenByGroup
    .filter(entry => stockKeys.includes(entry.group.key) && entry.rows.every(row => row.hasInventoryMapping))
    .map(entry => ({ key: entry.group.key, rows: entry.rows, units: Math.max(1, Math.min(stockUnits[entry.group.key] ?? unitCount(entry.rows), unitCount(entry.rows))) }))
  const stockRequests = stockEntries.flatMap(entry => allocateStock(entry.rows.map(row => ({ id: row.id, quantity: row.quantity })), entry.units))
  const stockQuantity = stockEntries.reduce((sum, entry) => sum + entry.units, 0)

  const approve = useMutation({
    mutationFn: () => hubApi<{ status: string }>(`/returns/${claimId}/actions`, {
      method: 'POST',
      headers: { 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"v${version}"` },
      body: JSON.stringify({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], returnLineIds: chosenRows.map(row => row.id), finalizedWith: wholeClaim ? 'Refund' : null, stockLines: stockRequests })
    }),
    onSuccess: async () => {
      const stock = stockRequests.length > 0 ? ` Stok ${stockQuantity.toLocaleString('tr-TR')} adet artırıldı.` : ''
      onDone(`${order.orderNumber} siparişinin iade kabul kararı ${marketplaceName}’a iletildi.${stock}`, 'success', [
        { label: 'Sipariş', value: `${order.orderNumber} · ${order.customer}`, tone: 'success' },
        ...chosenRows.map(row => ({ label: 'Ürün', value: `${row.title} (${row.quantity} adet)` })),
        ...(stockRequests.length > 0 ? [{ label: 'Stok', value: `+${stockQuantity.toLocaleString('tr-TR')} adet stoğa eklendi` }] : [])
      ], { href: `/returns?status=APPROVED&search=${encodeURIComponent(order.orderNumber.replace(/^#/, ''))}`, label: 'İadeyi Gör' })
      await client.invalidateQueries({ queryKey: ['return', claimId] })
      await client.invalidateQueries({ queryKey: ['returns'] })
      onClose()
    },
    onError: reason => {
      const message = reason instanceof Error ? reason.message : 'İade kararı başlatılamadı.'
      onDone(`${order.orderNumber} siparişinin iade kabul kararı ${marketplaceName}’a iletilemedi: ${message}`, 'error', [
        { label: 'Sipariş', value: `${order.orderNumber} · ${order.customer}` },
        { label: 'Entegrasyon', value: order.platformName },
        { label: 'Neden', value: message, tone: 'error' }
      ], isExternalWriteMessage(message) ? integrationNotificationTarget(order.connectionId, order.platformName) : { href: `/returns?search=${encodeURIComponent(order.orderNumber.replace(/^#/, ''))}`, label: 'İadeyi Gör' })
    }
  })

  const canSubmit = chosenRows.length > 0
  const toggle = (key: string) => setSelected(current => current.includes(key) ? current.filter(value => value !== key) : [...current, key])
  const toggleStock = (key: string) => setStockKeys(current => current.includes(key) ? current.filter(value => value !== key) : [...current, key])
  const allSelected = groups.length > 0 && selected.length === groups.length
  const orderFacts = [
    ['Müşteri', order.customer],
    ['Sipariş Tarihi', order.orderedAt ? new Date(order.orderedAt).toLocaleString('tr-TR') : null],
    ['Paket No', order.packageNumber],
    ['İade Kodu', order.claimCode],
    ['İade Sebebi', order.reason]
  ].filter((fact): fact is [string, string] => Boolean(fact[1]))

  return <Modal
    open={open}
    title="Onaylamak İstediğiniz Ürünleri Seçiniz"
    onClose={onClose}
    className="return-approve-modal"
    footer={<>
      {stockRequests.length > 0 && <span className="return-approve-stock-summary">Stoğa Eklenecek: +{stockQuantity.toLocaleString('tr-TR')} adet</span>}
      <button type="button" className="secondary" onClick={onClose} disabled={approve.isPending}>Vazgeç</button>
      <button type="button" disabled={approve.isPending || loading || !canSubmit} onClick={() => approve.mutate()}>{approve.isPending ? 'Gönderiliyor…' : 'Onayla'}</button>
    </>}
  >
    <section className="return-approve-order" aria-label="Sipariş bilgileri">
      <span className="return-approve-order-id"><span className="return-approve-order-logo"><PlatformMark code={order.platformCode} name={order.platformName} /></span><strong>{order.orderNumber}</strong></span>
      <dl>{orderFacts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    </section>
    {loading ? <Busy text="İade ürünleri yükleniyor…" /> : error ? <ErrorBox error={error} /> : groups.length === 0 ? <p className="return-approve-empty">Bu iadede işlem bekleyen ürün kalmadı.</p> : <div className="return-approve-table" role="table">
      <div className="return-approve-head" role="row">
        <span role="columnheader"><label className="return-approve-check"><input type="checkbox" checked={allSelected} disabled={wholeClaim} onChange={() => setSelected(allSelected ? [] : groups.map(group => group.key))} aria-label="Tüm ürünleri seç" /><span>Bilgiler</span></label></span>
        <span role="columnheader">Adet ve Stok</span>
      </div>
      {groups.map(group => {
        const first = group.rows[0]!
        const isSelected = selected.includes(group.key)
        const count = Math.max(1, Math.min(counts[group.key] ?? group.rows.length, group.rows.length))
        const picked = group.rows.slice(0, count)
        const mapped = picked.every(row => row.hasInventoryMapping)
        const stockOn = isSelected && stockKeys.includes(group.key) && mapped
        const canPickCount = group.rows.length > 1 && !wholeClaim
        const facts = [['Stok Kodu', first.sku], ['Renk', first.color], ['Beden', first.size], ['Barkod', first.barcode], ['Model Kodu', first.modelCode]]
          .filter((fact): fact is [string, string] => Boolean(fact[1]))
        return <div className="return-approve-row" role="row" key={group.key}>
          <label className="return-approve-info" role="cell">
            <input type="checkbox" checked={isSelected} disabled={wholeClaim} onChange={() => toggle(group.key)} aria-label={`${first.title} işleme dahil`} />
            <span className="reference-product-media return-approve-image">{first.imageUrl ? <img src={first.imageUrl} alt="" /> : <span className="reference-product-placeholder">↩</span>}<span className="quantity-bubble" role="img" aria-label={`${unitCount(group.rows)} adet`} title={`${unitCount(group.rows)} adet`}>{unitCount(group.rows)}</span></span>
            <span className="return-approve-text"><strong>{first.title}</strong>{facts.map(([label, value]) => <small key={label}>{label}: {value}</small>)}</span>
          </label>
          <div className="return-approve-side" role="cell">
            {canPickCount
              ? <select className="return-approve-quantity" aria-label={`${first.title} onaylanacak adet`} value={count} disabled={!isSelected} onChange={event => setCounts(current => ({ ...current, [group.key]: Number(event.target.value) }))}>{group.rows.map((_, index) => <option key={index + 1} value={index + 1}>{index + 1} Adet</option>)}</select>
              : <span className="return-approve-quantity">{unitCount(picked)} Adet</span>}
            <button type="button" role="switch" aria-checked={stockOn} disabled={!isSelected || !mapped} className={`return-approve-switch${stockOn ? ' is-on' : ''}`} onClick={() => toggleStock(group.key)} title={mapped ? undefined : 'Bu ürün yerel stok kaydıyla eşleşmiyor.'}>
              <i aria-hidden="true" />
              <span>Stoğa Ekle</span>
            </button>
            {stockOn && unitCount(picked) > 1 && <select className="return-approve-quantity" aria-label={`${first.title} stoğa eklenecek adet`} value={Math.max(1, Math.min(stockUnits[group.key] ?? unitCount(picked), unitCount(picked)))} onChange={event => setStockUnits(current => ({ ...current, [group.key]: Number(event.target.value) }))}>{Array.from({ length: unitCount(picked) }, (_, index) => <option key={index + 1} value={index + 1}>{index + 1} Adet Stoğa Ekle</option>)}</select>}
          </div>
        </div>
      })}
    </div>}
  </Modal>
}
