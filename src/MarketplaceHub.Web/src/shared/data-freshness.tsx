import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { hubApi } from './api'

export type FreshnessKind = 'ORDERS' | 'RETURNS' | 'QUESTIONS'
type FreshnessPart = { kind: FreshnessKind; lastSuccessAt: string | null; instant: boolean; connections: number }
type FreshnessView = { parts: FreshnessPart[] }

// "35 sn önce", "4 dk önce", "2 sa önce": how long ago the stores were last read.
export function freshnessAge(lastSuccessAt: string, now: number): string {
  const seconds = Math.max(0, Math.round((now - new Date(lastSuccessAt).getTime()) / 1000))
  if (seconds < 60) return `${Math.max(seconds, 1)} sn önce`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} dk önce`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours} sa önce`
  return `${Math.floor(hours / 24)} gün önce`
}

export function freshnessText(part: FreshnessPart | undefined, now: number): string | null {
  if (!part?.lastSuccessAt || part.connections === 0) return null
  return `Son güncelleme ${freshnessAge(part.lastSuccessAt, now)}${part.instant ? ' · Webhook açık' : ''}`
}

// One line under a page title telling when its data was last read from the marketplaces.
export function DataFreshnessNote({ kind }: { kind: FreshnessKind }) {
  const query = useQuery({ queryKey: ['data-freshness'], queryFn: () => hubApi<FreshnessView>('/data-freshness'), refetchInterval: 30_000, staleTime: 15_000 })
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 10_000)
    return () => window.clearInterval(timer)
  }, [])
  const text = freshnessText(query.data?.parts.find(part => part.kind === kind), now)
  if (!text) return null
  return <span className="rv-freshness" role="status">{text}</span>
}

export type InitialImportPart = { kind: FreshnessKind; state: 'DONE' | 'RUNNING' | 'QUEUED'; percent: number | null }
export type InitialImportView = { connectionId: string; running: boolean; parts: InitialImportPart[] }
const kindLabels: Record<FreshnessKind, string> = { ORDERS: 'Siparişler', RETURNS: 'İadeler', QUESTIONS: 'Sorular' }

// "İlk aktarım: Siparişler %60 · İadeler tamam · Sorular sırada"
export function initialImportSummary(parts: InitialImportPart[]): string {
  const states = parts.map(part => `${kindLabels[part.kind]} ${part.state === 'DONE' ? 'tamam' : part.percent != null ? `%${part.percent}` : part.state === 'RUNNING' ? 'aktarılıyor' : 'sırada'}`)
  return `İlk aktarım: ${states.join(' · ')}`
}
