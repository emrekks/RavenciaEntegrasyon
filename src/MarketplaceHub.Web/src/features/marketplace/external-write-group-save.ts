import { ApiRequestError, hubApi } from '../../shared/api'

export type ExternalWriteGroupSavePolicy = { resourceType: string; version: number; intervalSeconds: number; overlapSeconds: number; jitterSeconds: number }

// A write group (the automatic price/stock pair, the manual pair) is switched in ONE request: the server checks every
// version and saves them in one transaction, so the pair is never left half open (18 Nov: two parallel PUTs each took
// the price/stock baseline over the same offers, one failed with 409 and PttAVM stayed with price on, stock off).
export async function saveExternalWriteGroup(connectionId: string, policies: ExternalWriteGroupSavePolicy[], enabled: boolean): Promise<void> {
  try {
    await hubApi<unknown>(`/connections/${connectionId}/external-write-group`, { method: 'PUT', body: JSON.stringify({ enabled, policies: policies.map(policy => ({ resourceType: policy.resourceType, version: policy.version })) }) })
  } catch (reason) {
    // An API from before the group route answers 404/405 without a problem code while a deploy rolls out: the policies
    // go one after another there (never in parallel), each with its own version.
    if (!(reason instanceof ApiRequestError) || (reason.status !== 404 && reason.status !== 405) || reason.code) throw reason
    for (const policy of policies)
      await hubApi<unknown>(`/connections/${connectionId}/sync-policies/${policy.resourceType}`, { method: 'PUT', headers: { 'If-Match': `"v${policy.version}"` }, body: JSON.stringify({ intervalSeconds: policy.intervalSeconds, overlapSeconds: policy.overlapSeconds, jitterSeconds: policy.jitterSeconds, enabled }) })
  }
}
