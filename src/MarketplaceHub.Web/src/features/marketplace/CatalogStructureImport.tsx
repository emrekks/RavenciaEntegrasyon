import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, StatusBadge, UiIcon } from '../../shared/components'
import {
  canApplyCatalogStructure,
  catalogStructureAttributeRoleLabel,
  catalogStructureAttributeStatus,
  catalogStructureCategoryNeedsAttention,
  catalogStructureNewValueCount,
  catalogStructureDecisionLabel,
  catalogStructureDecisionTone,
  catalogStructureSummary,
  isCatalogStructureRunActive,
  type CatalogStructureRun
} from './catalog-structure-report'
import './catalog-structure-import.css'

type JobProgress = { job: { progressLabel?: string | null; progressPercent?: number | null } }

export function CatalogStructureImportCard({ connectionId, platformLabel, onNotice }: { connectionId: string; platformLabel: string; onNotice: (message: string) => void }) {
  const client = useQueryClient()
  const [openRunId, setOpenRunId] = useState<string | null>(null)
  const appliedRunSeen = useRef<string | null>(null)
  const runs = useQuery({
    queryKey: ['catalog-structure-runs', connectionId],
    queryFn: () => hubApi<CatalogStructureRun[]>(`/connections/${connectionId}/catalog-structure-runs`),
    enabled: !!connectionId,
    refetchInterval: query => (isCatalogStructureRunActive(query.state.data?.[0]) ? 3000 : false)
  })
  const latest = runs.data?.[0]
  const active = isCatalogStructureRunActive(latest)
  const progress = useQuery({
    queryKey: ['catalog-structure-job', latest?.jobId],
    queryFn: () => hubApi<JobProgress>(`/jobs/${latest!.jobId}`),
    enabled: active && !!latest?.jobId,
    refetchInterval: 3000
  })
  const start = useMutation({
    mutationFn: (mode: 'PREVIEW' | 'APPLY') => hubApi<CatalogStructureRun>(`/connections/${connectionId}/catalog-structure-runs?mode=${mode}`, { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: '{}' }),
    onSuccess: async (_run, mode) => {
      setOpenRunId(null)
      onNotice(mode === 'PREVIEW' ? `${platformLabel} yapı önizlemesi kuyruğa alındı. Panele hiçbir şey yazılmaz.` : `${platformLabel} yapısı panele aktarılmak üzere kuyruğa alındı.`)
      await client.invalidateQueries({ queryKey: ['catalog-structure-runs', connectionId] })
    },
    onError: reason => onNotice(reason instanceof Error ? reason.message : 'Yapı çekme başlatılamadı.')
  })

  // An applied run changes categories, attributes and mappings, so refresh them once.
  useEffect(() => {
    if (!latest || latest.mode !== 'APPLY' || latest.status !== 'COMPLETED' || appliedRunSeen.current === latest.id) return
    appliedRunSeen.current = latest.id
    for (const key of [['categories', 'mapping'], ['attributes', 'mapping-builder'], ['category-mappings', connectionId]]) void client.invalidateQueries({ queryKey: key })
  }, [latest, client, connectionId])

  const detail = useQuery({
    queryKey: ['catalog-structure-run', openRunId],
    queryFn: () => hubApi<CatalogStructureRun>(`/catalog-structure-runs/${openRunId}`),
    enabled: !!openRunId
  })
  const run = detail.data
  const report = run?.report ?? null

  return <article className="mapping-reference-card structure-import-card">
    <div className="mapping-section-heading"><div><h2><UiIcon name="download" />Platformdan Yapıyı Çek</h2><p>{platformLabel} mağazasındaki ürünlerin kategori, seçenek ve özellik yapısını panele aktarın. Pazar yerine hiçbir şey yazılmaz; önce önizleme gösterilir.</p></div>
      <button type="button" className="secondary" disabled={!connectionId || active || start.isPending} onClick={() => start.mutate('PREVIEW')}>{active ? 'Çalışıyor…' : 'Önizleme başlat'}</button></div>
    {runs.isLoading && <Busy />}
    {latest && <div className="structure-import-status" role="status">
      <StatusBadge tone={latest.status === 'FAILED' ? 'danger' : latest.status === 'COMPLETED' ? 'success' : 'info'}>{latest.mode === 'PREVIEW' ? 'Önizleme' : 'Uygulama'} · {latest.status === 'COMPLETED' ? 'tamamlandı' : latest.status === 'FAILED' ? 'başarısız' : latest.status === 'RUNNING' ? 'çalışıyor' : 'sırada'}</StatusBadge>
      {active && <span>{progress.data?.job.progressLabel ?? 'İşlem sırada…'}{progress.data?.job.progressPercent != null ? ` · %${progress.data.job.progressPercent}` : ''}</span>}
      {latest.status === 'FAILED' && <span className="structure-import-error">{latest.errorSummary ?? 'İşlem tamamlanamadı.'}</span>}
      {latest.status === 'COMPLETED' && <button type="button" className="secondary" onClick={() => setOpenRunId(latest.id)}>{latest.mode === 'PREVIEW' ? 'Önizlemeyi aç' : 'Raporu aç'}</button>}
    </div>}
    <Modal
      open={!!openRunId}
      title={run?.mode === 'APPLY' ? 'Yapı aktarım raporu' : 'Yapı önizlemesi'}
      description={run?.mode === 'APPLY' ? 'Aşağıdakiler panele yazıldı.' : 'Henüz hiçbir şey panele yazılmadı. Onaylarsanız aşağıdakiler uygulanır.'}
      onClose={() => setOpenRunId(null)}
      className="structure-import-modal"
      footer={<><button type="button" className="secondary" onClick={() => setOpenRunId(null)}>Kapat</button>{canApplyCatalogStructure(run, report) && <button type="button" disabled={start.isPending || active} onClick={() => start.mutate('APPLY')}>Panele uygula</button>}</>}
    >
      {detail.isLoading && <Busy />}
      {detail.isError && <ErrorBox error={detail.error} />}
      {report && <>
        <dl className="structure-import-totals">{catalogStructureSummary(report.totals, report.mode).map(item => <div key={item.label}><dt>{item.label}</dt><dd>{item.value.toLocaleString('tr-TR')}</dd></div>)}</dl>
        {report.warnings.map(warning => <p key={warning} className="structure-import-warning" role="alert">{warning}</p>)}
        {report.totals.productsWithoutCategory > 0 && <p className="structure-import-warning">{report.totals.productsWithoutCategory.toLocaleString('tr-TR')} ürün kategori bilgisi içermediği için kapsam dışı kaldı.</p>}
        {report.review.length > 0 && <section aria-label="İnceleme gerekenler"><h3>İnceleme gerekenler</h3><ul className="structure-import-review">{report.review.map(item => <li key={`${item.subject}:${item.reason}`}><strong>{item.subject}</strong><span>{item.reason}</span></li>)}</ul></section>}
        <section aria-label="Kategoriler"><h3>Kategoriler</h3>
          <p className="structure-import-hint">Değişiklik yapacak kategoriler açık gösterilir; zaten uyumlu olanları başlığına tıklayarak açabilirsiniz.</p>
          <div className="structure-import-categories">{report.categories.map(category => <details key={category.externalId} open={catalogStructureCategoryNeedsAttention(category)}>
            <summary className="structure-import-category-head"><strong>{category.remoteName}</strong><span aria-hidden="true">→</span><strong>{category.panelName || '—'}</strong><StatusBadge tone={catalogStructureDecisionTone(category.decision)}>{catalogStructureDecisionLabel(category.decision)}</StatusBadge><small>{category.productCount.toLocaleString('tr-TR')} ürün · {category.attributes.length} özellik</small></summary>
            <p>{category.reason}</p>
            {category.attributes.length > 0
              ? <table className="structure-import-attribute-table"><thead><tr><th>Özellik</th><th>Türü</th><th>Durum</th><th>Eklenecek değerler</th></tr></thead><tbody>{category.attributes.map(attribute => <tr key={`${attribute.remoteName}:${attribute.panelName}`}>
                <td><strong>{attribute.panelName}</strong>{attribute.remoteName !== attribute.panelName && <small>Pazar yerinde: {attribute.remoteName}</small>}</td>
                <td>{catalogStructureAttributeRoleLabel(attribute.role)}</td>
                <td>{catalogStructureAttributeStatus(attribute)}</td>
                <td>{catalogStructureNewValueCount(attribute) > 0 ? <span className="structure-import-values">{(attribute.newValues ?? []).slice(0, 24).map(value => <span key={value}>{value}</span>)}{catalogStructureNewValueCount(attribute) > 24 && <small>+{catalogStructureNewValueCount(attribute) - 24} değer daha</small>}</span> : <small>{attribute.usedValueCount ? `Ürünlerdeki ${attribute.usedValueCount} değer panelde mevcut` : '—'}</small>}</td>
              </tr>)}</tbody></table>
              : <p className="structure-import-hint">Bu kategoride ürünlerin kullandığı özellik bulunamadı.</p>}
            {(category.unmatchedFields?.length ?? 0) > 0 && <p className="structure-import-hint">Ürünlerde olup kategori özelliğiyle eşleşmeyen alanlar: {category.unmatchedFields!.join(", ")}</p>}
          </details>)}</div>
        </section>
      </>}
    </Modal>
  </article>
}
