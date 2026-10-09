export function canPrintReturnLabel(status: string | null | undefined, hasOutboundShipment: boolean): boolean {
  return status?.trim().toUpperCase() === 'REJECTED' && !hasOutboundShipment
}
