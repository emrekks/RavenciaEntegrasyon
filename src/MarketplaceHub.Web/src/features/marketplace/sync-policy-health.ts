export type SyncPolicyHealthInput = {
  enabled: boolean
  lastSuccessAt?: string | null
  healthStatus?: string | null
  cursorProgressStatus?: string | null
}

export type SyncPolicyHealthPresentation = {
  label: string
  tone: 'disabled' | 'paused' | 'healthy' | 'delayed' | 'degraded' | 'offline' | 'stalled' | 'unknown'
  stalled: boolean
}

/**
 * The enabled flows an integration card warns about: late past their window after an earlier success (OFFLINE) or
 * partly failing (DEGRADED). A flow that has not run yet waits; a closed or paused one says nothing.
 */
export function flowsNeedingAttention<T extends SyncPolicyHealthInput>(policies: readonly T[]): T[] {
  return policies.filter(policy => {
    if (!policy.enabled) return false
    const health = policy.healthStatus?.trim().toUpperCase()
    return health === 'DEGRADED' || (health === 'OFFLINE' && !!policy.lastSuccessAt)
  })
}

export function syncPolicyHealthPresentation(
  policy: SyncPolicyHealthInput,
  effectiveEnabled = policy.enabled,
): SyncPolicyHealthPresentation {
  if (!effectiveEnabled) return { label: 'Kapalı', tone: 'disabled', stalled: false }

  const health = policy.healthStatus?.trim().toUpperCase()
  // Write policies (SHIPMENT_WRITE, RETURN_WRITE...) are permissions, not reads: there is nothing to be late.
  if (health === 'PERMISSION') return { label: 'İzin açık', tone: 'healthy', stalled: false }
  // The open-return scan is not queued while no return is open.
  if (health === 'PAUSED_NO_OPEN_RETURNS') return { label: 'Beklemede · Açık iade yok', tone: 'paused', stalled: false }
  // The server holds a stock reconciliation while the automatic price/stock writes are off: waiting, not late.
  if (health === 'PAUSED_WRITES_OFF') return { label: 'Otomatik fiyat/stok yazma kapalı', tone: 'paused', stalled: false }
  const stalled = policy.cursorProgressStatus?.trim().toUpperCase() === 'STALLED'

  if (health === 'OFFLINE') return { label: policy.lastSuccessAt ? 'Güncelleme gecikti' : 'İlk çalışma bekliyor', tone: 'offline', stalled }
  if (health === 'DEGRADED') return { label: 'Kısmi hata', tone: 'degraded', stalled }
  if (stalled) return { label: 'İmleç durgun', tone: 'stalled', stalled: true }
  if (health === 'DELAYED') return { label: 'Gecikiyor', tone: 'delayed', stalled: false }
  if (health === 'HEALTHY') return { label: 'Güncel', tone: 'healthy', stalled: false }

  return { label: 'Bilinmiyor', tone: 'unknown', stalled: false }
}
