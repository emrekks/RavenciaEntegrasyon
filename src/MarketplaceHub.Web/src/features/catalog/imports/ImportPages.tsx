// İçe aktarımlar (/imports, /imports/:id).
import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll, loadAllPages } from '../../../shared/api'
import { newIdempotencyKey, sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../../shared/idempotency'
import { ErrorToast } from '../../../shared/components/NoticeToast'
import { statusLabel } from '../../../shared/status-labels'
import { Page, Tag, Versioned } from '../shared'
import { importSessionPollInterval } from './import-status'

type ImportSession = Versioned & { sourceType: string; status: string; totalRows: number; validRows: number; errorRows: number; reviewRows: number; sourceAssetId: string | null }
type Candidate = Versioned & { matchRule: string; safeSummary: string; productId: string | null; variantId: string | null }

// Preview rows carry warnings (an unknown or ambiguous category, a switched-off brand) in the summary.
function candidateWarnings(safeSummary: string): string[] {
  try {
    const parsed: unknown = JSON.parse(safeSummary)
    if (!parsed || typeof parsed !== 'object' || !('warnings' in parsed) || !Array.isArray(parsed.warnings)) return []
    return parsed.warnings.filter((item): item is string => typeof item === 'string')
  } catch {
    return []
  }
}

function useImportTask() {
  const [error, setError] = useState<unknown>()
  const pending = useMutation({ mutationFn: (task: () => Promise<unknown>) => task(), onMutate: () => setError(undefined), onError: reason => setError(reason) })
  return { error, pending }
}

export function ImportsPage() {
  const client = useQueryClient()
  const { error, pending } = useImportTask()
  const query = useQuery({ queryKey: ['imports'], queryFn: () => loadAllPages<ImportSession>('/imports') })
  function create(sourceType: string) {
    if (pending.isPending) return
    pending.mutate(async () => {
      await hubApi('/imports', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ sourceType, connectionId: null }) })
      await client.invalidateQueries({ queryKey: ['imports'] })
    })
  }
  return <Page title="İçe Aktarımlar" eyebrow="Katalog" className="catalog-list-page" action={<div className="actions"><button type="button" disabled={pending.isPending} onClick={() => create('CSV')}>CSV Başlat</button><button type="button" disabled={pending.isPending} onClick={() => create('XLSX')}>XLSX Başlat</button></div>}>
    <ErrorToast title="İçe Aktarım Hatası" error={error ?? query.error} />
    {!query.data?.items.length ? <div className="empty">Henüz içe aktarım oturumu yok.</div> : <div className="table-wrap"><table><thead><tr><th>Kaynak</th><th>Durum</th><th>Satır</th><th></th></tr></thead><tbody>{query.data.items.map(item => <tr key={item.id}><td>{item.sourceType}</td><td><Tag>{statusLabel(item.status)}</Tag></td><td>{item.validRows}/{item.totalRows}</td><td><Link to={`/imports/${item.id}`}>İncele</Link></td></tr>)}</tbody></table></div>}
  </Page>
}

