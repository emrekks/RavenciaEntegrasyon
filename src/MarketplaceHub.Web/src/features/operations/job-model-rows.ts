// The jobs a list row stands for: a model row's cards (JobModelGroupRows), or the job itself. Kept apart from
// job-model-group.ts so the list's filters do not pull the lazily loaded model row into the operations chunk.
export function rowJobs<Job extends { modelGroup?: { cards: ReadonlyArray<{ job: unknown }> } | null }>(row: Job): Job[] {
  return row.modelGroup ? row.modelGroup.cards.map(card => card.job as Job) : [row]
}
