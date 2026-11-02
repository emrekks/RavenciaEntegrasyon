import { describe, expect, it } from 'vitest'
import { classifyPublicationAttributeIssues } from './publication-attribute-readiness'
import { valueStillNeedsMapping } from './value-mapping-rule'

const active = (externalId: string) => externalId !== 'gone'
const saved = (externalId: string, status = 'VERIFIED', snapshotId = 'v2') => ({ localId: 'murdum', externalId, status, snapshotId })

describe('valueStillNeedsMapping', () => {
  it('follows the publish check: verified, on the current list, still active; a fresh pick counts at once', () => {
    expect(valueStillNeedsMapping('', undefined, 'v2', active)).toBe(true)
    expect(valueStillNeedsMapping('7006', undefined, 'v2', active)).toBe(false)
    expect(valueStillNeedsMapping('7006', saved('7006'), 'v2', active)).toBe(false)
    expect(valueStillNeedsMapping('7006', saved('7006', 'VERIFIED', 'v1'), 'v2', active)).toBe(true)
    expect(valueStillNeedsMapping('7006', saved('7006', 'SUGGESTED'), 'v2', active)).toBe(true)
    expect(valueStillNeedsMapping('gone', saved('gone'), 'v2', active)).toBe(true)
  })
})

describe('the publish check on Web Color values', () => {
  it('names each colour without a current Web Color value, and an outdated one as such', () => {
    const result = classifyPublicationAttributeIssues({
      selectedAttributes: [{ attributeId: 'renk', name: 'Renk', isRequired: true, values: [{ id: 'murdum', label: 'Mürdüm' }, { id: 'kiremit', label: 'Kiremit' }, { id: 'siyah', label: 'Siyah' }], hasCustomValue: false }],
      remoteAttributes: [{ externalId: '47', name: 'Renk', isActive: true, isRequired: true, allowsCustomValue: true }, { externalId: '348', name: 'Web Color', isActive: true, isRequired: true }],
      attributeSnapshotId: 'a1',
      attributeMappings: [{ localId: 'renk', externalId: '47', snapshotId: 'a1', status: 'VERIFIED' }, { localId: 'renk', externalId: '348', snapshotId: 'a1', status: 'VERIFIED' }],
      valueReferencesByAttribute: { 348: { snapshotId: 'v2', items: [{ externalId: '7009', isActive: true }, { externalId: '7006', isActive: true }], mappings: [{ localId: 'siyah', externalId: '7009', snapshotId: 'v2', status: 'VERIFIED' }, { localId: 'kiremit', externalId: '7006', snapshotId: 'v1', status: 'VERIFIED' }] } }
    })
    expect(result.requiredIssues.map(issue => [issue.attribute, issue.fix?.valueLabel, issue.state])).toEqual([['Web Color', 'Mürdüm', 'values'], ['Web Color', 'Kiremit', 'stale']])
  })
})
