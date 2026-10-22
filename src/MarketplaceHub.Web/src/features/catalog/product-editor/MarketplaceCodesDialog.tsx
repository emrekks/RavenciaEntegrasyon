import { useState, type KeyboardEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi } from '../../../shared/api'
import { DialogSurface, UiIcon } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { platformLogoSource } from '../../../shared/platform-logos'

export type MarketplaceCodeConnection = { connectionId: string; platformCode: string; displayName: string }
export type MarketplaceCodes = {
  connections: MarketplaceCodeConnection[]
  variants: Array<{ variantId: string; sku: string; barcode: string | null; codes: MarketplaceCodeLink[] }>
}
// lastSeenAt: when a product import last read the code at the marketplace; healthStatus NOT_FOUND / REJECTED: it no
// longer knows the code (healthReason says why).
export type MarketplaceCodeLink = { connectionId: string; externalId: string; externalBarcode: string | null; externalSku: string | null; lastSeenAt?: string | null; healthStatus?: string | null; healthReason?: string | null }
export type MarketplaceCodeRow = { variantId: string; label: string }
export type MarketplaceCodeState = keyof typeof codeStateLabels

// What each marketplace calls the code a variant is linked by: Hepsiburada's SKU (HBCV…), Shopify's Variant ID.
const platformCodeNames: Record<string, string> = { HEPSIBURADA: 'SKU', SHOPIFY: 'Variant ID' }
const platformNames: Record<string, string> = { HEPSIBURADA: 'Hepsiburada', SHOPIFY: 'Shopify' }
const codePlaceholders: Record<string, string> = { HEPSIBURADA: 'HBCV…', SHOPIFY: 'Variant ID' }
const codeStateLabels = { linked: 'Bağlı', unverified: 'Doğrulanmadı', broken: 'Koptu', empty: 'Bağlı değil', changed: 'Değişti', invalid: 'Hatalı' } as const

// What a cell says about its link: a refused or typed-over code first, then the saved code's health. "Bağlı" only for
// a code a product import read at the marketplace; one never read yet is "Doğrulanmadı", one it no longer knows "Koptu".
export function marketplaceCodeState(input: { error?: string; typed: string; saved: string; link?: MarketplaceCodeLink | null }): MarketplaceCodeState {
  if (input.error) return 'invalid'
  if (input.typed.trim() !== input.saved) return 'changed'
  if (!input.saved) return 'empty'
  if (input.link?.healthStatus === 'NOT_FOUND' || input.link?.healthStatus === 'REJECTED') return 'broken'
  return input.link?.lastSeenAt ? 'linked' : 'unverified'
}

// The column a connection's codes fill: "SKU" / "Variant ID" under the platform's logo, with the connection name when
// a platform has more than one connection.
export function marketplaceCodeColumnLabel(connection: MarketplaceCodeConnection, connections: MarketplaceCodeConnection[]) {
  const label = platformCodeNames[connection.platformCode] ?? 'Kod'
  return connections.filter(item => item.platformCode === connection.platformCode).length > 1 ? `${label} · ${connection.displayName}` : label
}

// The code a connection holds for a variant: the link id the import stored or a person typed (Hepsiburada SKU, Shopify
// Variant ID).
export function marketplaceCodeFor(codes: MarketplaceCodes, variantId: string, connectionId: string) {
  return marketplaceCodeLink(codes, variantId, connectionId)?.externalId ?? null
}

export function marketplaceCodeLink(codes: MarketplaceCodes, variantId: string, connectionId: string) {
  return codes.variants.find(variant => variant.variantId === variantId)?.codes.find(code => code.connectionId === connectionId) ?? null
}

// The server names a cell's error by "{variantId}:{connectionId}".
export function marketplaceCodeKey(variantId: string, connectionId: string) {
  return `${variantId}:${connectionId}`
}

// The cells whose typed code differs from the saved one, as the save request lists them.
export function changedMarketplaceCodes(codes: MarketplaceCodes, drafts: Record<string, string>) {
  return codes.variants.flatMap(variant => codes.connections.flatMap(connection => {
    const draft = drafts[marketplaceCodeKey(variant.variantId, connection.connectionId)]
    if (draft === undefined) return []
    const saved = marketplaceCodeFor(codes, variant.variantId, connection.connectionId) ?? ''
    return draft.trim() === saved ? [] : [{ variantId: variant.variantId, connectionId: connection.connectionId, code: draft.trim() }]
  }))
}

