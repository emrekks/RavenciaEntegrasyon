import type { ApiAnswer } from '../../shared/api/api-answer'
import { useEffect, useMemo, useRef } from 'react'
import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, settingsIfMatch, type Me } from '../../shared/api'
import { queryKeys } from '../../shared/api/query-keys'
import { lacksElevatedRole } from '../elevated-role'
import { normalizeShippingLabelSettings, type ShippingLabelSettings } from './shipping-label'

// Kargo etiketi şablonları belong to the account (owner, 26 Nov 2026): GET/PUT /settings/shipping-label-templates holds
// one list per tenant, so every computer and phone shows the same templates. Until then each browser kept its own list
// under ravencia.shippingLabelTemplates; that copy is never changed or deleted here. On the first successful read of
// the account's list, a device that still holds its own templates moves them in once: into an empty list as they are,
// into a list other devices already filled by name, the account's template winning and the device's one kept beside
// it as "<ad> (Bu Cihaz)". A template the account already holds as it is is not added twice. The device remembers the
// tenant it moved them to (ravencia.shippingLabelTemplatesMoved), so a template deleted later is not brought back. While
// the account's list cannot be read, the device's own copy is shown and nothing is written to the account.

export type ShippingLabelTemplate = { id: string; name: string; savedAt: string; settings: ShippingLabelSettings }

export const deviceShippingLabelTemplatesKey = 'ravencia.shippingLabelTemplates'
export const deviceShippingLabelTemplatesMovedKey = 'ravencia.shippingLabelTemplatesMoved'
export const shippingLabelTemplatesQueryKey = queryKeys.shippingLabelTemplates()
const templatesPath = '/settings/shipping-label-templates'
const maximumNameLength = 80

type TemplatesEnvelope = ApiAnswer<'getSettingsShippingLabelTemplates'>

function readTemplateList(value: unknown): ShippingLabelTemplate[] {
  const list = Array.isArray(value) ? value : value && typeof value === 'object' && Array.isArray((value as { templates?: unknown }).templates) ? (value as { templates: unknown[] }).templates : []
  return list
    .filter((item): item is { id: string; name: string; savedAt?: unknown; settings: unknown } => Boolean(item) && typeof item === 'object'
      && typeof (item as { id?: unknown }).id === 'string' && typeof (item as { name?: unknown }).name === 'string' && Boolean((item as { settings?: unknown }).settings))
    .map(item => ({ id: item.id, name: item.name, savedAt: typeof item.savedAt === 'string' ? item.savedAt : '', settings: normalizeShippingLabelSettings(item.settings) }))
}

/** This device's own templates (the list kept before they moved to the account); empty when none or unreadable. */
export function loadDeviceShippingLabelTemplates(): ShippingLabelTemplate[] {
  try {
    return readTemplateList(JSON.parse(localStorage.getItem(deviceShippingLabelTemplatesKey) ?? '[]'))
  } catch { return [] }
}

function movedToTenant(): string | null {
  try { return localStorage.getItem(deviceShippingLabelTemplatesMovedKey) } catch { return null }
}

function markMoved(tenantId: string) {
  try { localStorage.setItem(deviceShippingLabelTemplatesMovedKey, tenantId) } catch { /* A blocked store moves them again next time; the merge adds nothing twice. */ }
}

const sameSettings = (left: ShippingLabelSettings, right: ShippingLabelSettings) =>
  JSON.stringify(normalizeShippingLabelSettings(left)) === JSON.stringify(normalizeShippingLabelSettings(right))

function deviceName(name: string, taken: Set<string>) {
  for (let index = 1; ; index++) {
    const suffix = index === 1 ? ' (Bu Cihaz)' : ` (Bu Cihaz ${index})`
    const candidate = `${name.slice(0, maximumNameLength - suffix.length).trimEnd()}${suffix}`
    if (!taken.has(candidate)) return candidate
  }
}

/**
 * The account's templates with the device's added: the account's template wins a name, the device's one stays beside it
 * under "(Bu Cihaz)"; a device template the account holds as it is (same name and design) is not added again. Nothing
 * of the account's list is dropped or changed.
 */
export function mergeShippingLabelTemplates(account: readonly ShippingLabelTemplate[], device: readonly ShippingLabelTemplate[]) {
  const templates = [...account]
  const names = new Set(account.map(item => item.name))
  const ids = new Set(account.map(item => item.id))
  let added = 0
  for (const template of device) {
    if (templates.some(item => item.name === template.name && sameSettings(item.settings, template.settings))) continue
    const name = names.has(template.name) ? deviceName(template.name, names) : template.name.slice(0, maximumNameLength)
    let id = template.id
    for (let index = 2; ids.has(id); index++) id = `${template.id}-${index}`
    templates.push({ ...template, id, name })
    names.add(name)
    ids.add(id)
    added++
  }
  return { templates, added }
}

