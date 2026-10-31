/**
 * The connection the brand mapping card works on: the one picked on this platform, else the platform's first. It is
 * derived while rendering, so a platform tab never paints a frame without a connection (no footer, "no saved mapping").
 */
export function brandPlatformConnectionId(platformConnections: readonly { id: string }[], choice: string) {
  return platformConnections.some(item => item.id === choice) ? choice : platformConnections[0]?.id ?? ''
}
