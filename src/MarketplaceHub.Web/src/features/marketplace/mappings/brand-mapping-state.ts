/**
 * The connection the brand mapping card works on: the one picked on this platform, else the platform's first. It is
 * derived while rendering, so a platform tab never paints a frame without a connection (no footer, "no saved mapping").
 */
export function brandPlatformConnectionId(platformConnections: readonly { id: string }[], choice: string) {
  return platformConnections.some(item => item.id === choice) ? choice : platformConnections[0]?.id ?? ''
}

/**
 * Whether the panel brand's saved mapping on this connection is known (scan 25 Nov 2026, D-4). Only a read that
 * answered (a mapping, or none) is known: a failed read is not "no mapping", so the card neither offers
 * "Eşleştirmeyi Kaydet" without the saved version nor shows the picked brand as new.
 */
export type BrandMappingReadState = 'idle' | 'loading' | 'failed' | 'known'
export function brandMappingReadState(query: { isSuccess: boolean; isError: boolean }, enabled: boolean): BrandMappingReadState {
  if (!enabled) return 'idle'
  if (query.isSuccess) return 'known'
  if (query.isError) return 'failed'
  return 'loading'
}
