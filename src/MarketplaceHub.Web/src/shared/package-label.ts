// Orders without a real marketplace package (Pazarama, PttAVM) get a virtual package id `order:{orderNumber}`.
// That prefix is an internal key, so the screen shows only the code. The stored id and API payloads stay as they are.
const virtualPackagePrefix = /^order:/u

export function formatPackageLabel(value: string): string
export function formatPackageLabel(value: string | null | undefined): string | null
export function formatPackageLabel(value: string | null | undefined) {
  if (value == null) return null
  return value.replace(virtualPackagePrefix, '')
}