function CodeHead({ connection, connections }: { connection: MarketplaceCodeConnection; connections: MarketplaceCodeConnection[] }) {
  const logo = platformLogoSource(connection.platformCode)
  return <span className="marketplace-code-head">
    {logo && <img src={logo} alt="" />}
    <span className="sr-only">{platformNames[connection.platformCode] ?? connection.platformCode} </span>
    <span>{marketplaceCodeColumnLabel(connection, connections)}</span>
  </span>
}

// Hepsiburada SKUs and Shopify Variant IDs of the variants the editor shows, as the last product import read them or a
// person typed them. Opened from one variant's row, it shows that variant's codes on each connection. A typed code links
// the variant, an emptied one removes its link.
export function MarketplaceCodesDialog({ productId, rows, onClose }: { productId: string; rows: MarketplaceCodeRow[]; onClose: () => void }) {
  const client = useQueryClient()
  const codes = useQuery({ queryKey: ['product-marketplace-codes', productId], queryFn: () => hubApi<MarketplaceCodes>(`/products/${productId}/marketplace-codes`, { cache: 'no-store' }) })
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [cellErrors, setCellErrors] = useState<Record<string, string>>({})
  const [status, setStatus] = useState<{ kind: 'success' | 'error'; message: string } | null>(null)
  const data = codes.data
  const changes = data ? changedMarketplaceCodes(data, drafts) : []
  const save = useMutation({
    mutationFn: () => hubApi<MarketplaceCodes>(`/products/${productId}/marketplace-codes`, { method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ codes: changes }) }),
    onSuccess: next => {
      client.setQueryData(['product-marketplace-codes', productId], next)
      setDrafts({})
      setCellErrors({})
      setStatus({ kind: 'success', message: `${changes.length.toLocaleString('tr-TR')} kod kaydedildi.` })
    },
    onError: reason => {
      const fields = reason instanceof ApiRequestError ? reason.fieldErrors ?? {} : {}
      setCellErrors(Object.fromEntries(Object.entries(fields).map(([key, messages]) => [key, messages[0] ?? ''])))
      setStatus({ kind: 'error', message: reason instanceof Error ? reason.message : 'Kodlar kaydedilemedi.' })
    }
  })
  const connections = data?.connections ?? []
  const knownVariants = new Set(data?.variants.map(variant => variant.variantId) ?? [])
  const single = rows.length === 1 ? rows[0] : null
  const canSave = changes.length > 0 && !save.isPending

  // Enter saves; inside the product form it must not submit the product.
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'Enter') return
    event.preventDefault()
    if (canSave) save.mutate()
  }
  // A cell's code as saved and as typed, and what that means for the link (marketplaceCodeState).
  const cell = (variantId: string, connection: MarketplaceCodeConnection) => {
    const key = marketplaceCodeKey(variantId, connection.connectionId)
    const link = data ? marketplaceCodeLink(data, variantId, connection.connectionId) : null
    const saved = link?.externalId ?? ''
    const value = drafts[key] ?? saved
    const error = cellErrors[key]
    const state = marketplaceCodeState({ error, typed: value, saved, link })
    return { key, value, error, state, link }
  }
  const codeField = (variantId: string, connection: MarketplaceCodeConnection, label: string) => {
    const { key, value, error, state, link } = cell(variantId, connection)
    const known = knownVariants.has(variantId)
    return <>
      <input className={`marketplace-code-input technical-field${state === 'changed' ? ' is-changed' : state === 'broken' ? ' is-broken' : ''}`} value={value} placeholder={codePlaceholders[connection.platformCode] ?? 'Kod'} autoComplete="off" spellCheck={false}
        disabled={!known || save.isPending} title={!known ? 'Önce ürünü kaydedin.' : state === 'broken' ? link?.healthReason ?? 'Pazaryeri bu kodu tanımıyor.' : undefined} aria-label={label} aria-invalid={error || state === 'broken' ? true : undefined}
        onChange={event => {
          const next = event.target.value
          setDrafts(current => ({ ...current, [key]: next }))
          setStatus(null)
          if (error) setCellErrors(current => { const rest = { ...current }; delete rest[key]; return rest })
        }}
        onKeyDown={onKeyDown} />
      {error && <small className="marketplace-code-error" role="alert">{error}</small>}
    </>
  }
  const fieldLabel = (variantLabel: string, connection: MarketplaceCodeConnection) => `${variantLabel} ${platformNames[connection.platformCode] ?? connection.platformCode} ${marketplaceCodeColumnLabel(connection, connections)}`

  return <div className="workspace-modal-backdrop marketplace-codes-backdrop" role="presentation" onMouseDown={onClose}>
    <DialogSurface as="section" onClose={onClose} className="workspace-modal marketplace-codes-modal" aria-labelledby="marketplace-codes-title" onMouseDown={event => event.stopPropagation()}>
      <header><div><p className="eyebrow">{single ? 'Varyant Eşleştirme' : 'Varyantlar'}</p><h2 id="marketplace-codes-title">{single ? single.label : 'Pazaryeri Kodları'}</h2><p>{single ? 'Bu varyantın pazaryerlerindeki kodları. Fiyat ve stok bu kodlarla gönderilir.' : 'Her varyantın Hepsiburada SKU’su ve Shopify Variant ID’si. Fiyat ve stok bu kodlarla gönderilir.'}</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header>
      <div className="marketplace-codes-body">
        {codes.isLoading ? <p className="marketplace-codes-note">Kodlar yükleniyor…</p>
          : codes.isError ? <p className="marketplace-codes-note" role="alert">Pazaryeri kodları okunamadı.</p>
            : !connections.length ? <p className="marketplace-codes-note">Etkin Hepsiburada veya Shopify bağlantısı yok.</p>
              : single ? <ul className="marketplace-code-list">{connections.map(connection => {
                const { state, link } = cell(single.variantId, connection)
                const seenAt = state === 'linked' && link?.lastSeenAt ? new Date(link.lastSeenAt).toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'Europe/Istanbul' }) : null
                const logo = platformLogoSource(connection.platformCode)
                const platform = platformNames[connection.platformCode] ?? connection.platformCode
                const named = connection.displayName.trim().toLocaleLowerCase('tr-TR') !== platform.toLocaleLowerCase('tr-TR')
                return <li key={connection.connectionId} className={`marketplace-code-row is-${state}`}>
                  <span className="marketplace-code-platform">
                    {logo ? <img src={logo} alt="" /> : <span className="marketplace-code-logo-fallback" aria-hidden="true">{platform.slice(0, 1)}</span>}
                    <span className="marketplace-code-name">
                      <strong>{platform}<em className={`marketplace-code-state is-${state}`}>{codeStateLabels[state]}</em></strong>
                      <small>{platformCodeNames[connection.platformCode] ?? 'Kod'}{named ? ` · ${connection.displayName}` : ''}{seenAt ? ` · Son görülme: ${seenAt}` : ''}</small>
                    </span>
                  </span>
                  <span className="marketplace-code-field">{codeField(single.variantId, connection, fieldLabel(single.label, connection))}</span>
                  {state === 'broken' && <small className="marketplace-code-reason">{link?.healthReason ?? 'Pazaryeri bu kodu tanımıyor.'} Fiyat ve stok bu koda gönderilmez; kodu düzeltip kaydedin.</small>}
                  {state === 'unverified' && <small className="marketplace-code-reason is-info">Kod henüz pazaryerinden okunmadı; bir sonraki ürün aktarımında doğrulanır.</small>}
                </li>
              })}</ul>
              : <div className="marketplace-codes-table-wrap"><table className="marketplace-codes-table">
                <thead><tr><th scope="col">Varyant</th>{connections.map(connection => <th scope="col" key={connection.connectionId}><CodeHead connection={connection} connections={connections} /></th>)}</tr></thead>
                <tbody>{rows.map(row => <tr key={row.variantId}><th scope="row">{row.label}</th>{connections.map(connection => <td key={connection.connectionId}>{codeField(row.variantId, connection, fieldLabel(row.label, connection))}</td>)}</tr>)}</tbody>
              </table></div>}
        <p className="marketplace-codes-note">Kodu yazıp kaydedin; boşaltılan kod eşleşmeyi kaldırır. Yeni kodun stok kodu ve barkodu bir sonraki ürün aktarımında okunur.</p>
        {status && <p className={`marketplace-codes-status is-${status.kind}`} role={status.kind === 'error' ? 'alert' : 'status'}>{status.message}</p>}
      </div>
      <footer><button type="button" className="secondary" onClick={onClose}>Kapat</button><button type="button" disabled={!canSave} onClick={() => save.mutate()}>{save.isPending ? 'Kaydediliyor…' : changes.length ? `Kaydet (${changes.length.toLocaleString('tr-TR')})` : 'Kaydet'}</button></footer>
    </DialogSurface>
  </div>
}
