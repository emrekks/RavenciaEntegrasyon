import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, useLocation } from 'react-router'
import { byText, cleanup, click, render, waitFor } from '../test/render'
import { readUrlChoice, readUrlList, readUrlNumber, readUrlText, useChangeEffect, useListUrlSync, withListParams } from './list-url-state'

afterEach(cleanup)

describe('list address state', () => {
  it('reads values and falls back on anything it cannot use', () => {
    const params = new URLSearchParams('page=3&size=37&q=%20mont%20&platforms=TRENDYOL,,N11&sort=date_asc&bad=-2')
    expect(readUrlNumber(params, 'page', 1)).toBe(3)
    expect(readUrlNumber(params, 'size', 20, [20, 50])).toBe(20)
    expect(readUrlNumber(params, 'bad', 1)).toBe(1)
    expect(readUrlText(params, 'q')).toBe('mont')
    expect(readUrlText(params, 'missing', 'ALL')).toBe('ALL')
    expect(readUrlList(params, 'platforms')).toEqual(['TRENDYOL', 'N11'])
    expect(readUrlChoice(params, 'sort', ['DATE_DESC', 'DATE_ASC'] as const, 'DATE_DESC')).toBe('DATE_ASC')
  })

  it('writes values over the address, leaves defaults out and keeps other parameters', () => {
    const next = withListParams(new URLSearchParams('tab=auth&page=4'), { page: 1, size: 50, status: 'ALL', platforms: ['N11'], q: '' }, { page: 1, size: 20, status: 'ALL' })
    expect(next.toString()).toBe('tab=auth&size=50&platforms=N11')
  })

  it('keeps the applied state in the address and runs change effects only after a change', async () => {
    const reset = vi.fn()
    function List() {
      const [page, setPage] = useState(2)
      const [status, setStatus] = useState('ALL')
      useChangeEffect(status, reset)
      useListUrlSync({ page, status }, { page: 1, status: 'ALL' })
      return <><button type="button" onClick={() => setPage(5)}>Sayfa</button><button type="button" onClick={() => setStatus('NEW')}>Yeni</button><output>{useLocation().search}</output></>
    }
    const { host } = render(<MemoryRouter initialEntries={['/orders?tab=x']}><List /></MemoryRouter>)
    await waitFor(() => host.querySelector('output')?.textContent === '?tab=x&page=2')
    expect(reset).not.toHaveBeenCalled()
    click(byText(host, 'button', 'Sayfa'))
    await waitFor(() => host.querySelector('output')?.textContent === '?tab=x&page=5')
    click(byText(host, 'button', 'Yeni'))
    await waitFor(() => host.querySelector('output')?.textContent === '?tab=x&page=5&status=NEW')
    expect(reset).toHaveBeenCalledTimes(1)
  })
})
