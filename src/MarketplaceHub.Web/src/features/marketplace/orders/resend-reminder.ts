// The "Yeniden Gönderim" card's old manual reminder: until 23 Nov 2026 the owner typed the number of resends waiting.
// Since then the gear keeps records (manual-resends.ts); a count typed before cannot become records, so it is shown as
// "Eski Hatırlatma: N" until the owner clears it (the server refuses a higher number). Kept per tenant on the server.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, settingsIfMatch } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'

export type ResendReminderEnvelope = { count: number; version: number }
export const resendReminderQueryKey = queryKeys.settings('resend-reminder')

export function useResendReminder() {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: resendReminderQueryKey,
    queryFn: ({ signal }) => hubApi<ApiAnswer<'getSettingsResendReminder'>>('/settings/resend-reminder', { signal }),
    staleTime: 60_000,
    refetchOnWindowFocus: true
  })
  async function clear() {
    // A stored setting is written only over the version read (If-Match); one not read yet is read first.
    const current = client.getQueryData<ResendReminderEnvelope>(resendReminderQueryKey)
      ?? await client.fetchQuery({ queryKey: resendReminderQueryKey, queryFn: ({ signal }) => hubApi<ApiAnswer<'getSettingsResendReminder'>>('/settings/resend-reminder', { signal }) })
    try {
      const saved = await hubApi<ApiAnswer<'putSettingsResendReminder'>>('/settings/resend-reminder', { method: 'PUT', headers: settingsIfMatch(current?.version), body: JSON.stringify({ count: 0 }) })
      client.setQueryData(resendReminderQueryKey, saved)
    } catch (error) {
      void client.invalidateQueries({ queryKey: resendReminderQueryKey })
      throw error
    }
  }
  return { count: query.data?.count ?? 0, loaded: query.data !== undefined, clear }
}
