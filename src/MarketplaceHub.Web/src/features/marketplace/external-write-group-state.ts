export type ExternalWriteGroupPolicy = {
  enabled: boolean
  requiresExternalWrites?: boolean
}

export type ExternalWriteGroupState = {
  configuredEnabled: boolean
  enabled: boolean
  partiallyEnabled: boolean
  blocked: boolean
  disabled: boolean
  label: string
}

export type ExternalWriteCapabilityEvidence = {
  code: string
  verifiedForConnection?: boolean
  supportLevel?: string | null
  verifiedAt?: string | null
  requiredScope?: string | null
  evidenceNote?: string | null
}

export type ExternalWriteEvidenceState = {
  blocked: boolean
  label: string
  details: string
}

export function externalWriteEvidenceState(
  capabilities: ExternalWriteCapabilityEvidence[],
  platformName: string,
  capabilitiesLoading = false,
): ExternalWriteEvidenceState {
  const missing = capabilities.filter(capability => !capability.verifiedForConnection)
  const normalizedSupport = (supportLevel: string | null | undefined) => (supportLevel ?? '').replaceAll('_', '').toUpperCase()
  const missingScope = missing.some(capability => normalizedSupport(capability.supportLevel) === 'NOTSUPPORTED' && capability.verifiedAt)
  const unknown = missing.some(capability => ['UNKNOWN', 'TEMPORARILYUNAVAILABLE'].includes(normalizedSupport(capability.supportLevel)) && capability.verifiedAt)

  return {
    blocked: capabilitiesLoading || missing.length > 0,
    label: capabilitiesLoading
      ? 'Yetenekler kontrol ediliyor'
      : missingScope
        ? `${platformName} izni eksik`
        : unknown
          ? `${platformName} yetenek doğrulanamadı`
          : 'Bağlantı testi gerekli',
    details: missing.map(capability => capability.evidenceNote?.trim() || capability.requiredScope || capability.code).join(' '),
  }
}

export function externalWriteGroupState(
  policies: ExternalWriteGroupPolicy[],
  externalWritesEnabled: boolean,
  evidenceBlocked: boolean,
  capabilitiesLoading = false,
  evidenceBlockedLabel = 'Bağlantı testi gerekli',
): ExternalWriteGroupState {
  const configuredEnabled = policies.length > 0 && policies.every(policy => policy.enabled)
  const activePolicies = policies.filter(policy => policy.enabled)
  const blocked = (!externalWritesEnabled && policies.some(policy => policy.requiresExternalWrites === true)) || evidenceBlocked
  const partiallyEnabled = activePolicies.length > 0 && !configuredEnabled
  const blockedLabel = evidenceBlocked
    ? capabilitiesLoading ? 'Yetenekler kontrol ediliyor' : evidenceBlockedLabel
    : 'Dış yazma kapalı'

  return {
    configuredEnabled,
    enabled: configuredEnabled && !blocked,
    partiallyEnabled,
    blocked,
    disabled: (blocked && !configuredEnabled) || policies.length === 0,
    label: blocked && !configuredEnabled
      ? blockedLabel
      : partiallyEnabled
        ? 'Kısmen açık'
        : configuredEnabled
          ? blocked ? blockedLabel : 'Açık'
          : 'Kapalı',
  }
}