export function ImportDetailPage() {
  const { id } = useParams()
  const client = useQueryClient()
  const { error, pending } = useImportTask()
  // Re-read only while the worker moves the import on; a finished import is not polled.
  const session = useQuery({
    queryKey: ['import', id],
    queryFn: () => hubPoll<ImportSession>(`/imports/${id}`),
    enabled: !!id,
    refetchInterval: query => importSessionPollInterval(query.state.data?.status)
  })
  const candidates = useQuery({ queryKey: ['candidates', id], queryFn: () => loadAllPages<Candidate>(`/imports/${id}/candidates`), enabled: session.data?.status === 'REVIEW_REQUIRED' })
  function upload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pending.isPending) return
    const form = event.currentTarget
    pending.mutate(async () => {
      await hubApi(`/imports/${id}/source-file`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: new FormData(form) })
      await client.invalidateQueries({ queryKey: ['import', id] })
    })
  }
  function map(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pending.isPending || !session.data) return
    const version = session.data.version
    const data = new FormData(event.currentTarget)
    const headers = String(data.get('headers')).split(',').map(x => x.trim())
    const fields = String(data.get('fields')).split(',').map(x => x.trim())
    pending.mutate(async () => {
      await hubApi(`/imports/${id}/column-mapping`, { method: 'PUT', headers: { 'If-Match': `"v${version}"` }, body: JSON.stringify({ profileName: 'Manuel eşleme', variantGroupKey: null, mappings: headers.map((sourceColumn, sortOrder) => ({ sourceColumn, targetField: fields[sortOrder], sortOrder })) }) })
      await client.invalidateQueries({ queryKey: ['import', id] })
    })
  }
  function job(kind: 'preview' | 'apply') {
    if (pending.isPending) return
    // The same key for a double click or a resend after a lost answer; a fresh one after any HTTP error (G4).
    const versionedKey = versionedIdempotencyKey(`import-${kind}`, id ?? '', session.data?.version ?? 0)
    pending.mutate(async () => {
      await sendWithVersionedIdempotencyKey(versionedKey, key => hubApi(`/imports/${id}/${kind}-jobs`, { method: 'POST', headers: { 'Idempotency-Key': key } }))
      await client.invalidateQueries({ queryKey: ['import', id] })
    })
  }
  function decide(candidate: Candidate, decision: 'CREATE' | 'LINK' | 'SKIP') {
    if (pending.isPending) return
    pending.mutate(async () => {
      await hubApi(`/imports/${id}/decisions/${candidate.id}`, { method: 'PUT', headers: { 'If-Match': `"v${candidate.version}"` }, body: JSON.stringify({ decision, productId: decision === 'LINK' ? candidate.productId : null, variantId: decision === 'LINK' ? candidate.variantId : null }) })
      await client.invalidateQueries({ queryKey: ['candidates', id] })
      await client.invalidateQueries({ queryKey: ['import', id] })
    })
  }
  if (!session.data) return <Page title="İçe Aktarım" eyebrow="Katalog" className="catalog-list-page"><ErrorToast title="İçe Aktarım Yüklenemedi" error={session.error} /><p>Yükleniyor…</p></Page>
  const metrics: Array<[string, string | number]> = [['Durum', statusLabel(session.data.status)], ['Toplam', session.data.totalRows], ['Geçerli', session.data.validRows], ['Hatalı', session.data.errorRows]]
  return <Page title={`İçe Aktarım ${session.data.id.slice(0, 8)}`} eyebrow="Katalog" className="catalog-list-page">
    <div className="metrics">{metrics.map(([label, value]) => <article key={label}><small>{label}</small><strong>{value}</strong></article>)}</div>
    <ErrorToast title="İçe Aktarım İşlemi Başarısız" error={error} />
    {session.data.status === 'CREATED' && <div className="detail-grid">
      <form className="panel" onSubmit={upload}><h2>1. Dosya</h2><input name="file" type="file" accept=".csv,.xlsx" required /><button disabled={pending.isPending}>Yükle</button></form>
      <form className="panel" onSubmit={map}><h2>2. Kolon Eşleme</h2><label>Başlıklar<input name="headers" placeholder="Ürün,SKU,Barkod" required /></label><label>Hedefler<input name="fields" placeholder="title,sku,barcode" required /></label><button disabled={pending.isPending}>Kaydet</button></form>
    </div>}
    <div className="actions spaced">
      {session.data.status === 'CREATED' && session.data.sourceAssetId && <button type="button" disabled={pending.isPending} onClick={() => job('preview')}>Önizleme Oluştur</button>}
      {session.data.status === 'READY_TO_APPLY' && <button type="button" disabled={pending.isPending} onClick={() => job('apply')}>Kararları Uygula</button>}
      {session.data.errorRows > 0 && <a className="button-link secondary" href={`/api/v1/imports/${id}/errors.csv`}>Hataları İndir</a>}
    </div>
    {candidates.data?.items.map(candidate => <article className="candidate" key={candidate.id}><div><Tag>{candidate.matchRule}</Tag><code>{candidate.safeSummary}</code>{candidateWarnings(candidate.safeSummary).map(warning => <p className="rv-field-hint" role="note" key={warning}>{warning}</p>)}</div><div className="actions"><button type="button" disabled={pending.isPending} onClick={() => decide(candidate, 'CREATE')}>Yeni</button>{candidate.productId && <button type="button" disabled={pending.isPending} onClick={() => decide(candidate, 'LINK')}>Eşle</button>}<button type="button" className="secondary" disabled={pending.isPending} onClick={() => decide(candidate, 'SKIP')}>Atla</button></div></article>)}
  </Page>
}
