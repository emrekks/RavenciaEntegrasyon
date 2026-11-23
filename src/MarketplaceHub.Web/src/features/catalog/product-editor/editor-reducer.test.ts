import { describe, expect, it } from 'vitest'
import { baselineWithSavedMedia, baselineWithWebColor, changedEditorFields, editorDirtyFields, editorReducer, editorStateDirty, initialEditorFields, initialEditorState, type EditorFields, type EditorState } from './editor-reducer'
import type { VariantDraft } from './editor-types'

const row: VariantDraft = { key: 'v1', optionSignature: 'Renk: Mavi', options: { Renk: 'Mavi' }, attributeValueIds: {}, sku: 'A', barcode: '', stock: 1, salePrice: 2, listPrice: 3, costPrice: 0, mediaRefs: ['url|a'] }
const fields = (extra: Partial<EditorFields> = {}): EditorFields => ({ ...initialEditorFields(), variantRows: [row], ...extra })
const changed = (left: EditorFields, right: EditorFields) => changedEditorFields(editorDirtyFields(left), editorDirtyFields(right))
const withBaseline = (state: EditorState) => editorReducer(state, { type: 'takeBaseline', baseline: editorDirtyFields(state.fields) })

describe('the unsaved-changes check, field by field', () => {
  it('ignores platform prices and versions shown in the rows', () => {
    expect(changed(fields({ variantRows: [{ ...row, platformStatuses: [], loadedVersion: 4 }] }), fields())).toEqual([])
    expect(changed(fields({ variantRows: [{ ...row, stock: 2 }] }), fields())).toEqual(['rows'])
  })

  it('counts the marketplace prices, the Web Color, the size chart and waiting files', () => {
    expect(changed(fields({ channelPricing: { ty: { listPrice: '150', salePrice: '120' } } }), fields())).toEqual(['channelPricing'])
    expect(changed(fields({ variantChannelPricing: { 'v1:ty': { listPrice: 150, salePrice: 150 } } }), fields())).toEqual(['variantChannelPricing'])
    expect(changed(fields({ webColorAutoEnabled: false, manualWebColorValueId: 'wc1' }), fields())).toEqual(['webColor'])
    expect(changed(fields({ sizeChartUrl: 'https://cdn/c.jpg' }), fields())).toEqual(['sizeChartUrl'])
    expect(changed(fields({ mediaFiles: [{ id: 'f1', file: new File([], 'a.jpg') }] }), fields())).toEqual(['pendingMediaFileCount'])
    // The order the stores were switched on in does not matter.
    const price = { listPrice: '1', salePrice: '1' }
    expect(changed(fields({ channelPricing: { x: price, y: price } }), fields({ channelPricing: { y: price, x: price } }))).toEqual([])
  })

  it('takes a seeded Web Color and the editor’s own picture writes into the baseline, nothing else', () => {
    const baseline = editorDirtyFields(fields({ form: { ...initialEditorFields().form, title: 'Eski', mediaUrls: 'a\nb' } }))
    expect(baselineWithWebColor(baseline, { autoEnabled: false, manualValueId: 'wc1' })).toEqual({ ...baseline, webColor: [false, 'wc1'] })
    const moved = baselineWithSavedMedia(baseline, { mediaUrls: value => value.split('\n').filter(item => item !== 'b').join('\n'), rowMediaRefs: () => [], familyMediaOrder: () => ['a'], sizeChartUrl: () => null })
    expect(moved.form).toEqual({ ...baseline.form, mediaUrls: 'a' })
    expect(moved.rows[0][10]).toEqual([])
    expect(moved.familyMediaOrder).toEqual(['a'])
    expect(moved.form.title).toBe('Eski')
  })
})

describe('editorReducer', () => {
  it('is clean until a baseline is taken, and dirty when a field differs from it', () => {
    let state = editorReducer(initialEditorState(), { type: 'setField', field: 'form', update: form => ({ ...form, title: 'A' }) })
    expect(editorStateDirty(state)).toBe(false)
    state = withBaseline(state)
    expect(editorStateDirty(state)).toBe(false)
    state = editorReducer(state, { type: 'setField', field: 'form', update: form => ({ ...form, title: 'B' }) })
    expect(editorStateDirty(state)).toBe(true)
    // Taking the edit back leaves nothing unsaved.
    state = editorReducer(state, { type: 'setField', field: 'form', update: form => ({ ...form, title: 'A' }) })
    expect(editorStateDirty(state)).toBe(false)
  })

  it('changes nothing for a field set to the value it holds', () => {
    const state = initialEditorState()
    expect(editorReducer(state, { type: 'setField', field: 'variantRows', update: rows => rows })).toBe(state)
  })

  it('counts the baseline requests and moves a baseline only once there is one', () => {
    const state = editorReducer(editorReducer(initialEditorState(), { type: 'requestBaseline' }), { type: 'requestWebColorBaseline' })
    expect([state.baselineRequest, state.webColorBaselineRequest]).toEqual([1, 1])
    expect(editorReducer(state, { type: 'adjustBaseline', adjust: baseline => ({ ...baseline, sizeChartUrl: 'x' }) })).toBe(state)
    const taken = withBaseline(state)
    expect(editorReducer(taken, { type: 'adjustBaseline', adjust: baseline => ({ ...baseline, sizeChartUrl: 'x' }) }).baseline?.sizeChartUrl).toBe('x')
  })
})
