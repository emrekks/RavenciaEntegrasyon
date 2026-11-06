import { useQueryClient } from '@tanstack/react-query'
import type { Me } from '../api'

// No invoice goes out without the owner's approval: issuing an e-invoice, cancelling it, and sending or removing its
// link at the marketplace are for the owner and administrators (RoleAuthorizationMiddleware answers the other roles
// 403). The session (['me']) is loaded before any page renders and its role does not change without a new sign-in.
export const invoiceSendDeniedReason = 'Fatura kesme, iptal ve pazaryerine gönderme yalnız sahip ve yöneticiler içindir.'

export function canSendInvoices(role: string | null | undefined) {
  return ['OWNER', 'ADMINISTRATOR'].includes((role ?? '').toUpperCase())
}

export function useCanSendInvoices() {
  return canSendInvoices(useQueryClient().getQueryData<Me>(['me'])?.role)
}
