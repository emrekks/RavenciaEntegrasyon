export type HepsiburadaValueSyncState = {
  allowsCustomValue: boolean | null | undefined
  snapshotMissing: boolean
  syncPending: boolean
  alreadyRequested: boolean
}

export function shouldAutoSyncHepsiburadaValues(state: HepsiburadaValueSyncState) {
  return state.allowsCustomValue === false
    && state.snapshotMissing
    && !state.syncPending
    && !state.alreadyRequested
}

export function isReferenceSyncJobTerminal(status: string | undefined) {
  return status === 'SUCCEEDED'
    || status === 'BLOCKED'
    || status === 'MANUAL_REVIEW'
    || status === 'DEAD'
    || status === 'CANCELLED'
}
