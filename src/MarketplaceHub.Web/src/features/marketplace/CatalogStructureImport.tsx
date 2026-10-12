import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, StatusBadge, UiIcon } from '../../shared/components'
import { PlatformMark } from '../../shared/platform-mark'
import { CatalogStructurePreview } from './CatalogStructurePreview'
import {
  canApplyCatalogStructure,
  catalogStructureNarrative,
  catalogStructureRunStats,
  catalogStructureRunStatusLabel,
  formatCatalogStructureRunTime,
  isCatalogStructureRunActive,
  type CatalogStructureRun
} from './catalog-structure-report'
import './catalog-structure-import.css'

type JobProgress = { job: { progressLabel?: string | null; progressPercent?: number | null } }

export type CatalogStructurePlatform = { code: string; label: string; available: boolean }

type Props = {
  platformCode: string
  connectionId: string
  platforms: CatalogStructurePlatform[]
  onSelectPlatform: (code: string) => void
  onNotice: (message: string) => void
}

// A header button that opens the launcher: choose the marketplace, start a read-only preview,
// then review it and apply it to the panel.
export function CatalogStructureImportButton({ platformCode, connectionId, platforms, onSelectPlatform, onNotice }: Props) {
  const client = useQueryClient()
  const [launcherOpen, setLauncherOpen] = useState(false)
  const [openRunId, setOpenRunId] = useState<string | null>(null)
  const [confirming, setConfirming] = useState(false)
  const appliedRunSeen = useRef<string | null>(null)
  const platform = platforms.find(item => item.code === platformCode)
  const platformLabel = platform?.label ?? 'Pazar yeri'

  const runs = useQuery({
    queryKey: ['catalog-structure-runs', connectionId],
    queryFn: () => hubApi<CatalogStructureRun[]>(`/connections/${connectionId}/catalog-structure-runs`),
    enabled: !!connectionId && !!platform,
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
      setConfirming(false)
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
  const latestDetail = useQuery({
    queryKey: ['catalog-structure-run', latest?.id],
    queryFn: () => hubApi<CatalogStructureRun>(`/catalog-structure-runs/${latest!.id}`),
    enabled: launcherOpen && latest?.status === 'COMPLETED'
  })
  const run = detail.data
  const report = run?.report ?? null
  const canApply = canApplyCatalogStructure(run, report)
  const closePreview = () => { setOpenRunId(null); setConfirming(false) }
  const openPreview = (id: string) => { setLauncherOpen(false); setOpenRunId(id) }

  return <>
    <button type="button" className="secondary structure-header-button" onClick={() => setLauncherOpen(true)} aria-haspopup="dialog" title="Pazar yerindeki ürünlerden kategori, seçenek ve özellik yapısını panele aktarın">
      <UiIcon name="download" size={16} />Platformdan Yapıyı Çek{active && <span className="structure-header-dot" aria-label="İşlem sürüyor" />}
    </button>

    <Modal
      open={launcherOpen}
      title="Platformdan Yapıyı Çek"
      description="Pazar yerindeki ürünlerden kategori, seçenek ve özellik yapısını panele aktarın."
      onClose={() => setLauncherOpen(false)}
      className="structure-launcher-modal"
      footer={<>
        <button type="button" className="secondary structure-launcher-close" onClick={() => setLauncherOpen(false)}>Kapat</button>
        {latest?.status === 'COMPLETED' && <button type="button" className="secondary" onClick={() => openPreview(latest.id)}>{latest.mode === 'PREVIEW' ? 'Önizlemeyi aç' : 'Raporu aç'}</button>}
        <button type="button" disabled={!connectionId || active || start.isPending} onClick={() => start.mutate('PREVIEW')}>{active ? 'Çalışıyor…' : latest ? 'Yeni önizleme' : 'Önizleme başlat'}</button>
      </>}
    >
      <div className="structure-launcher">
        <section aria-label="Pazar yeri">
          <h3>Pazar yeri</h3>
          <div className="structure-platform-tabs" role="tablist" aria-label="Pazar yeri">
            {platforms.map(item => <button key={item.code} type="button" role="tab" aria-selected={item.code === platformCode} className={item.code === platformCode ? 'active' : undefined} disabled={!item.available} title={item.available ? `${item.label} ürünlerini tara` : `${item.label} için aktif bağlantı yok`} onClick={() => onSelectPlatform(item.code)}>
              <span className="structure-platform-logo"><PlatformMark code={item.code} name={item.label} /></span>
              <small>{item.available ? 'Aktif bağlantı' : 'Bağlantı yok'}</small>
            </button>)}
          </div>
        </section>

        <ol className="structure-steps" aria-label="Nasıl çalışır">
          <li><b>1</b><div><strong>Önizleme</strong><span>Ürünleriniz okunur. Panele ve pazar yerine hiçbir şey yazılmaz.</span></div></li>
          <li><b>2</b><div><strong>İnceleme</strong><span>Hangi kategori, özellik ve değerin ekleneceğini görür, değişiklikleri filtrelersiniz.</span></div></li>
          <li><b>3</b><div><strong>Uygulama</strong><span>Onayınızla tek işlemde panele yazılır; hata olursa hiçbir şey değişmez.</span></div></li>
        </ol>

        <section aria-label="Son işlem">
          <h3>Son işlem · {platformLabel}</h3>
          {!connectionId
            ? <p className="structure-import-hint">{platformLabel} bağlantısı hazırlanıyor…</p>
            : runs.isLoading ? <Busy />
            : !latest ? <p className="structure-run-empty">Henüz önizleme çalıştırılmadı.</p>
            : <div className="structure-run-card" role="status">
              <div className="structure-run-head">
                <StatusBadge tone={latest.status === 'FAILED' ? 'danger' : latest.status === 'COMPLETED' ? 'success' : 'info'}>{catalogStructureRunStatusLabel(latest)}</StatusBadge>
                <small>{formatCatalogStructureRunTime(latest.completedAt ?? latest.createdAt)}</small>
              </div>
              {active && <div className="structure-run-progress">
                <div className="structure-progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress.data?.job.progressPercent ?? undefined}><span style={{ width: `${progress.data?.job.progressPercent ?? 8}%` }} /></div>
                <small>{progress.data?.job.progressLabel ?? 'İşlem sırada…'}{progress.data?.job.progressPercent != null ? ` · %${progress.data.job.progressPercent}` : ''}</small>
              </div>}
              {latest.status === 'FAILED' && <p className="structure-import-error">{latest.errorSummary ?? 'İşlem tamamlanamadı.'}</p>}
              {latest.status === 'COMPLETED' && latestDetail.data?.report && <dl className="structure-run-stats">{catalogStructureRunStats(latestDetail.data.report).map(item => <div key={item.label}><dt>{item.label}</dt><dd>{item.value.toLocaleString('tr-TR')}</dd></div>)}</dl>}
            </div>}
        </section>
      </div>
    </Modal>

    <Modal
      open={!!openRunId}
      title={run?.mode === 'APPLY' ? `${platformLabel} aktarım raporu` : `${platformLabel} yapı önizlemesi`}
      description={run?.mode === 'APPLY' ? 'Aşağıdakiler panele yazıldı.' : 'Henüz hiçbir şey panele yazılmadı. Onaylarsanız aşağıdakiler uygulanır.'}
      onClose={closePreview}
      className="structure-import-modal"
      footer={confirming && report
        ? <><span className="structure-preview-confirm">Bu işlem panele yazar. Devam edilsin mi?</span><button type="button" className="secondary" onClick={() => setConfirming(false)}>Vazgeç</button><button type="button" disabled={start.isPending || active} onClick={() => start.mutate('APPLY')}>Evet, panele uygula</button></>
        : <><button type="button" className="secondary" onClick={closePreview}>Kapat</button>{canApply && <button type="button" disabled={start.isPending || active} onClick={() => setConfirming(true)}>Panele uygula</button>}</>}
    >
      {detail.isLoading && <Busy />}
      {detail.isError && <ErrorBox error={detail.error} />}
      {report && <>
        {confirming && <ul className="structure-preview-confirm-list">{catalogStructureNarrative(report, platformLabel).map(line => <li key={line}>{line}</li>)}</ul>}
        <CatalogStructurePreview report={report} platformLabel={platformLabel} />
      </>}
    </Modal>
  </>
}
