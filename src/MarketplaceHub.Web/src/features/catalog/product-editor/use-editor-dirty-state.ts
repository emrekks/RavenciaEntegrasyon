// Unsaved changes (W6): the baseline taken right after the form was filled, compared field by field with the form as it
// is now (editor-reducer.ts). Owns: a baseline is taken only on request (after a seed, after the editor's own write, on a
// new product's open), media a create left behind are put back right after it, and leaving with unsaved changes asks first.
import { useEffect, useEffectEvent, useMemo, useRef } from 'react'
import { revealFieldError } from './field-error-reveal'
import { type PendingCreateMedia } from './pending-create-media'
import { changedEditorFields, editorDirtyFields, type DirtyFields } from './editor-reducer'
import { PendingProductMediaFile } from './editor-types'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'

export function useEditorDirtyState(c: EditorProps & EditorStateResult & EditorMediaPointerResult) {
  const { editProductId, duplicateProductId, form, attributeSelections, attributeTextValues, variantRows, setVariantRows, channelPricing, variantChannelPricing, mediaFiles, setMediaFiles, pendingFamilyMediaRemovals, familyMediaOrder, sizeChartUrl, removedVariantIds, baselineRequest, requestBaseline, baseline, takeBaseline, webColorAutoEnabled, manualWebColorValueId, fieldErrors, lastFieldReveal } = c
  useEffect(() => {
    if (!Object.values(fieldErrors).some(Boolean)) return
    const frame = requestAnimationFrame(() => { lastFieldReveal.current = Date.now(); revealFieldError(document.getElementById('product-creation-form')) })
    return () => cancelAnimationFrame(frame)
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fieldErrors])
  const currentDirtyFields = useMemo(() => editorDirtyFields({ form, variantRows, attributeSelections, attributeTextValues, familyMediaOrder, mediaFiles, removedVariantIds, pendingFamilyMediaRemovals, channelPricing, variantChannelPricing, webColorAutoEnabled, manualWebColorValueId, sizeChartUrl }), [attributeSelections, attributeTextValues, channelPricing, familyMediaOrder, form, manualWebColorValueId, mediaFiles, pendingFamilyMediaRemovals, removedVariantIds, sizeChartUrl, variantChannelPricing, variantRows, webColorAutoEnabled])
  // A new product compares with the empty form it opened with; a copy and an edit with the form right after seeding.
  const changedFields = useMemo(() => baseline === null ? [] : changedEditorFields(currentDirtyFields, baseline), [baseline, currentDirtyFields])
  const editorDirty = changedFields.length > 0
  const editorDirtyRef = useRef(editorDirty)
  editorDirtyRef.current = editorDirty
  // Media a create left behind (pending-create-media.ts): put back right after the baseline is taken, so they are
  // unsaved changes of the product's page.
  const restoreAfterSnapshot = useRef<PendingCreateMedia<PendingProductMediaFile> | null>(null)
  const captureBaseline = useEffectEvent(() => {
    // A copy, as the text snapshot it replaced was: a later change to the form's objects never moves the baseline.
    takeBaseline(JSON.parse(JSON.stringify(currentDirtyFields)) as DirtyFields)
    const restore = restoreAfterSnapshot.current
    if (!restore) return
    restoreAfterSnapshot.current = null
    setMediaFiles(current => [...current, ...restore.files.filter(file => !current.some(item => item.id === file.id))])
    setVariantRows(rows => rows.map(row => restore.variantMediaRefs[row.key] ? { ...row, mediaRefs: restore.variantMediaRefs[row.key] } : row))
  })
  useEffect(() => { if (baselineRequest) captureBaseline() }, [baselineRequest])
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (!editProductId && !duplicateProductId) requestBaseline() }, [duplicateProductId, editProductId])
  useEffect(() => {
    if (!editorDirty) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [editorDirty])
  return { currentDirtyFields, changedFields, editorDirty, editorDirtyRef, restoreAfterSnapshot }
}
export type EditorDirtyStateResult = ReturnType<typeof useEditorDirtyState>
