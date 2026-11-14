// A Trendyol product still under review or refused sits on Trendyol's unapproved list (server ImportedApprovalStates:
// a listing APPROVAL_PENDING or REJECTED). It is not on sale, but it is there: an update goes to it through
// unapproved-bulk-update (server ProductUpdateComposer), never as "not in the store" and never as a second create
// (owner, 15 Nov 2026: MZ056 Kahverengi/Mürdüm, rejected for "Hatalı Görsel Linki", were left out as "bu mağazada yok").
const heldStatuses = new Set(['REJECTED', 'APPROVAL_PENDING'])

export function isTrendyolHeld(platformCode: string | null | undefined, status: string | null | undefined) {
  return platformCode?.trim().toUpperCase() === 'TRENDYOL' && heldStatuses.has(status?.trim().toUpperCase() ?? '')
}
