// The "Yeniden Gönderim" card's manual reminder: Trendyol never returns resend packages, so the owner types the number
// of resends waiting. It is kept per tenant on the server (every device shows it), never in this browser.
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, settingsIfMatch } from '../../../shared/api'

export type ResendReminderEnvelope = { count: number; version: number }
export const resendReminderQueryKey = ['settings', 'resend-reminder'] as const
export const maxResendReminder = 100_000

// What the field holds -> the number to save; undefined for anything that is not a whole number from 0 to the maximum.
export function parseResendReminder(text: string): number | undefined {
  const trimmed = text.trim()
  if (!/^\d{1,6}$/.test(trimmed)) return undefined
  const value = Number(trimmed)
  return value <= maxResendReminder ? value : undefined
}

export function useResendReminder() {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: resendReminderQueryKey,
    queryFn: () => hubApi<ResendReminderEnvelope>('/settings/resend-reminder'),
    staleTime: 60_000,
    refetchOnWindowFocus: true
  })
  async function save(count: number) {
    // A stored setting is written only over the version read (If-Match); one not read yet is read first.
    const current = client.getQueryData<ResendReminderEnvelope>(resendReminderQueryKey)
      ?? await client.fetchQuery({ queryKey: resendReminderQueryKey, queryFn: () => hubApi<ResendReminderEnvelope>('/settings/resend-reminder') })
    try {
      const saved = await hubApi<ResendReminderEnvelope>('/settings/resend-reminder', { method: 'PUT', headers: settingsIfMatch(current?.version), body: JSON.stringify({ count }) })
      client.setQueryData(resendReminderQueryKey, saved)
    } catch (error) {
      void client.invalidateQueries({ queryKey: resendReminderQueryKey })
      throw error
    }
  }
  return { count: query.data?.count ?? 0, loaded: query.data !== undefined, save }
}
