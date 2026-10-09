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

export function externalWriteGroupState(
  policies: ExternalWriteGroupPolicy[],
  externalWritesEnabled: boolean,
  evidenceBlocked: boolean,
  capabilitiesLoading = false,
): ExternalWriteGroupState {
  const configuredEnabled = policies.length > 0 && policies.every(policy => policy.enabled)
  const activePolicies = policies.filter(policy => policy.enabled)
  const blocked = (!externalWritesEnabled && policies.some(policy => policy.requiresExternalWrites === true)) || evidenceBlocked
  const partiallyEnabled = activePolicies.length > 0 && !configuredEnabled
  const blockedLabel = evidenceBlocked
    ? capabilitiesLoading ? 'Yetenekler kontrol ediliyor' : 'Bağlantı testi gerekli'
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
