// Ayarlar › Veri › Paneli Tamamen Sıfırla: the tenant's records all go (orders, products, invoices, stock, finance, jobs
// and every log); the account, its users, the connections with their credentials and the settings stay. The server runs
// it as one job (PANEL_FACTORY_RESET) and lists its steps; the panel follows the job step by step.

export type PanelFactoryResetGroup = { key: string; label: string; count: number }
export type PanelFactoryResetJob = { jobId: string; status: string; step: number; progressLabel: string | null; progressPercent: number | null; error: string | null; createdAt: string; completedAt: string | null }
export type PanelFactoryResetView = { groups: PanelFactoryResetGroup[]; total: number; steps: string[]; kept: string[]; confirmationWord: string; job: PanelFactoryResetJob | null }
export type PanelResetStepState = 'done' | 'running' | 'pending' | 'failed'

export const panelResetConfirmationWord = 'PANELİ SIFIRLA'

// The typed word must match exactly, the Turkish İ included ("PANELI SIFIRLA" and lower case do not open the button).
export function panelResetConfirmed(text: string, word = panelResetConfirmationWord) {
  return text.trim() === word
}

export function panelResetRunning(status: string | undefined | null) {
  return status === 'PENDING' || status === 'LEASED' || status === 'RETRY_SCHEDULED'
}

export function panelResetFailed(status: string | undefined | null) {
  return status === 'DEAD' || status === 'BLOCKED' || status === 'MANUAL_REVIEW' || status === 'CANCELLED'
}

// Each step's state: the ones before the job's step are done, its own runs (or failed), the rest wait; a finished job
// has every step done.
export function panelResetStepStates(steps: string[], job: PanelFactoryResetJob | null | undefined): PanelResetStepState[] {
  if (!job) return steps.map(() => 'pending')
  if (job.status === 'SUCCEEDED') return steps.map(() => 'done')
  const current = Math.max(0, Math.min(job.step, steps.length - 1))
  const failed = panelResetFailed(job.status)
  return steps.map((_, index) => index < current ? 'done' : index === current ? (failed ? 'failed' : 'running') : 'pending')
}
