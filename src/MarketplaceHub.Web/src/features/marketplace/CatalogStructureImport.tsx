import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Busy, ErrorBox, Modal, StatusBadge, UiIcon } from '../../shared/components'
import { CatalogStructurePreview } from './CatalogStructurePreview'
import {
  canApplyCatalogStructure,
  catalogStructureNarrative,
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
      description="Pazar yerindeki ürünlerin kategori, seçenek ve özellik yapısını panele aktarın. Pazar yerine hiçbir şey yazılmaz; önce önizleme gösterilir."
      onClose={() => setLauncherOpen(false)}
      className="structure-launcher-modal"
      footer={<button type="button" className="secondary" onClick={() => setLauncherOpen(false)}>Kapat</button>}
    >
      <div className="structure-platform-tabs" role="tablist" aria-label="Pazar yeri">
        {platforms.map(item => <button key={item.code} type="button" role="tab" aria-selected={item.code === platformCode} className={item.code === platformCode ? 'active' : undefined} disabled={!item.available} title={item.available ? `${item.label} ürünlerini tara` : `${item.label} için aktif bağlantı yok`} onClick={() => onSelectPlatform(item.code)}>{item.label}</button>)}
      </div>
      {!connectionId
        ? <p className="structure-import-hint">{platformLabel} bağlantısı hazırlanıyor…</p>
        : <div className="structure-launcher-body">
          <div className="structure-launcher-actions">
            <button type="button" disabled={active || start.isPending} onClick={() => start.mutate('PREVIEW')}>{active ? 'Çalışıyor…' : 'Önizleme başlat'}</button>
            {runs.isLoading && <Busy />}
          </div>
          {latest && <div className="structure-import-status" role="status">
            <StatusBadge tone={latest.status === 'FAILED' ? 'danger' : latest.status === 'COMPLETED' ? 'success' : 'info'}>{latest.mode === 'PREVIEW' ? 'Önizleme' : 'Uygulama'} · {latest.status === 'COMPLETED' ? 'tamamlandı' : latest.status === 'FAILED' ? 'başarısız' : latest.status === 'RUNNING' ? 'çalışıyor' : 'sırada'}</StatusBadge>
            {active && <span>{progress.data?.job.progressLabel ?? 'İşlem sırada…'}{progress.data?.job.progressPercent != null ? ` · %${progress.data.job.progressPercent}` : ''}</span>}
            {latest.status === 'FAILED' && <span className="structure-import-error">{latest.errorSummary ?? 'İşlem tamamlanamadı.'}</span>}
            {latest.status === 'COMPLETED' && <button type="button" className="secondary" onClick={() => openPreview(latest.id)}>{latest.mode === 'PREVIEW' ? 'Önizlemeyi aç' : 'Raporu aç'}</button>}
          </div>}
        </div>}
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
