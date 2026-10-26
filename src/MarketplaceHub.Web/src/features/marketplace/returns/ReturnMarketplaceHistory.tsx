import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { formatDateTime } from '../../../shared/format-date'
import { statusLabel } from '../../../shared/status-labels'
import { RrError, RrIcon, RrLoading } from '../returns-ui'

type SettledLine = { id: string; title: string; acceptDetail?: string | null; marketplaceNote?: string | null; resolved?: boolean | null; acceptedBySeller?: boolean | null }
type AuditEntry = { previousStatus: string | null; newStatus: string; executorApp: string | null; executorUser: string | null; at: string }
type LineAudit = { returnLineId: string; title: string; entries: AuditEntry[]; error: string | null }

// Trendyol's claim item statuses as the seller panel names them; anything else falls back to the shared labels.
const claimStatusNames: Record<string, string> = {
  CREATED: 'Oluşturuldu',
  WAITINGINACTION: 'Satıcı kararı bekleniyor',
  ACCEPTED: 'Kabul edildi',
  REJECTED: 'Reddedildi',
  UNRESOLVED: 'İhtilafta',
  INANALYSIS: 'Trendyol incelemesinde',
  CANCELLED: 'İptal edildi',
  WAITINGFRAUDCHECK: 'Trendyol kontrolünde'
}
export function claimStatusName(value: string | null | undefined) {
  if (!value?.trim()) return '—'
  return claimStatusNames[value.trim().toUpperCase().replace(/[\s_-]+/g, '')] ?? statusLabel(value)
}

// acceptDetail: who accepted the line. Unknown values are shown as Trendyol sent them.
export function acceptDetailText(value: string | null | undefined) {
  switch (value?.trim().toUpperCase()) {
    case undefined: case '': return null
    case 'SUPPLIER': return 'Satıcı onayladı'
    case 'DISPUTE': return 'İhtilaf sonucu kabul edildi'
    case 'SYSTEM': return 'Süre dolunca otomatik kabul edildi'
    default: return value!.trim()
  }
}

const yesNo = (value: boolean | null | undefined) => value == null ? null : value ? 'Evet' : 'Hayır'

// How Trendyol settled each line (from the return sync) and, on request, its status history read live from Trendyol.
// Read only: nothing here writes to the marketplace.
// showSettlement: false where the lines' own table already shows the settlement (the Detaylı Bilgi window).
export function ReturnMarketplaceHistory({ returnId, lines, showSettlement = true }: { returnId: string; lines: SettledLine[]; showSettlement?: boolean }) {
  const [historyOpen, setHistoryOpen] = useState(false)
  const audits = useQuery({ queryKey: ['return-audits', returnId], queryFn: () => hubApi<LineAudit[]>(`/returns/${returnId}/audits`), enabled: historyOpen, staleTime: 5 * 60_000 })
  const settled = lines.map(line => ({ line, facts: [
    ['Kabul Şekli', acceptDetailText(line.acceptDetail)],
    ['İhtilaf Çözüldü', yesNo(line.resolved)],
    ['Satıcı Onayı', yesNo(line.acceptedBySeller)],
    ['Trendyol Notu', line.marketplaceNote?.trim() || null]
  ].filter((fact): fact is [string, string] => Boolean(fact[1])) }))
  return <article className="rr-mhist">
    <div className="rr-h3">{showSettlement ? 'Trendyol Kararı ve Durum Geçmişi' : 'Trendyol Durum Geçmişi'}</div>
    {!showSettlement ? null : settled.some(entry => entry.facts.length) ? settled.filter(entry => entry.facts.length).map(({ line, facts }) => <div className="rr-mhist-line" key={line.id}>
      {lines.length > 1 && <b>{line.title}</b>}
      <dl className="rr-kv">{facts.map(([label, value]) => <div key={label} style={{ display: 'contents' }}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    </div>) : <p className="rr-mhist-none">Trendyol bu iade için henüz bir karar bilgisi göndermedi.</p>}
    {!historyOpen ? <button type="button" className="rr-btn" onClick={() => setHistoryOpen(true)}><RrIcon name="refresh" />Durum Geçmişini Getir</button>
      : audits.isLoading ? <RrLoading text="Durum geçmişi Trendyol’dan okunuyor…" />
      : audits.isError || !audits.data ? <RrError error={audits.error} onRetry={() => void audits.refetch()} />
      : audits.data.map(line => <div className="rr-mhist-line" key={line.returnLineId}>
        {audits.data.length > 1 && <b>{line.title}</b>}
        {line.error ? <p className="rr-mhist-none">{line.error}</p> : !line.entries.length ? <p className="rr-mhist-none">Trendyol bu kalem için durum geçmişi göndermedi.</p>
          : <ol className="rr-mhist-steps">{line.entries.map((entry, index) => <li key={`${entry.at}-${index}`}>
            <span className="rr-num">{formatDateTime(entry.at)}</span>
            <b>{entry.previousStatus ? `${claimStatusName(entry.previousStatus)} → ` : ''}{claimStatusName(entry.newStatus)}</b>
            {(entry.executorApp || entry.executorUser) && <small>{[entry.executorApp, entry.executorUser].filter(Boolean).join(' · ')}</small>}
          </li>)}</ol>}
      </div>)}
    <p className="rr-mhist-note">Ret talebinde gönderilen açıklama ve belgeler Trendyol API’sinde okunamıyor; yalnızca Trendyol satıcı panelinde görünür.</p>
  </article>
}
