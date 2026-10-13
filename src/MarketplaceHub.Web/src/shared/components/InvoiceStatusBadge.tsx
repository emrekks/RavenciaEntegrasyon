import { StatusBadge, type StatusTone } from './DesignSystem'
import { invoiceStatusLabel, invoiceStatusTone, type InvoiceStatusTone } from '../status-labels'

type InvoiceStatusBadgeProps = {
  status: string | null | undefined
  tone?: InvoiceStatusTone
  label?: string
  className?: string
  role?: 'status'
  title?: string
}

export function InvoiceStatusBadge({ status, tone, label, className, role = 'status', title }: InvoiceStatusBadgeProps) {
  const resolvedTone = (tone ?? invoiceStatusTone(status)) as StatusTone
  return <StatusBadge tone={resolvedTone} className={['invoice-status-label', className].filter(Boolean).join(' ')} role={role} title={title} aria-label={label ?? invoiceStatusLabel(status)}>{label ?? invoiceStatusLabel(status)}</StatusBadge>
}
