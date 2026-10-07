export type SyncPolicyHealthInput = {
  enabled: boolean
  healthStatus?: string | null
  cursorProgressStatus?: string | null
}

export type SyncPolicyHealthPresentation = {
  label: string
  tone: 'disabled' | 'healthy' | 'delayed' | 'degraded' | 'offline' | 'stalled' | 'unknown'
  stalled: boolean
}

export function syncPolicyHealthPresentation(
  policy: SyncPolicyHealthInput,
  effectiveEnabled = policy.enabled,
): SyncPolicyHealthPresentation {
  if (!effectiveEnabled) return { label: 'Kapalı', tone: 'disabled', stalled: false }

  const health = policy.healthStatus?.trim().toUpperCase()
  const stalled = policy.cursorProgressStatus?.trim().toUpperCase() === 'STALLED'

  if (health === 'OFFLINE') return { label: 'Çevrim dışı', tone: 'offline', stalled }
  if (health === 'DEGRADED') return { label: 'Kısmi hata', tone: 'degraded', stalled }
  if (stalled) return { label: 'İmleç durgun', tone: 'stalled', stalled: true }
  if (health === 'DELAYED') return { label: 'Gecikiyor', tone: 'delayed', stalled: false }
  if (health === 'HEALTHY') return { label: 'Güncel', tone: 'healthy', stalled: false }

  return { label: 'Bilinmiyor', tone: 'unknown', stalled: false }
}
