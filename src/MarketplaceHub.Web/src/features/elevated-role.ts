import { useQueryClient } from '@tanstack/react-query'
import type { Me } from '../shared/api'

// The owner and administrators: the roles RoleAuthorizationMiddleware.ElevatedRoles lets write Toplu Değer Ata
// (products/{id}/bulk-field) and the account's Görünüm and Kargo Etiketi settings (PUT settings/appearance,
// settings/shipping-label); every other role gets 403 there. Keep the list in step with the middleware.
export const ELEVATED_ROLES = ['OWNER', 'ADMINISTRATOR'] as const

export function isElevatedRole(role: string | null | undefined) {
  return (ELEVATED_ROLES as readonly string[]).includes((role ?? '').toUpperCase())
}

// Whether the signed-in role is known to be refused those writes. The session (['me']) is read before any page renders
// and its role does not change without a new sign-in; a role not known here is not taken for a refusal (the server
// still answers for itself).
export function lacksElevatedRole(role: string | null | undefined) {
  return role != null && role !== '' && !isElevatedRole(role)
}

export function useLacksElevatedRole() {
  return lacksElevatedRole(useQueryClient().getQueryData<Me>(['me'])?.role)
}
