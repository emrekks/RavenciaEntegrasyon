import { afterEach, describe, expect, it } from 'vitest'
import { ApiRequestError } from '../api/client'
import { cleanup, render } from '../../test/render'
import { autoRetryDelaySeconds, autoRetryLine, noteLoadFailure, useAutoRetry, type AutoRetry } from './useAutoRetry'

const retry = (patch: Partial<AutoRetry>): AutoRetry => ({ left: 10, total: 10, stopped: false, retrying: false, now: () => {}, stop: () => {}, ...patch })

describe('automatic retry of a failed load', () => {
  it('waits longer after every failed try and settles at one minute', () => {
    expect([0, 1, 2, 3, 4, 9].map(autoRetryDelaySeconds)).toEqual([10, 20, 40, 60, 60, 60])
  })

  it('counts failures in a row and forgets them after five quiet minutes', () => {
    const start = 1_000_000
    expect([0, 12, 40].map(offset => noteLoadFailure(start + offset * 1000))).toEqual([0, 1, 2])
    expect(noteLoadFailure(start + 40_000 + 5 * 60_000 + 1)).toBe(0)
  })

  it('tells what happens next', () => {
    expect(autoRetryLine(retry({ left: 7 }))).toBe('7 saniye içinde yeniden denenecek.')
    expect(autoRetryLine(retry({ left: 0, retrying: true }))).toBe('Yeniden deneniyor…')
    expect(autoRetryLine(retry({ stopped: true }))).toBe('Otomatik deneme durduruldu.')
  })
})

describe('automatic retry and errors a repeat cannot fix', () => {
  afterEach(cleanup)
  function Probe({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
    const retry = useAutoRetry(error, onRetry)
    return <output>{retry ? `${retry.left}` : 'none'}</output>
  }

  it('counts down for a server failure', () => {
    const { host } = render(<Probe error={new ApiRequestError('Sunucu hatası', 502)} onRetry={() => {}} />)
    expect(host.textContent).not.toBe('none')
  })

  it('does not try again when the session ended, access is refused or the record is gone', () => {
    for (const status of [401, 403, 404, 412, 422]) {
      const { host, unmount } = render(<Probe error={new ApiRequestError('x', status)} onRetry={() => {}} />)
      expect(host.textContent).toBe('none')
      unmount()
    }
  })
})
