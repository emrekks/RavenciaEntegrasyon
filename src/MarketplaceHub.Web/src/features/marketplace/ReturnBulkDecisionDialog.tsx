import { useState } from 'react'
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { RrBadge, RrIcon, RrLogo, RrModal, RrThumb } from './returns-ui'
import { integrationNotificationTarget, isExternalWriteMessage, type NotificationTarget } from '../../shared/notifications'
import { allocateStock, totalUnits } from './return-stock-allocation'
import { formatIstanbul } from '../../shared/format-date'
import { versionedIdempotencyKey } from '../../shared/idempotency'

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
  connectionId: string | null
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
// The return lines (claim items) of a claim: the ids the stock request has to name.
type DetailLine = { id: string; orderLineId: string; quantity: number; remainingQuantity: number; status?: string | null; hasInventoryMapping: boolean; title: string; imageUrl: string | null }
type DetailLines = { lines: DetailLine[] | null }

type ProductGroup = { key: string; lines: { id: string; quantity: number }[]; units: number; mapped: boolean; display: BulkDecisionLine | undefined; fallbackTitle: string; fallbackImage: string | null }

type Props = {
  mode: 'APPROVE' | 'REJECT'
  items: BulkDecisionItem[]
  onClose: () => void
  onDone: (message: string, kind: 'success' | 'info' | 'error', details?: Array<{ label: string; value: string; tone?: 'success' | 'error' }>, target?: NotificationTarget | null) => void
}

const isActionable = (line: DetailLine) => !line.status || line.status.toUpperCase() === 'ACTION_REQUIRED'

