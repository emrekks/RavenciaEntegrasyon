const completedStatuses = new Set(['COMPLETED'])
const failedStatuses = new Set(['REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW', 'MARKETPLACE_FAILED', 'FATURA_PLATFORMA_AKTARILMADI', 'CANCELLED', 'CANCELLED_LOCAL'])

export function invoiceCompletionOutcome(status: string): 'complete' | 'failed' | 'pending' {
  const normalized = status.trim().toUpperCase()
  if (completedStatuses.has(normalized)) return 'complete'
  if (failedStatuses.has(normalized)) return 'failed'
  return 'pending'
}
