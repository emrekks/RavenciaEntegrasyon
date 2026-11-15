// Publish selection and Platformlarda Güncelle: the settings window, its saved choice and Hızlı Güncelle.
import { platformUpdateWriteClosed } from './PlatformUpdateDialog'
import { platformUpdateChoiceRecord, platformUpdatePlan, platformUpdatePreferencesFrom, platformUpdateStoresFrom, readQuickPlatformUpdateChoice, type PlatformUpdateField, type PlatformUpdatePreferences } from './platform-update-fields'
import { productUpdateCapabilities, quickPlatformUpdateTargets } from '../platform-update-targets'
import { isProductPublicationConnection } from '../shared'
import { publishPlatformName } from './editor-helpers'
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
import type { AttributeActionsResult } from './use-attribute-actions'
import type { VariantActionsResult } from './use-variant-actions'
import type { VariantCodeActionsResult } from './use-variant-code-actions'
import type { EditorMediaActionsResult } from './use-editor-media-actions'

export function usePlatformUpdate(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult & VariantRowDragResult & VariantMediaAutoSaveResult & EditorViewResult & AttributeActionsResult & VariantActionsResult & VariantCodeActionsResult & EditorMediaActionsResult) {
  const { selectedChannelIds, setSelectedChannelIds, setPlatformUpdateDialogOpen, platformUpdateConnectionIds, setPlatformUpdateConnectionIds, platformUpdatePreferences, setPlatformUpdatePreferences, setSavingPlatformUpdateChoice, platformUpdateChoice, pendingPlatformUpdate, showFeedback, connections, blockedByVariantCodeRules, productUpdateTargets } = c
  function updateChannel(id: string) {
    const selected = selectedChannelIds.includes(id)
    const platformCode = connections.data?.items.find(item => item.id === id)?.platformCode
    const name = platformCode ? publishPlatformName(platformCode) : 'Pazaryeri'
    setSelectedChannelIds(current => selected ? current.filter(item => item !== id) : [...current, id])
    showFeedback(selected ? 'Platform Seçimden Çıkarıldı' : 'Platform Seçildi', selected ? `${name} yayın seçiminden çıkarıldı; Yayınla ona gönderim yapmaz.` : `${name} yayın için seçildi; Yayınla ile gönderim kuyruğuna alınır.`, 'info')
  }
  function updateableProductConnectionIds() {
    return (connections.data?.items ?? []).filter(item => isProductPublicationConnection(item) && productUpdateCapabilities(item.platformCode).writable && item.status.trim().toUpperCase() === 'ACTIVE').map(item => item.id)
  }
  function openPlatformUpdateDialog() {
    const updateableIds = updateableProductConnectionIds()
    // The stores picked last time, as they were left; the very first time, the ones picked for this product.
    setPlatformUpdateConnectionIds(platformUpdateStoresFrom(platformUpdateChoice.record, updateableIds, selectedChannelIds))
    setPlatformUpdatePreferences(platformUpdatePreferencesFrom(platformUpdateChoice.record))
    setPlatformUpdateDialogOpen(true)
  }
  // The settings window edits the choice Hızlı Güncelle sends; Kaydet keeps it (on the server, for every device), Vazgeç
  // leaves the saved one.
  function changePlatformUpdatePreferences(next: PlatformUpdatePreferences) {
    setPlatformUpdatePreferences(next)
  }
  async function storePlatformUpdateChoice(fields: PlatformUpdateField[]) {
    setSavingPlatformUpdateChoice(true)
    try {
      await platformUpdateChoice.save(platformUpdateChoiceRecord({ ...platformUpdatePreferences, fields }, platformUpdateConnectionIds))
      return true
    } catch (reason) {
      showFeedback('Hızlı Güncelle Ayarı Kaydedilemedi', reason instanceof Error ? reason.message : 'Seçim kaydedilemedi; yeniden deneyin.', 'error')
      return false
    } finally {
      setSavingPlatformUpdateChoice(false)
    }
  }
  async function savePlatformUpdateChoice(fields: PlatformUpdateField[]) {
    if (!await storePlatformUpdateChoice(fields)) return
    setPlatformUpdateDialogOpen(false)
    showFeedback('Hızlı Güncelle Ayarı Kaydedildi', 'Hızlı Güncelle bundan sonra seçtiğiniz bilgileri seçtiğiniz platformlara pencere açmadan gönderir.', 'success')
  }
  // Gönder in the settings window: keeps the choice exactly as Kaydet does, then sends it as Hızlı Güncelle does. A choice
  // that could not be kept does not stop the send (the failure is said).
  function sendPlatformUpdateChoice(fields: PlatformUpdateField[]) {
    if (blockedByVariantCodeRules()) return
    void storePlatformUpdateChoice(fields)
    queuePlatformUpdate(platformUpdateConnectionIds, fields)
  }
  function queuePlatformUpdate(connectionIds: string[], fields: PlatformUpdateField[], quick = false) {
    const updateableIds = new Set(updateableProductConnectionIds())
    const safeConnectionIds = [...new Set(connectionIds)].filter(id => updateableIds.has(id))
    if (!safeConnectionIds.length) {
      showFeedback('Platform Seçilmedi', 'Güncelleme için desteklenen ve aktif bir platform seçin.', 'error')
      return
    }
    const formElement = document.getElementById('product-creation-form')
    if (!(formElement instanceof HTMLFormElement)) {
      showFeedback('Güncelleme Başlatılamadı', 'Ürün formu bulunamadı; güncelleme başlatılamadı.', 'error')
      return
    }
    pendingPlatformUpdate.current = { connectionIds: safeConnectionIds, fields, quick }
    setPlatformUpdateDialogOpen(false)
    formElement.requestSubmit()
  }
  // Hızlı Güncelle: the saved choice of the settings window, sent at once (a manual write; the automatic price/stock
  // switch does not apply). A choice that reaches no store or sends nothing opens the window instead.
  function startQuickPlatformUpdate() {
    // The saved choice is read from the server; until it is, the window opens instead of sending a guess.
    if (!platformUpdateChoice.loaded) {
      openPlatformUpdateDialog()
      showFeedback('Hızlı Güncelle Ayarı Okunamadı', platformUpdateChoice.failed ? 'Kayıtlı seçim okunamadı; gönderilecek bilgileri ve platformları kontrol edip Gönder’e basın.' : 'Kayıtlı seçim yükleniyor; gönderilecek bilgileri ve platformları kontrol edip Gönder’e basın.', 'info')
      return
    }
    const updateableIds = updateableProductConnectionIds()
    const { connectionIds, fields } = readQuickPlatformUpdateChoice(platformUpdateChoice.record, updateableIds, quickPlatformUpdateTargets(selectedChannelIds, updateableIds))
    const platformCodes = new Map((connections.data?.items ?? []).map(item => [item.id, item.platformCode]))
    // A store whose write switch is closed would only fail after the save: the window shows which and where it opens.
    const closed = productUpdateTargets.filter(target => connectionIds.includes(target.connection.id) && platformUpdateWriteClosed(target))
    if (closed.length) {
      openPlatformUpdateDialog()
      showFeedback('Pazaryerine Yazma Kapalı', `${closed.map(target => target.connection.displayName).join(', ')} için yazma kapalı; Entegrasyonlar › mağaza › Pazaryerine Yazma’dan açın ya da mağazayı seçimden çıkarın.`, 'info')
      return
    }
    if (!connectionIds.length) {
      openPlatformUpdateDialog()
      showFeedback('Platform Seçin', 'Hızlı Güncelle için güncellenecek platformları seçip Kaydet’e basın.', 'info')
      return
    }
    if (!connectionIds.some(id => platformUpdatePlan(platformCodes.get(id) ?? '', fields).sends.length > 0)) {
      openPlatformUpdateDialog()
      showFeedback('Bilgi Seçin', 'Seçili platformlara gidecek bir bilgi seçilmedi; gönderilecek bilgileri seçip Kaydet’e basın.', 'info')
      return
    }
    queuePlatformUpdate(connectionIds, fields, true)
  }
  return { updateChannel, updateableProductConnectionIds, openPlatformUpdateDialog, changePlatformUpdatePreferences, storePlatformUpdateChoice, savePlatformUpdateChoice, sendPlatformUpdateChoice, queuePlatformUpdate, startQuickPlatformUpdate }
}
export type PlatformUpdateResult = ReturnType<typeof usePlatformUpdate>
