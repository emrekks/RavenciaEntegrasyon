export function canPrintReturnLabel(status: string | null | undefined): boolean {
  return status?.trim().toUpperCase() === 'REJECTED'
}
