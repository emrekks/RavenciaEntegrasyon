// İşlem Takibi's model rows (server: JobModelGroupPolicy, JobOperationsService.ModelGroups): the product jobs one send
// queued for the colour cards of one model come as one row that opens to list the cards. The row's state sums up its
// cards' states and never hides a failure: one failed card makes the whole row read as failed.
import type { ApiAnswer } from '../../shared/api/api-answer'
import { jobStatusLabel } from '../../shared/job-status-labels'
import { parseVariantOptionSignature } from '../catalog/variant-option-matching'
import { isContinuingJob } from './job-waiting'

type PageItem = ApiAnswer<'getJobsPage'>['items'][number]
export type JobModelGroup = NonNullable<PageItem['modelGroup']>
export type JobModelGroupCard = JobModelGroup['cards'][number]
type MemberState = { status: string; lastErrorCode: string | null }


type Bucket = 'success' | 'running' | 'error' | 'cancelled'
function bucket(job: MemberState): Bucket {
  if (job.status === 'SUCCEEDED') return 'success'
  if (job.status === 'CANCELLED') return 'cancelled'
  if (job.status === 'BLOCKED' || job.status === 'MANUAL_REVIEW' || job.status === 'DEAD') return 'error'
  return isContinuingJob(job) || ['PENDING', 'LEASED', 'RETRY_SCHEDULED'].includes(job.status) ? 'running' : 'error'
}

// A count and its word stay together (no-break space): a narrow column breaks the line only between counts.
const bucketWords: Record<Bucket, string> = { success: 'Başarılı', running: 'Sürüyor', error: 'Başarısız', cancelled: 'İptal' }
// The worst failure names the row: used-up attempts, then blocked, then waiting for a manual review.
const failureOrder = ['DEAD', 'BLOCKED', 'MANUAL_REVIEW']

export type JobModelGroupState = { tone: 'success' | 'running' | 'error' | 'neutral'; label: string; counts: string | null }

/** The row's one state: a failure wins, then work still running ("Sürüyor (5/7)", done of all), then all succeeded. */
export function jobModelGroupState(jobs: readonly MemberState[]): JobModelGroupState {
  const tally: Record<Bucket, number> = { success: 0, running: 0, error: 0, cancelled: 0 }
  for (const job of jobs) tally[bucket(job)] += 1
  const mixed = (Object.values(tally).filter(count => count > 0).length > 1)
  const counts = mixed ? (['success', 'running', 'error', 'cancelled'] as const).filter(key => tally[key] > 0).map(key => `${tally[key]}\u00a0${bucketWords[key]}`).join(' · ') : null
  if (tally.error > 0) {
    const worst = failureOrder.find(status => jobs.some(job => job.status === status)) ?? 'DEAD'
    return { tone: 'error', label: jobStatusLabel(worst), counts }
  }
  if (tally.running > 0) return { tone: 'running', label: `Sürüyor (${jobs.length - tally.running}/${jobs.length})`, counts }
  if (tally.cancelled === jobs.length) return { tone: 'neutral', label: jobStatusLabel('CANCELLED'), counts: null }
  if (tally.cancelled > 0) return { tone: 'neutral', label: 'Kısmen İptal Edildi', counts }
  return { tone: 'success', label: jobStatusLabel('SUCCEEDED'), counts: null }
}

const colourNames = new Set(['RENK', 'RENKLER', 'COLOR', 'COLORS', 'COLOUR', 'COLOURS'])
const webColourNames = new Set(['WEBCOLOR', 'WEBCOLOUR', 'WEBRENK'])
const optionName = (name: string) => name.replace(/[\s_-]+/g, '').toLocaleUpperCase('tr-TR')

/** The card's colour from its first variant's options (Renk before Web Color, as the catalog reads it); else its title. */
export function jobModelCardColour(card: Pick<JobModelGroupCard, 'optionSignature' | 'title'>) {
  const options = parseVariantOptionSignature(card.optionSignature ?? '')
  const colour = options.find(option => colourNames.has(optionName(option.name))) ?? options.find(option => webColourNames.has(optionName(option.name)))
  return colour?.value || card.title?.trim() || 'Renk Bilinmiyor'
}

/** "MZ900 · 7 Renk · 21 Varyant · Model Adı": code and counts first, so a narrow column cuts the name; the variant
 * count only when every card's payload said it. */
export function jobModelGroupLine(group: Pick<JobModelGroup, 'title' | 'modelCode' | 'cardCount' | 'variantCount'>) {
  return [group.modelCode?.trim(), `${group.cardCount} Renk`, group.variantCount != null ? `${group.variantCount} Varyant` : null, group.title?.trim()].filter(Boolean).join(' · ')
}
