// Ayarlar › Veri › Paneli Tamamen Sıfırla: the tenant's records all go (orders, products, invoices, stock, finance, jobs
// and every log); the account, its users, the connections with their credentials and the settings stay. The server runs
// it as one job (PANEL_FACTORY_RESET) and lists its steps; the panel follows the job step by step.

export type PanelFactoryResetGroup = { key: string; label: string; description: string; section: string; requires: string[]; count: number }
export type PanelFactoryResetJob = { jobId: string; status: string; full: boolean; categories: string[]; steps: string[]; step: number; progressLabel: string | null; progressPercent: number | null; error: string | null; createdAt: string; completedAt: string | null }
export type PanelFactoryResetView = { groups: PanelFactoryResetGroup[]; total: number; steps: string[]; kept: string[]; confirmationWord: string; partialConfirmationWord: string; job: PanelFactoryResetJob | null }
export type PanelResetStepState = 'done' | 'running' | 'pending' | 'failed'

export const panelResetConfirmationWord = 'PANELİ SIFIRLA'

// The typed words count in Turkish capitals, with spaces at the ends and doubled ones ignored: "paneli sıfırla" matches,
// "PANELI SIFIRLA" (dotless I) does not. The server compares the same way.
export function normalizePanelResetConfirmation(text: string) {
  return text.trim().replace(/\s+/g, ' ').toLocaleUpperCase('tr-TR')
}

export const dataResetPartialConfirmationWord = 'Verileri sil'

// Ayarlar › Veri's confirmation word: "Verileri sil" for some data types, PANELİ SIFIRLA once every one is chosen (that
// is the full reset, which ends the other sessions; the server refuses the partial word for it).
export function dataResetConfirmWord(typeCount: number, pickedCount: number) {
  return typeCount > 0 && pickedCount >= typeCount ? panelResetConfirmationWord : dataResetPartialConfirmationWord
}

export function panelResetConfirmed(text: string, word = panelResetConfirmationWord) {
  return normalizePanelResetConfirmation(text) === normalizePanelResetConfirmation(word)
}

// A category chosen brings the ones it requires; one taken off takes off those that require it.
export function togglePanelResetCategories(groups: Pick<PanelFactoryResetGroup, 'key' | 'requires'>[], picked: string[], keys: string[], checked: boolean) {
  const next = new Set(picked)
  const queue = [...keys]
  while (queue.length) {
    const key = queue.shift()!
    if (checked) {
      if (next.has(key) && !keys.includes(key)) continue
      next.add(key)
      queue.push(...(groups.find(group => group.key === key)?.requires ?? []).filter(required => !next.has(required)))
    } else {
      next.delete(key)
      queue.push(...groups.filter(group => group.requires.includes(key) && next.has(group.key)).map(group => group.key))
    }
  }
  return groups.map(group => group.key).filter(key => next.has(key))
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
