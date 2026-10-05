export function visibleReturnClaims<T extends { status: string }>(claims: readonly T[]): T[] {
  return claims.filter(claim => !['CANCELLED', 'CANCELED'].includes(claim.status.trim().toUpperCase()))
}
