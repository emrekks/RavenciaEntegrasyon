export type PublicationAttributeSelection = {
  attributeId: string
  name: string
  isRequired: boolean
  values: Array<{ id: string; label: string }>
  hasCustomValue: boolean
}

export type PublicationAttributeReference = {
  externalId: string
  name: string
  isActive: boolean
  isRequired?: boolean | null
  allowsCustomValue?: boolean | null
}

export type PublicationMappingReference = {
  localId: string
  externalId: string
  snapshotId: string
  status: string
}

export type PublicationValueReferenceSet = {
  snapshotId: string
  items: Array<{ externalId: string; isActive: boolean }>
  mappings: PublicationMappingReference[]
}

export type PublicationAttributeIssue = { attribute: string; detail: string }

export type PublicationAttributeIssues = {
  requiredIssues: PublicationAttributeIssue[]
  optionalWarnings: PublicationAttributeIssue[]
}

function isCustomPanelColorName(name: string) {
  const normalized = name
    .replace(/\[A-TDG\]/gi, '')
    .replace(/\[TDG\]/gi, '')
    .replace(/[\s_-]/g, '')
    .toLocaleUpperCase('tr-TR')
  return normalized === 'RENK' || normalized === 'COLOR' || normalized === 'COLOUR'
}

export function classifyPublicationAttributeIssues(input: {
  selectedAttributes: PublicationAttributeSelection[]
  remoteAttributes: PublicationAttributeReference[]
  attributeSnapshotId: string
  attributeMappings: PublicationMappingReference[]
  valueReferencesByAttribute: Record<string, PublicationValueReferenceSet>
}): PublicationAttributeIssues {
  const requiredIssues: PublicationAttributeIssue[] = []
  const optionalWarnings: PublicationAttributeIssue[] = []
  const addIssue = (required: boolean, issue: PublicationAttributeIssue) => {
    const target = required ? requiredIssues : optionalWarnings
    if (!target.some(item => item.attribute === issue.attribute && item.detail === issue.detail)) target.push(issue)
  }
  const remoteById = new Map(input.remoteAttributes.filter(item => item.isActive).map(item => [item.externalId, item]))
  const currentAttributeMappings = input.attributeMappings.filter(mapping =>
    mapping.status === 'VERIFIED' && mapping.snapshotId === input.attributeSnapshotId && remoteById.has(mapping.externalId)
  )
  const mappedExternalIds = new Set(currentAttributeMappings.map(mapping => mapping.externalId))

  for (const remote of remoteById.values()) {
    if (remote.isRequired && !mappedExternalIds.has(remote.externalId)) {
      addIssue(true, { attribute: remote.name, detail: 'Zorunlu Trendyol özelliği için güncel ve doğrulanmış eşleme yok.' })
    }
  }

  for (const selected of input.selectedAttributes) {
    if (!selected.values.length && !selected.hasCustomValue) continue
    const mappings = currentAttributeMappings
      .filter(mapping => mapping.localId === selected.attributeId)
      .sort((left, right) => {
        const leftColor = isCustomPanelColorName(remoteById.get(left.externalId)?.name ?? '') ? 1 : 0
        const rightColor = isCustomPanelColorName(remoteById.get(right.externalId)?.name ?? '') ? 1 : 0
        return leftColor - rightColor || left.externalId.localeCompare(right.externalId)
      })
    const mapping = mappings[0]
    const remote = mapping ? remoteById.get(mapping.externalId) : undefined
    const required = selected.isRequired || remote?.isRequired === true
    if (!mapping || !remote) {
      addIssue(required, { attribute: selected.name, detail: 'Seçili değer için güncel Trendyol özellik eşlemesi yok.' })
      continue
    }

    if (selected.hasCustomValue && remote.allowsCustomValue !== true) {
      addIssue(required, { attribute: selected.name, detail: 'Trendyol bu özellik için serbest değeri kabul etmiyor.' })
    }

    if (isCustomPanelColorName(remote.name)) continue
    const references = input.valueReferencesByAttribute[remote.externalId]
    for (const value of selected.values) {
      const valueMapping = references?.mappings.find(candidate =>
        candidate.localId === value.id && candidate.status === 'VERIFIED' && candidate.snapshotId === references.snapshotId
      )
      const activeReference = valueMapping && references.items.some(item => item.externalId === valueMapping.externalId && item.isActive)
      if (!activeReference) {
        addIssue(required, { attribute: selected.name, detail: `“${value.label || 'Seçili değer'}” için güncel Trendyol değer eşlemesi yok.` })
      }
    }
  }

  return { requiredIssues, optionalWarnings }
}
