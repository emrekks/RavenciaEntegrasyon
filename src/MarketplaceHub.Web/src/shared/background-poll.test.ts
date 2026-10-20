import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
import { isBackgroundPoll, markUserInteraction, pollRequestInit } from './background-poll'

describe('background poll header (W1)', () => {
  const now = 1_000_000

  function context(loaded: boolean) {
    const client = new QueryClient()
    const queryKey = ['jobs', 'counts']
    if (loaded) client.setQueryData(queryKey, [])
    return { client, queryKey }
  }

  it('leaves the first load alone', () => {
    markUserInteraction(0)
    expect(isBackgroundPoll(context(false), now)).toBe(false)
    expect(pollRequestInit(context(false), undefined, now)).toBeUndefined()
  })

  it('marks a refetch of loaded data while the person is idle', () => {
    markUserInteraction(now - 60_000)
    const init = pollRequestInit(context(true), { headers: { Accept: 'application/json' } }, now)
    const headers = new Headers(init?.headers)
    expect(headers.get('X-Background-Poll')).toBe('1')
    expect(headers.get('Accept')).toBe('application/json')
  })

  it('does not mark a refetch right after a click or key press (Yenile, paging)', () => {
    markUserInteraction(now - 500)
    expect(isBackgroundPoll(context(true), now)).toBe(false)
  })
})
