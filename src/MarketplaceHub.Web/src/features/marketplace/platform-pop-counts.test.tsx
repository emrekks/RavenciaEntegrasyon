import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { ReturnColumnPop, ReturnPlatformPop, platformOptionCount } from './shared'

const options = [{ value: 'TRENDYOL', label: 'Trendyol' }, { value: 'PTTAVM', label: 'PttAVM' }, { value: 'N11', label: 'N11' }]
const counts = (host: HTMLElement) => [...host.querySelectorAll('.rr-opt')].map(row => row.querySelector('.rr-opt-count'))

// The invoices, orders and returns pages fill the counts from the server; without them the rows stay as they were.
describe('ReturnPlatformPop counts', () => {
  afterEach(cleanup)

  it('shows each platform count, a faded zero and zero for a platform the answer leaves out', () => {
    const { host } = render(<ReturnPlatformPop title="Platform" options={options} selected={[]} onChange={() => undefined} counts={{ TRENDYOL: 1234, PttAvm: 0 }} />)
    expect(counts(host).map(cell => cell?.textContent)).toEqual(['1.234', '0', '0'])
    expect(counts(host).map(cell => cell?.classList.contains('is-zero'))).toEqual([false, true, true])
  })

  it('shows a placeholder while the counts load, never a zero', () => {
    const { host } = render(<ReturnPlatformPop title="Platform" options={options} selected={[]} onChange={() => undefined} counts={null} />)
    expect(counts(host).every(cell => cell?.classList.contains('is-loading') && cell.textContent === '')).toBe(true)
  })

  it('shows no count when none is given', () => {
    const { host } = render(<ReturnPlatformPop title="Platform" options={options} selected={[]} onChange={() => undefined} />)
    expect(counts(host).every(cell => cell === null)).toBe(true)
  })

  it('reads a count whatever the code case', () => {
    expect(platformOptionCount({ HEPSIBURADA: 3 }, 'hepsiburada')).toBe(3)
    expect(platformOptionCount(null, 'N11')).toBeUndefined()
  })
})

describe('ReturnColumnPop counts', () => {
  afterEach(cleanup)
  const cargo = [{ value: 'ALL', label: 'Tüm Kargolar' }, { value: 'Aras Kargo', label: 'Aras Kargo' }, { value: 'Yurtiçi', label: 'Yurtiçi Kargo' }]

  it('shows each choice its count, none on the "all" row', () => {
    const { host } = render(<ReturnColumnPop label="Kargo Firması" options={cargo} value="ALL" onPick={() => undefined} counts={{ 'Aras Kargo': 12 }} />)
    expect(counts(host).map(cell => cell?.textContent ?? null)).toEqual([null, '12', '0'])
  })

  it('shows placeholders while loading and nothing without counts', () => {
    const loading = render(<ReturnColumnPop label="Kargo Firması" options={cargo} value="ALL" onPick={() => undefined} counts={null} />)
    expect(counts(loading.host).slice(1).every(cell => cell?.classList.contains('is-loading'))).toBe(true)
    cleanup()
    const plain = render(<ReturnColumnPop label="Kargo Firması" options={cargo} value="ALL" onPick={() => undefined} />)
    expect(counts(plain.host).every(cell => cell === null)).toBe(true)
  })
})
