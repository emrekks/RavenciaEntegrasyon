import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { newIdempotencyKey } from '../../shared/idempotency'
import { Badge, Button, useConfirmDialog } from '../../shared/components'
import { linkRevocationActionLabel, linkRevocationConfirmCopy, linkRevocationNote, linkRevocationStatusLabel, linkRevocationSuccessMessage, linkRevocationTone, type InvoiceLinkRevocation } from './invoice-link-revocation'
import './invoice-link-revocation.css'

type Props = {
  invoiceId: string
  invoiceNumber: string | null
  items: InvoiceLinkRevocation[]
  onNotice: (message: string, kind: 'success' | 'error') => void
}

// Links a cancelled invoice left on marketplace packages. Each removal is the operator's own request, confirmed in the
// panel's dialog (no password: being signed in with the role is enough); nothing here runs on its own.
export function InvoiceLinkRevocationPanel({ invoiceId, invoiceNumber, items, onNotice }: Props) {
  const client = useQueryClient()
  const [confirm, confirmDialog] = useConfirmDialog()
  const revoke = useMutation({
    mutationFn: (item: InvoiceLinkRevocation) => hubApi<InvoiceLinkRevocation>(`/invoices/${invoiceId}/marketplace-link-revocations/${item.deliveryStateId}`, {
      method: 'POST',
      headers: { 'Idempotency-Key': newIdempotencyKey() },
      body: JSON.stringify({ confirmed: true }),
    }),
    onSuccess: async (_result, item) => {
      onNotice(linkRevocationSuccessMessage(item), 'success')
      await client.invalidateQueries({ queryKey: ['invoice', invoiceId] })
    },
    onError: async error => {
      onNotice(error instanceof Error ? error.message : 'Fatura linki kaldırılamadı.', 'error')
      await client.invalidateQueries({ queryKey: ['invoice', invoiceId] })
    },
  })

  async function start(item: InvoiceLinkRevocation) {
    if (!(await confirm(linkRevocationConfirmCopy(item, invoiceNumber)))) return
    revoke.mutate(item)
  }

  if (items.length === 0) return null
  return <div className="panel invoice-link-revocations">
    <h2>Pazaryeri Fatura Linkleri</h2>
    <p className="invoice-detail-muted">Bu fatura iptal edildi. Pazaryerine iletilen linki kaldırılmadan aynı pakete yeni fatura iletilemez.</p>
    <div className="card-list">{items.map(item => {
      const action = linkRevocationActionLabel(item)
      const note = linkRevocationNote(item)
      const running = revoke.isPending && revoke.variables?.deliveryStateId === item.deliveryStateId
      return <div className="record-card invoice-link-revocation" key={item.deliveryStateId}>
        <span className="invoice-link-revocation-body">
          <strong>{item.platformName}{item.packageNumber ? ` · Paket ${item.packageNumber}` : ''}</strong>
          {item.status !== 'LINK_REMOVED' && <small>{item.instructions}</small>}
          {note && <small>{note}</small>}
          {item.errorCode && item.status !== 'LINK_REMOVED' && <small>Son hata: <code className="technical-text">{item.errorCode}</code></small>}
          {action && !item.canAct && item.blockedReason && <small className="invoice-link-revocation-blocked">{item.blockedReason}</small>}
        </span>
        <span className="invoice-link-revocation-actions">
          <Badge tone={linkRevocationTone(item.status)}>{linkRevocationStatusLabel(item.status)}</Badge>
          {action && <Button variant={item.mode === 'AUTOMATIC' ? 'danger' : 'secondary'} size="md" loading={running} disabled={!item.canAct || revoke.isPending} onClick={() => void start(item)}>{action}</Button>}
        </span>
      </div>
    })}</div>
    {confirmDialog}
  </div>
}