// Sends one decision per selected return, one after another. Every return keeps its own idempotency
// key, so a retry can never decide the same claim twice, and one failure never stops the others.
export function ReturnBulkDecisionDialog({ mode, items: selectedItems, onClose, onDone }: Props) {
  const client = useQueryClient()
  // The list refreshes while the decisions run; keep the cards that were confirmed.
  const [items] = useState(selectedItems)
  const approving = mode === 'APPROVE'
  const [stockKeys, setStockKeys] = useState<string[]>([])
  const [stockUnits, setStockUnits] = useState<Record<string, number>>({})
  const [reasonId, setReasonId] = useState('')
  const [explanation, setExplanation] = useState('')
  const [running, setRunning] = useState(false)
  const [outcomes, setOutcomes] = useState<Outcome[]>([])
  const finished = !running && outcomes.length > 0
  const failed = outcomes.filter(outcome => !outcome.ok)
  const singlePlatform = new Set(items.map(item => item.platformCode.toUpperCase())).size === 1

  // Stock is chosen per product, so the claim details give the line ids and whether each product maps to stock.
  const details = useQueries({
    queries: items.map(item => ({
      queryKey: ['return', item.id],
      queryFn: () => hubApi<DetailLines>(`/returns/${item.id}`),
      enabled: approving,
      staleTime: 15_000
    }))
  })
  const groupsFor = (item: BulkDecisionItem, index: number): ProductGroup[] => {
    const lines = (details[index]?.data?.lines ?? []).filter(isActionable)
    const byProduct = new Map<string, DetailLine[]>()
    for (const line of lines) byProduct.set(line.orderLineId, [...(byProduct.get(line.orderLineId) ?? []), line])
    return Array.from(byProduct, ([orderLineId, group]) => {
      const sources = group.map(line => ({ id: line.id, quantity: line.remainingQuantity > 0 ? line.remainingQuantity : line.quantity }))
      return { key: `${item.id}:${orderLineId}`, lines: sources, units: totalUnits(sources), mapped: group.every(line => line.hasInventoryMapping), display: item.lines.find(line => line.id === orderLineId), fallbackTitle: group[0]!.title, fallbackImage: group[0]!.imageUrl }
    })
  }
  const allGroups = items.map((item, index) => groupsFor(item, index))
  const unitsFor = (group: ProductGroup) => Math.max(1, Math.min(stockUnits[group.key] ?? group.units, group.units))
  const stockTotal = allGroups.flat().filter(group => stockKeys.includes(group.key) && group.mapped).reduce((sum, group) => sum + unitsFor(group), 0)

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
    for (const [index, item] of items.entries()) {
      try {
        const stockLines = (allGroups[index] ?? []).filter(group => stockKeys.includes(group.key) && group.mapped).flatMap(group => allocateStock(group.lines, unitsFor(group)))
        const body = JSON.stringify(approving
          ? { action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [], finalizedWith: item.platformCode.toUpperCase() === 'HEPSIBURADA' ? 'Refund' : null, stockLines }
          : { action: 'REJECT', reasonCode: reasonId, explanation: explanation.trim(), evidenceAssetIds: [], finalizedWith: null })
        await hubApi(`/returns/${item.id}/actions`, {
          method: 'POST',
          headers: { 'Idempotency-Key': versionedIdempotencyKey('return-action', item.id, item.version, body), 'If-Match': `"v${item.version}"` },
          body
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
      ? `${okCount} iadenin ${approving ? 'kabul' : 'ret'} kararı pazar yerine iletildi.${approving && stockTotal > 0 ? ` Stok ${stockTotal.toLocaleString('tr-TR')} adet artırıldı.` : ''}`
      : `Toplu ${approving ? 'iade kabulünde' : 'iade retinde'} ${okCount} iade ${noun}, ${failCount} iade tamamlanamadı. Ayrıntılar bildirimde listelendi.`, failCount === 0 ? 'success' : 'error',
      [
        ...items.map(item => {
          const result = results.find(entry => entry.id === item.id)
          return { label: item.orderNumber, value: `${item.customerName} · ${item.platformName} · ${result?.ok ? (approving ? 'Onaylandı' : 'Reddedildi') : result?.message ?? 'Gönderilmedi'}`, tone: result?.ok ? 'success' as const : 'error' as const }
        }),
        ...(approving && stockTotal > 0 ? [{ label: 'Stok', value: `+${stockTotal.toLocaleString('tr-TR')} adet stoğa eklendi` }] : []),
        ...(!approving ? [{ label: 'Ret Nedeni', value: usableReasons.find(reason => reason.id === reasonId)?.name ?? reasonId }] : [])
      ], bulkTarget(results))
  }

  // Failures from a closed write switch lead to that integration; everything else to the matching returns tab.
  function bulkTarget(results: Outcome[]): NotificationTarget {
    const writeFailures = items.filter(item => results.some(result => result.id === item.id && !result.ok && isExternalWriteMessage(result.message)))
    if (writeFailures.length > 0) {
      const connections = new Set(writeFailures.map(item => item.connectionId))
      return connections.size === 1 ? integrationNotificationTarget(writeFailures[0]!.connectionId, writeFailures[0]!.platformName) : { href: '/integrations', label: 'Entegrasyonlara Git' }
    }
    return approving ? { href: '/returns?status=APPROVED', label: 'Onaylanan İadelere Git' } : { href: '/returns?status=REJECTED', label: 'Reddedilen İadelere Git' }
  }

  const toggleStock = (key: string) => setStockKeys(current => current.includes(key) ? current.filter(value => value !== key) : [...current, key])

  const mixedReject = !approving && !singlePlatform && !finished
  return <RrModal
    crumb="Aksiyon Bekleyen İadeler"
    title={approving ? 'Toplu İade Onayı' : 'Toplu İade Reddi'}
    desc={`${items.length} iade seçildi. ${approving ? 'Onayla' : 'Reddet'} dediğinizde her iade sırayla pazar yerine gönderilir.`}
    size={mixedReject ? 'md' : 'lg'}
    onClose={() => { if (!running) onClose() }}
    foot={finished
      ? <><span className="rr-note" /><button type="button" className="rr-btn rr-primary" onClick={onClose}>Kapat</button></>
      : <><span className="rr-note">{approving && stockTotal > 0 && <>Stoğa Eklenecek: <b>+{stockTotal.toLocaleString('tr-TR')} adet</b></>}</span><button type="button" className="rr-btn" onClick={onClose} disabled={running}>Vazgeç</button><button type="button" className={`rr-btn ${approving ? 'rr-primary' : 'rr-solid'}`} disabled={running || !canSubmit} onClick={() => void run()}>{running ? `Gönderiliyor… (${outcomes.length}/${items.length})` : approving ? `${items.length} İadeyi Onayla` : `${items.length} İadeyi Reddet`}</button></>}
  >
    {finished && <div className={`rr-callout${failed.length ? ' rr-danger' : ''}`}><RrIcon name={failed.length ? 'alert' : 'info'} /><span><b>{failed.length === 0 ? `${outcomes.length} iade ${approving ? 'onaylandı' : 'reddedildi'}.` : `${outcomes.length - failed.length} iade ${approving ? 'onaylandı' : 'reddedildi'}, ${failed.length} iade ${approving ? 'onaylanamadı' : 'reddedilemedi'}.`}</b> Ayrıntılar bildirimde listelendi.</span></div>}
    {mixedReject && <div className="rr-callout rr-danger"><RrIcon name="alert" /><span>Toplu ret için tek bir pazar yerinin iadelerini seçin. Ret nedenleri pazar yerine göre değişir.</span></div>}
    {!finished && !approving && singlePlatform && <>
      <div className="rr-dgrid">
        <label className="rr-fld"><small>Ret Nedeni</small>
          <select className="rr-in" value={reasonId} onChange={event => setReasonId(event.target.value)} disabled={running || reasons.isLoading}>
            <option value="">{reasons.isLoading ? 'Nedenler yükleniyor…' : 'Ret nedeni seçin'}</option>
            {usableReasons.map(reason => <option key={reason.id} value={reason.id}>{reason.name}</option>)}
          </select>
        </label>
        <label className="rr-fld rr-full"><small>Açıklama</small>
          <textarea className="rr-in" value={explanation} maxLength={500} rows={3} disabled={running} onChange={event => setExplanation(event.target.value)} placeholder="Müşteriye iletilecek ret açıklaması" />
          <em className="rr-num">{explanation.trim().length} / 500</em>
        </label>
      </div>
      {reasons.isError && <div className="rr-callout rr-danger"><RrIcon name="alert" /><span>Ret nedenleri alınamadı. Pencereyi kapatıp tekrar deneyin.</span></div>}
      {reasons.data && usableReasons.length === 0 && <div className="rr-callout rr-danger"><RrIcon name="alert" /><span>Bu iadeler için kanıt dosyası gerektirmeyen bir ret nedeni yok. Bu iadeleri tek tek reddedin.</span></div>}
      {usableReasons.length > 0 && <div className="rr-callout"><RrIcon name="info" /><span>Kanıt dosyası gerektiren nedenler toplu retle kullanılamaz, bu yüzden listede görünmez.</span></div>}
    </>}
    {!mixedReject && items.map((item, index) => {
      const outcome = outcomes.find(entry => entry.id === item.id)
      const groups = allGroups[index] ?? []
      const loadingDetail = approving && details[index]?.isLoading
      const stockLocked = running || finished
      const products = groups.length > 0
        ? groups.map(group => ({ group, line: group.display ?? { id: group.key, title: group.fallbackTitle, imageUrl: group.fallbackImage, quantity: group.units, sku: '', barcode: null, color: null, size: null, modelCode: null } }))
        : item.lines.map(line => ({ group: undefined, line }))
      const state = outcome ? (outcome.ok ? (approving ? 'Onaylandı' : 'Reddedildi') : null) : running ? 'Sırada' : 'Bekliyor'
      return <article className="rr-ocard" key={item.id}>
        <header>
          <RrLogo code={item.platformCode} />
          <div><b>{item.orderNumber}</b><span className="rr-osfacts"><span>Müşteri: <b>{item.customerName}</b></span>{item.orderedAt && <span>Sipariş Tarihi: <b className="rr-num">{formatIstanbul(item.orderedAt, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })}</b></span>}{item.packageNumber && <span>Paket No: <b className="rr-num">{item.packageNumber}</b></span>}</span></div>
          {state ? <RrBadge label={state} /> : <span className="rr-reason-tip" tabIndex={0}><RrBadge label="İşlem Tamamlanamadı" tone="bad" /><span className="rr-reason-pop" role="tooltip"><b>İşlem Tamamlanamadı</b>{outcome?.message}</span></span>}
        </header>
        <section>
          {products.map(({ group, line }) => {
            const facts = [['Barkod', line.barcode], ['Stok Kodu', line.sku], ['Model Kodu', line.modelCode], ['Renk', line.color], ['Beden', line.size]].filter((fact): fact is [string, string] => Boolean(fact[1]))
            const units = group?.units ?? line.quantity
            const stockOn = Boolean(group && stockKeys.includes(group.key) && group.mapped)
            return <div className="rr-bcard-p" key={group?.key ?? line.id}>
              <RrThumb src={line.imageUrl} quantity={units} large alt={line.title} />
              <span className="rr-ptext"><b>{line.title}</b>{facts.map(([label, value]) => <small key={label}>{label}: {value}</small>)}</span>
              {approving && <div className="rr-stockctl">
                <button type="button" role="switch" aria-checked={stockOn} disabled={stockLocked || !group || !group.mapped} className={`rr-switch${stockOn ? ' rr-on' : ''}`} onClick={() => group && toggleStock(group.key)} title={loadingDetail ? 'İade ayrıntıları yükleniyor…' : group && !group.mapped ? 'Bu ürün yerel stok kaydıyla eşleşmiyor.' : undefined}><i aria-hidden="true" /><span>Stoğa Ekle</span></button>
                {stockOn && group && group.units > 1 && <select className="rr-in" aria-label={`${line.title} stoğa eklenecek adet`} disabled={stockLocked} value={unitsFor(group)} onChange={event => setStockUnits(current => ({ ...current, [group.key]: Number(event.target.value) }))}>{Array.from({ length: group.units }, (_, number) => <option key={number + 1} value={number + 1}>{number + 1} Adet Stoğa Ekle</option>)}</select>}
              </div>}
            </div>
          })}
        </section>
      </article>
    })}
  </RrModal>
}
