export const mappingPlatformDefinitions = [
  { code: 'TRENDYOL', label: 'Trendyol' },
  { code: 'HEPSIBURADA', label: 'Hepsiburada' },
  { code: 'N11', label: 'n11' },
  { code: 'PAZARAMA', label: 'Pazarama' },
  { code: 'PTTAVM', label: 'PttAVM' },
  { code: 'SHOPIFY', label: 'Shopify' }
] as const

export type MappingPlatformCode = typeof mappingPlatformDefinitions[number]['code']

export function activeMappingConnectionsForPlatform<T extends { platformCode: string; status: string }>(
  connections: readonly T[],
  platformCode: string
): T[] {
  const expectedPlatform = platformCode.toUpperCase()
  return connections.filter(connection =>
    connection.platformCode.toUpperCase() === expectedPlatform
    && ['ACTIVE', 'VERIFIED'].includes(connection.status.toUpperCase())
  )
}

export function mappingPlatformLabel(platformCode: string): string {
  return mappingPlatformDefinitions.find(platform => platform.code === platformCode.toUpperCase())?.label ?? platformCode
}
