import { platformDisplayNames } from '../../shared/platform-names'

export const mappingPlatformDefinitions = [
  { code: 'TRENDYOL', label: platformDisplayNames.TRENDYOL },
  { code: 'HEPSIBURADA', label: platformDisplayNames.HEPSIBURADA },
  { code: 'N11', label: platformDisplayNames.N11 },
  { code: 'PAZARAMA', label: platformDisplayNames.PAZARAMA },
  { code: 'PTTAVM', label: platformDisplayNames.PTTAVM },
  { code: 'SHOPIFY', label: platformDisplayNames.SHOPIFY }
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
