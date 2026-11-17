// Category attributes and option values: selecting, adding a value, editing a row's options, Web Color mode.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useState } from 'react'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { toggleProductAttributeValue } from '../attribute-selection'
import { normalizeVariantOptionValue, parseVariantOptionSignature } from '../variant-option-matching'
import { isOptionRequirement, optionSignatureFromOptions, type CategoryRequirement } from './publication-readiness'
import { type VariantOptionEditChange } from './VariantOptionEditDialog'
import { isWebColorOptionName, normalizeVariantOptionName } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { VariantDraft } from './editor-types'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import type { EditorDataResult } from './use-editor-data'
import type { EditorSeedResult } from './use-editor-seed'
import type { VariantRowDragResult } from './use-variant-row-drag'
import type { VariantMediaAutoSaveResult } from './use-variant-media-autosave'
import type { EditorViewResult } from './use-editor-view'

export function useAttributeActions(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult) {
  const { client, setNotice, form, attributeSelections, setAttributeSelections, variantAttributeIds, setVariantAttributeIds, variantRows, setVariantRows, setAttributeValueQueries, setWebColorAutoEnabled, setManualWebColorValueId, showFeedback, setOptionEditRowKey, mappedRequirements, webColorRequirement, webColorValues } = c
  function toggleAttributeValue(attributeId: string, valueId: string) {
    const requirement = mappedRequirements.find(item => item.attributeId === attributeId)
    const alreadySelected = (attributeSelections[attributeId] ?? []).includes(valueId)
    if (!alreadySelected && requirement && isOptionRequirement(requirement) && !variantAttributeIds.includes(attributeId) && variantAttributeIds.length >= 2) {
      const message = 'Bir ürün en fazla 2 seçenek grubuyla varyantlanabilir.'; setNotice(message); showFeedback('Seçenek Grubu Sınırı', message, 'error'); return
    }
    setAttributeSelections(current => {
      const next = toggleProductAttributeValue(current, attributeId, valueId, Boolean(requirement && !isOptionRequirement(requirement)))
      const nextValues = next[attributeId] ?? []
      if (requirement && isOptionRequirement(requirement)) {
        setVariantAttributeIds(currentAxes => nextValues.length ? currentAxes.includes(attributeId) ? currentAxes : [...currentAxes, attributeId] : currentAxes.filter(id => id !== attributeId))
      }
      return next
    })
  }

  // "… Değerini Ekle" under an option group's search box: the typed value joins the panel attribute (Renk, Beden) and is
  // selected here; the category's other forms see it on their next load. No marketplace value is mapped to it.
  const [addingOptionValueFor, setAddingOptionValueFor] = useState<string | null>(null)
  async function addOptionValue(requirement: CategoryRequirement, value: string) {
    if (addingOptionValueFor) return
    setAddingOptionValueFor(requirement.attributeId)
    try {
      const attribute = await hubApi<ApiAnswer<'postCatalogAttributesByIdValues'>>(`/catalog/attributes/${requirement.attributeId}/values`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify([{ value, sortOrder: requirement.attribute.values.length }]) })
      const activeValues = attribute.values.filter(item => item.isActive).sort((left, right) => left.sortOrder - right.sortOrder)
      const created = activeValues.find(item => normalizeVariantOptionValue(item.value) === normalizeVariantOptionValue(value))
      client.setQueryData<CategoryRequirement[]>(queryKeys.categoryRequirements(form.categoryId), current => current?.map(item => item.attributeId !== requirement.attributeId ? item : { ...item, attribute: { ...item.attribute, values: activeValues.map(({ id, value: label }) => ({ id, value: label })) } }))
      void client.invalidateQueries({ queryKey: queryKeys.categoryRequirements(), refetchType: 'none' })
      setAttributeValueQueries(current => ({ ...current, [requirement.attributeId]: '' }))
      if (created && !(attributeSelections[requirement.attributeId] ?? []).includes(created.id)) toggleAttributeValue(requirement.attributeId, created.id)
      showFeedback('Değer Eklendi', `“${value}” ${requirement.attribute.name} seçeneklerine eklendi ve seçildi. Pazaryeri eşleştirmesini Eşleştirmeler › Kategori Eşleme'de kategorinin Kategori Özellikleri penceresinden yapabilirsiniz.`, 'success')
    } catch (reason) {
      showFeedback('Değer Eklenemedi', reason instanceof Error ? reason.message : 'Değer eklenemedi.', 'error')
    } finally {
      setAddingOptionValueFor(null)
    }
  }

  function rowOptionEntries(row: VariantDraft) {
    const saved = Object.entries(row.options).filter(([, value]) => value.trim()).map(([name, value]) => ({ name, value }))
    return saved.length ? saved : parseVariantOptionSignature(row.optionSignature)
  }
  function optionAttributeFor(name: string) {
    const key = normalizeVariantOptionName(name)
    return mappedRequirements.find(item => isOptionRequirement(item) && !isWebColorOptionName(item.attribute.name) && normalizeVariantOptionName(item.attribute.name) === key)
  }
  function rowHasOptionValue(row: VariantDraft, name: string, value: string) {
    const key = normalizeVariantOptionName(name)
    return rowOptionEntries(row).some(entry => normalizeVariantOptionName(entry.name) === key && normalizeVariantOptionValue(entry.value) === normalizeVariantOptionValue(value))
  }
  // A changed option value goes to the row (or, with the switch on, every row holding the same value), with its
  // attribute value id so the saved variant attributes follow; the new value joins the option group's selection.
  function applyVariantOptionChanges(rowKey: string, changes: VariantOptionEditChange[], applyToAll: boolean) {
    const nextRows = variantRows.map(row => {
      const applying = changes.filter(change => row.key === rowKey || (applyToAll && rowHasOptionValue(row, change.name, change.from)))
      if (!applying.length) return row
      const options = Object.fromEntries(rowOptionEntries(row).map(entry => [entry.name, entry.value]))
      const attributeValueIds = { ...row.attributeValueIds }
      for (const change of applying) {
        const key = Object.keys(options).find(name => normalizeVariantOptionName(name) === normalizeVariantOptionName(change.name)) ?? change.name
        options[key] = change.to
        const attribute = optionAttributeFor(change.name)
        if (attribute) {
          if (change.valueId) attributeValueIds[attribute.attributeId] = change.valueId
          else delete attributeValueIds[attribute.attributeId]
        }
      }
      return { ...row, options, attributeValueIds, optionSignature: optionSignatureFromOptions(options) || row.optionSignature }
    })
    setVariantRows(nextRows)
    setAttributeSelections(current => {
      const next = { ...current }
      for (const change of changes) {
        const attribute = optionAttributeFor(change.name)
        if (!attribute) continue
        // The old value leaves the selection once no row holds it, so regenerating variants does not bring it back.
        const oldValueId = attribute.attribute.values.find(value => normalizeVariantOptionValue(value.value) === normalizeVariantOptionValue(change.from))?.id
        const oldStillUsed = nextRows.some(row => oldValueId && row.attributeValueIds[attribute.attributeId] === oldValueId)
        next[attribute.attributeId] = [...new Set([...(current[attribute.attributeId] ?? []), ...(change.valueId ? [change.valueId] : [])])]
          .filter(valueId => valueId !== oldValueId || oldStillUsed)
      }
      return next
    })
    setOptionEditRowKey(null)
  }
  function toggleWebColorAuto(enabled: boolean) {
    setWebColorAutoEnabled(enabled)
    if (enabled) {
      setManualWebColorValueId('')
      return
    }
    const selected = webColorRequirement ? attributeSelections[webColorRequirement.attributeId] ?? [] : []
    setManualWebColorValueId(current => current || selected[0] || webColorValues[0]?.id || '')
  }
  return { toggleAttributeValue, addingOptionValueFor, setAddingOptionValueFor, addOptionValue, rowOptionEntries, optionAttributeFor, rowHasOptionValue, applyVariantOptionChanges, toggleWebColorAuto }
}
export type AttributeActionsResult = ReturnType<typeof useAttributeActions>
