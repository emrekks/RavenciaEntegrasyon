import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { RrError, RrIcon, RrLoading, RrLogo, RrModal, RrThumb } from './returns-ui'
import { integrationNotificationTarget, isExternalWriteMessage, type NotificationTarget } from '../../shared/notifications'
import { allocateStock } from './return-stock-allocation'
import { formatIstanbul } from '../../shared/format-date'
import { versionedIdempotencyKey } from '../../shared/idempotency'

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
    mutationFn: () => {
      const body = JSON.stringify({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], returnLineIds: chosenRows.map(row => row.id), finalizedWith: wholeClaim ? 'Refund' : null, stockLines: stockRequests })
      return hubApi<{ status: string }>(`/returns/${claimId}/actions`, { method: 'POST', headers: { 'Idempotency-Key': versionedIdempotencyKey('return-action', claimId, version, body), 'If-Match': `"v${version}"` }, body })
    },
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
    ['Sipariş Tarihi', order.orderedAt ? formatIstanbul(order.orderedAt, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : null],
    ['Paket No', order.packageNumber],
    ['İade Kodu', order.claimCode],
    ['İade Sebebi', order.reason]
  ].filter((fact): fact is [string, string] => Boolean(fact[1]))

  if (!open) return null
  return <RrModal
    crumb={`${order.platformName} İade`}
    title="Onaylamak İstediğiniz Ürünleri Seçiniz"
    size="lg"
    onClose={() => { if (!approve.isPending) onClose() }}
    foot={<>
      <span className="rr-note">{stockRequests.length > 0 && <>Stoğa Eklenecek: <b>+{stockQuantity.toLocaleString('tr-TR')} adet</b></>}</span>
      <button type="button" className="rr-btn" onClick={onClose} disabled={approve.isPending}>Vazgeç</button>
      <button type="button" className="rr-btn rr-primary" disabled={approve.isPending || loading || !canSubmit} onClick={() => approve.mutate()}>{approve.isPending ? 'Gönderiliyor…' : 'Onayla'}</button>
    </>}
  >
    <section className="rr-osum" aria-label="Sipariş bilgileri">
      <span><RrLogo code={order.platformCode} />{order.orderNumber}</span>
      <dl>{orderFacts.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    </section>
    {wholeClaim && <div className="rr-callout"><RrIcon name="info" /><span>Hepsiburada kararı bu talepteki tüm ürün satırlarına ücret iadesi olarak uygulanır.</span></div>}
    {loading ? <RrLoading text="İade ürünleri yükleniyor…" /> : error ? <RrError error={error} /> : groups.length === 0 ? <p className="rr-lead">Bu iadede işlem bekleyen ürün kalmadı.</p> : <div className="rr-apr" role="table">
      <div className="rr-apr-h" role="row">
        <span role="columnheader"><label><input type="checkbox" className="rr-cb" checked={allSelected} disabled={wholeClaim} onChange={() => setSelected(allSelected ? [] : groups.map(group => group.key))} aria-label="Tüm ürünleri seç" />Bilgiler</label></span>
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
        // Codes first (barcode, stock code, model code), then the variant (colour, size).
        const facts = [['Barkod', first.barcode], ['Stok Kodu', first.sku], ['Model Kodu', first.modelCode], ['Renk', first.color], ['Beden', first.size]]
          .filter((fact): fact is [string, string] => Boolean(fact[1]))
        return <div className="rr-apr-r" role="row" key={group.key}>
          <label className="rr-apr-info" role="cell">
            <input type="checkbox" className="rr-cb" checked={isSelected} disabled={wholeClaim} onChange={() => toggle(group.key)} aria-label={`${first.title} işleme dahil`} />
            <RrThumb src={first.imageUrl} quantity={unitCount(group.rows)} large alt={first.title} />
            <span className="rr-ptext"><b>{first.title}</b>{facts.map(([label, value]) => <small key={label}>{label}: {value}</small>)}</span>
          </label>
          <div className="rr-apr-side" role="cell">
            {canPickCount
              ? <select className="rr-in" aria-label={`${first.title} onaylanacak adet`} value={count} disabled={!isSelected} onChange={event => setCounts(current => ({ ...current, [group.key]: Number(event.target.value) }))}>{group.rows.map((_, index) => <option key={index + 1} value={index + 1}>{index + 1} Adet</option>)}</select>
              : <span className="rr-qty-static">{unitCount(picked)} Adet</span>}
            <button type="button" role="switch" aria-checked={stockOn} disabled={!isSelected || !mapped} className={`rr-switch${stockOn ? ' rr-on' : ''}`} onClick={() => toggleStock(group.key)} title={mapped ? undefined : 'Bu ürün yerel stok kaydıyla eşleşmiyor.'}>
              <i aria-hidden="true" />
              <span>Stoğa Ekle</span>
            </button>
            {stockOn && unitCount(picked) > 1 && <select className="rr-in" aria-label={`${first.title} stoğa eklenecek adet`} value={Math.max(1, Math.min(stockUnits[group.key] ?? unitCount(picked), unitCount(picked)))} onChange={event => setStockUnits(current => ({ ...current, [group.key]: Number(event.target.value) }))}>{Array.from({ length: unitCount(picked) }, (_, index) => <option key={index + 1} value={index + 1}>{index + 1} Adet Stoğa Ekle</option>)}</select>}
            {!mapped && <small style={{ color: 'var(--rr-subtle)', fontSize: 12 }}>Yerel stok kaydıyla eşleşmiyor.</small>}
          </div>
        </div>
      })}
    </div>}
  </RrModal>
}
