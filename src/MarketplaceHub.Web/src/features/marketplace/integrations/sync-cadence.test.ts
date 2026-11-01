import { describe, expect, it } from 'vitest'
import { cadenceChoices, cadenceNote, cadenceShortText, type SyncCadence } from './integration-helpers'

const intervals: Array<[number, string]> = [[30, '30 saniye'], [60, '1 dakika'], [120, '2 dakika'], [600, '10 dakika'], [1800, '30 dakika'], [3600, '1 saat'], [21600, '6 saat']]
const cadence = (overrides: Partial<SyncCadence> = {}): SyncCadence => ({ effectiveIntervalSeconds: 120, defaultIntervalSeconds: 120, minimumIntervalSeconds: 60, maximumIntervalSeconds: 3600, basis: '', idleKind: 'WEBHOOK', idleBackoffEnabled: true, idleIntervalSeconds: 480, defaultIdleIntervalSeconds: 480, idleActive: false, stageFloor: false, ...overrides })

describe('sync cadence', () => {
  it('writes intervals short for the flow table', () => {
    expect(cadenceShortText(120)).toBe('2 dk')
    expect(cadenceShortText(3600)).toBe('1 sa')
    expect(cadenceShortText(86400)).toBe('1 gün')
    expect(cadenceShortText(45)).toBe('45 sn')
  })

  it('offers only the cadences within the flow bounds', () => {
    expect(cadenceChoices(intervals, 60, 3600, 120).map(([seconds]) => seconds)).toEqual([60, 120, 600, 1800, 3600])
  })

  it('keeps a stored cadence outside the bounds listed so the select shows it', () => {
    const choices = cadenceChoices(intervals, 900, 21600, 120)
    expect(choices[0]).toEqual([120, '2 dk (sınır dışı)'])
    expect(choices.map(([seconds]) => seconds)).toEqual([120, 1800, 3600, 21600])
  })

  it('says why a flow runs at another interval than its own', () => {
    expect(cadenceNote(cadence())).toBeNull()
    expect(cadenceNote(cadence({ idleActive: true }))).toBe('Bildirimler sağlıklı; seyrek okunuyor')
    expect(cadenceNote(cadence({ idleActive: true, idleKind: 'QUIET' }))).toBe('Yeni soru yok; seyrek okunuyor')
    expect(cadenceNote(cadence({ stageFloor: true }))).toBe('Test ortamı en çok 10 dakikada bir okunur')
  })
})
