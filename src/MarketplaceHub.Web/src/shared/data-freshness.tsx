export type FreshnessKind = 'ORDERS' | 'RETURNS' | 'QUESTIONS'

export type InitialImportPart = { kind: FreshnessKind; state: 'DONE' | 'RUNNING' | 'QUEUED'; percent: number | null }
export type InitialImportView = { connectionId: string; running: boolean; parts: InitialImportPart[] }
const kindLabels: Record<FreshnessKind, string> = { ORDERS: 'Siparişler', RETURNS: 'İadeler', QUESTIONS: 'Sorular' }

// "İlk aktarım: Siparişler %60 · İadeler tamam · Sorular sırada"
export function initialImportSummary(parts: InitialImportPart[]): string {
  const states = parts.map(part => `${kindLabels[part.kind]} ${part.state === 'DONE' ? 'tamam' : part.percent != null ? `%${part.percent}` : part.state === 'RUNNING' ? 'aktarılıyor' : 'sırada'}`)
  return `İlk aktarım: ${states.join(' · ')}`
}
