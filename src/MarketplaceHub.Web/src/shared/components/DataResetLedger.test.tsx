import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, render, type } from '../../test/render'
import { DataResetLedger, type DataResetGroup } from './DataResetLedger'

afterEach(cleanup)

const groups: DataResetGroup[] = [{ label: 'Sipariş ve Finans', description: '', options: [
  { scope: 'ORDERS', label: 'Sipariş Verileri', description: '', unit: 'sipariş', implies: ['RETURNS', 'INVOICES'] },
  { scope: 'RETURNS', label: 'İade Verileri', description: '', unit: 'iade' },
  { scope: 'INVOICES', label: 'Fatura Verileri', description: '', unit: 'fatura' }
] }]

function Harness({ onConfirm }: { onConfirm: (scopes: string[]) => void }) {
  const [picked, setPicked] = useState<string[]>([])
  const [confirmation, setConfirmation] = useState('')
  return <DataResetLedger idPrefix="t" groups={groups} counts={{ ORDERS: 1200, RETURNS: 30, INVOICES: 900 }} picked={picked} onToggle={(scopes, checked) => setPicked(current => checked ? [...new Set([...current, ...scopes])] : current.filter(scope => !scopes.includes(scope)))}
    notes={[]} confirmWord="Trendyol" confirmation={confirmation} onConfirmation={setConfirmation} busy={false} onConfirm={() => onConfirm(picked)} />
}

describe('data reset ledger', () => {
  it('takes an order’s returns and invoices with it, totals them and asks for the typed name', () => {
    const onConfirm = vi.fn()
    render(<Harness onConfirm={onConfirm} />)
    const rows = () => Array.from(document.querySelectorAll<HTMLLabelElement>('.drl-row'))
    click(rows()[0]!.querySelector('input')!)
    expect(rows().map(row => row.classList.contains('is-locked'))).toEqual([false, true, true])
    expect(rows()[1]!.querySelector('input')!.disabled).toBe(true)
    expect(document.querySelector('.drl-total')?.textContent).toContain('2.130 kayıt')
    const go = document.querySelector<HTMLButtonElement>('.drl-go')!
    expect(go.textContent).toBe('3 Veri Türünü Sıfırla')
    expect(go.disabled).toBe(true)
    type(document.querySelector<HTMLInputElement>('#t-confirm')!, 'Trendyol')
    expect(go.disabled).toBe(false)
    click(go)
    // Only the picked scope goes to the server; it removes the returns and invoices itself.
    expect(onConfirm).toHaveBeenCalledWith(['ORDERS'])
  })
})
