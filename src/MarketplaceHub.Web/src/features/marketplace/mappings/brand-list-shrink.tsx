import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { formatDateTime } from '../../../shared/format-date'
import { useLacksElevatedRole } from '../../elevated-role'
import { brandReferenceKey } from './brand-reference'
import './brand-list-shrink.css'

export type BrandListShrink = NonNullable<ApiAnswer<'getReferenceDataBrandsShrink'>>

export const brandListShrinkKey = (connectionId: string) => [...brandReferenceKey(connectionId), 'shrink'] as const

/**
 * Kuyruk Faz 1B (review D2): a brand list read that lacked too many brands was not applied (REFERENCE_LIST_SHRANK) and
 * nothing was deleted. When the shrink is real on the marketplace, the owner or an administrator approves it here and
 * the next read of this connection applies it once (within 48 hours); otherwise nothing needs doing.
 */
export function BrandListShrinkNotice({ connectionId, platformLabel }: { connectionId: string; platformLabel: string }) {
  const client = useQueryClient()
  const lacksRole = useLacksElevatedRole()
  const shrink = useQuery({ queryKey: brandListShrinkKey(connectionId), queryFn: ({ signal }) => hubApi<BrandListShrink | null>(`/reference-data/brands/shrink?connectionId=${encodeURIComponent(connectionId)}`, { signal }), enabled: !!connectionId, retry: false })
  const approve = useMutation({
    mutationFn: () => hubApi<BrandListShrink>(`/reference-data/brands/shrink/approval?connectionId=${encodeURIComponent(connectionId)}`, { method: 'POST', body: '{}' }),
    onSuccess: value => client.setQueryData(brandListShrinkKey(connectionId), value)
  })
  if (!shrink.data) return null
  const approved = shrink.data.approvedAt && shrink.data.approvalExpiresAt
  return <div className="brand-list-shrink" role="status">
    <div>
      <strong>{platformLabel} Marka Listesi Küçüldü, Uygulanmadı</strong>
      <p>{shrink.data.summary}</p>
      {approved
        ? <small>Küçülme onaylandı ({formatDateTime(shrink.data.approvedAt!)}); sonraki okuma bir kez uygular. Onay {formatDateTime(shrink.data.approvalExpiresAt!)} tarihine kadar geçerli.</small>
        : lacksRole ? <small>Onayı yalnız sahip ya da yönetici verebilir.</small> : null}
      {approve.isError && <small role="alert">{approve.error instanceof Error ? approve.error.message : 'Onay kaydedilemedi.'}</small>}
    </div>
    {!approved && !lacksRole && <button type="button" className="secondary" disabled={approve.isPending} onClick={() => approve.mutate()}>{approve.isPending ? 'Onaylanıyor…' : 'Küçülmeyi Onayla'}</button>}
  </div>
}