// A GET that started before a save may answer after it with the older list: the list this page already holds wins
// while its version is newer, so a late read never puts back a template just deleted or drops one just saved.
async function fetchAccount(client: QueryClient, signal: AbortSignal | undefined) {
  const fetched = await hubApi<TemplatesEnvelope>(templatesPath, { signal })
  const held = client.getQueryData<TemplatesEnvelope>(shippingLabelTemplatesQueryKey)
  return held && held.version > fetched.version ? held : fetched
}

function readAccount(client: QueryClient) {
  return client.fetchQuery({ queryKey: shippingLabelTemplatesQueryKey, queryFn: ({ signal }) => fetchAccount(client, signal), staleTime: 0 })
}

// The saved answer goes into the cache after any read still running is cancelled, so that read cannot overwrite it.
async function storeSaved(client: QueryClient, saved: TemplatesEnvelope) {
  await client.cancelQueries({ queryKey: shippingLabelTemplatesQueryKey })
  const held = client.getQueryData<TemplatesEnvelope>(shippingLabelTemplatesQueryKey)
  if (!held || held.version <= saved.version) client.setQueryData(shippingLabelTemplatesQueryKey, saved)
}

function writeAccount(templates: ShippingLabelTemplate[], version: number) {
  return hubApi<ApiAnswer<'putSettingsShippingLabelTemplates'>>(templatesPath, { method: 'PUT', headers: settingsIfMatch(version), body: JSON.stringify({ templates }) })
}

const changedMeanwhile = (error: unknown) => error instanceof ApiRequestError && (error.status === 412 || error.status === 428)

/**
 * Applies one change to the account's latest list and writes it over the version read; when another device saved
 * meanwhile, the change is applied once more to the newer list. Returns the saved answer.
 */
async function changeAccount(client: QueryClient, current: TemplatesEnvelope | undefined, change: (templates: ShippingLabelTemplate[]) => ShippingLabelTemplate[] | null) {
  let envelope = current ?? await readAccount(client)
  for (let attempt = 0; ; attempt++) {
    const next = change(readTemplateList(envelope.settings))
    if (next === null) return envelope
    try {
      await client.cancelQueries({ queryKey: shippingLabelTemplatesQueryKey })
      const saved = await writeAccount(next, envelope.version)
      await storeSaved(client, saved)
      return saved
    } catch (error) {
      if (attempt > 0 || !changedMeanwhile(error)) throw error
      envelope = await readAccount(client)
    }
  }
}

/**
 * Moves this device's templates into the account once (see the note at the top). Resolves to how many were added;
 * a refused or failed write leaves the device's copy and the mark as they were, to be tried at the next read.
 */
export async function moveDeviceTemplatesToAccount(client: QueryClient, read: TemplatesEnvelope, me: Pick<Me, 'role' | 'tenantId'> | undefined) {
  const tenantId = me?.tenantId
  if (!tenantId || movedToTenant() !== null) return 0
  const device = loadDeviceShippingLabelTemplates()
  if (device.length === 0) { markMoved(tenantId); return 0 }
  // Only the owner and administrators write the account's templates; another role keeps the device's copy for later.
  if (lacksElevatedRole(me?.role)) return 0
  let added = 0
  await changeAccount(client, read, templates => {
    const merged = mergeShippingLabelTemplates(templates, device)
    added = merged.added
    return merged.added > 0 ? merged.templates : null
  })
  markMoved(tenantId)
  return added
}

export function useShippingLabelTemplates() {
  const client = useQueryClient()
  const moveTried = useRef(false)
  const query = useQuery({
    queryKey: shippingLabelTemplatesQueryKey,
    queryFn: ({ signal }) => fetchAccount(client, signal),
    staleTime: 60_000,
    retry: 2,
    refetchOnWindowFocus: true
  })
  const read = query.isSuccess ? query.data : undefined
  useEffect(() => {
    if (!read || moveTried.current) return
    moveTried.current = true
    void moveDeviceTemplatesToAccount(client, read, client.getQueryData<Me>(queryKeys.me())).catch(() => { moveTried.current = false })
  }, [client, read])
  const deviceTemplates = useMemo(() => read ? [] : loadDeviceShippingLabelTemplates(), [read])
  const templates = useMemo(() => read ? readTemplateList(read.settings) : deviceTemplates, [read, deviceTemplates])

  // A save needs the account's list read: without it nothing is written, so a device that cannot read the account
  // never writes its own copy over it.
  function requireRead() {
    if (!read) throw new Error('Şablonlar hesaptan okunamadı; bağlantı gelince yeniden deneyin.')
    // The latest list this page holds (a save just made is in the cache before the next render).
    return client.getQueryData<TemplatesEnvelope>(shippingLabelTemplatesQueryKey) ?? read
  }

  async function saveTemplate(template: ShippingLabelTemplate) {
    await changeAccount(client, requireRead(), list => [...list.filter(item => item.name !== template.name), template])
  }

  async function deleteTemplate(id: string) {
    await changeAccount(client, requireRead(), list => list.some(item => item.id === id) ? list.filter(item => item.id !== id) : null)
  }

  return { templates, fromDevice: !read, loading: query.isPending, saveTemplate, deleteTemplate }
}
