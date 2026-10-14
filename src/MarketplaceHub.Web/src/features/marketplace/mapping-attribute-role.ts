export type AttributeMappingRole = 'ATTRIBUTE' | 'OPTION'

export function resolveAttributeMappingRole(categoryRole: string | null | undefined, panelAttributeIsOption: boolean): AttributeMappingRole {
  const normalizedCategoryRole = categoryRole?.trim().toUpperCase()
  if (normalizedCategoryRole === 'OPTION') return 'OPTION'
  if (normalizedCategoryRole === 'ATTRIBUTE') return 'ATTRIBUTE'
  return panelAttributeIsOption ? 'OPTION' : 'ATTRIBUTE'
}
