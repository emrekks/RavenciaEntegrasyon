import { describe, expect, it } from 'vitest'
import { applyFilterPatch } from './order-filter-apply'

describe('applying one filter at once', () => {
  it('keeps unsubmitted search text out of the applied filters but in the box', () => {
    const applied = { search: '', cargo: 'ALL', status: 'ALL' }
    const form = { search: 'yarım', cargo: 'ALL', status: 'ALL' }

    const next = applyFilterPatch(applied, form, { cargo: 'Aras Kargo' })

    expect(next.applied).toEqual({ search: '', cargo: 'Aras Kargo', status: 'ALL' })
    expect(next.form).toEqual({ search: 'yarım', cargo: 'Aras Kargo', status: 'ALL' })
  })
})
