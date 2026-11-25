import { describe, expect, it } from 'vitest'
import { jobModelCardColour, jobModelGroupLine, jobModelGroupState } from './job-model-group'
import { rowJobs } from './job-model-rows'

const jobs = (...statuses: string[]) => statuses.map(status => ({ status, lastErrorCode: null }))

describe('jobModelGroupState', () => {
  it('reads as succeeded only when every card succeeded', () => {
    expect(jobModelGroupState(jobs('SUCCEEDED', 'SUCCEEDED'))).toEqual({ tone: 'success', label: 'Başarılı', counts: null })
  })

  it('never hides a failure behind succeeded cards: the worst failure names the row, with the counts', () => {
    const state = jobModelGroupState(jobs('SUCCEEDED', 'SUCCEEDED', 'SUCCEEDED', 'SUCCEEDED', 'SUCCEEDED', 'DEAD', 'MANUAL_REVIEW'))
    expect(state.tone).toBe('error')
    expect(state.label).toBe('Deneme Limiti Doldu')
    expect(state.counts).toBe('5\u00a0Başarılı · 2\u00a0Başarısız')
    expect(jobModelGroupState(jobs('SUCCEEDED', 'BLOCKED', 'LEASED')).label).toBe('Engellendi')
  })

  it('shows work still running with how many cards finished', () => {
    const state = jobModelGroupState([...jobs('SUCCEEDED', 'SUCCEEDED', 'LEASED', 'PENDING'), { status: 'RETRY_SCHEDULED', lastErrorCode: 'SLICE_CONTINUES' }])
    expect(state).toEqual({ tone: 'running', label: 'Sürüyor (2/5)', counts: '2\u00a0Başarılı · 3\u00a0Sürüyor' })
  })

  it('keeps cancelled cards apart from failures', () => {
    expect(jobModelGroupState(jobs('CANCELLED', 'CANCELLED')).label).toBe('İptal Edildi')
    expect(jobModelGroupState(jobs('SUCCEEDED', 'CANCELLED'))).toEqual({ tone: 'neutral', label: 'Kısmen İptal Edildi', counts: '1\u00a0Başarılı · 1\u00a0İptal' })
  })
})

describe('model row text', () => {
  it('names the card by its colour option, Renk before Web Color, else by its title', () => {
    expect(jobModelCardColour({ optionSignature: 'Web Color: Siyah | Renk: Gece Mavisi | Beden: M', title: 'Elbise' })).toBe('Gece Mavisi')
    expect(jobModelCardColour({ optionSignature: 'WebColor: Siyah | Beden: M', title: 'Elbise' })).toBe('Siyah')
    expect(jobModelCardColour({ optionSignature: 'Beden: M', title: 'Elbise' })).toBe('Elbise')
  })

  it('says the variant count only when it is known', () => {
    expect(jobModelGroupLine({ title: 'Örnek Elbise', modelCode: 'MZ900', cardCount: 7, variantCount: 21 })).toBe('MZ900 · 7 Renk · 21 Varyant · Örnek Elbise')
    expect(jobModelGroupLine({ title: 'Örnek Elbise', modelCode: 'MZ900', cardCount: 7, variantCount: null })).toBe('MZ900 · 7 Renk · Örnek Elbise')
  })

  it('a model row stands for its cards’ jobs, a plain row for itself', () => {
    const card = { id: 'b' }
    expect(rowJobs({ id: 'a', modelGroup: { cards: [{ job: card }] } })).toEqual([card])
    expect(rowJobs({ id: 'a', modelGroup: null })).toEqual([{ id: 'a', modelGroup: null }])
  })
})
