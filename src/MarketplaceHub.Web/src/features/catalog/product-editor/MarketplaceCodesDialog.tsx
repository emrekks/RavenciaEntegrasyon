import { useQuery } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { DialogSurface, UiIcon } from '../../../shared/components'

export type MarketplaceCodeConnection = { connectionId: string; platformCode: string; displayName: string }
export type MarketplaceCodes = {
  connections: MarketplaceCodeConnection[]
  variants: Array<{ variantId: string; sku: string; barcode: string | null; codes: Array<{ connectionId: string; externalId: string; externalBarcode: string | null; externalSku: string | null }> }>
}
export type MarketplaceCodeRow = { variantId: string; label: string }

const platformCodeLabels: Record<string, string> = { HEPSIBURADA: 'HB Kod', SHOPIFY: 'Shopify Kod' }

// The column a connection's codes fill: "HB Kod" / "Shopify Kod", with the connection name when a platform has
// more than one connection.
export function marketplaceCodeColumnLabel(connection: MarketplaceCodeConnection, connections: MarketplaceCodeConnection[]) {
  const label = platformCodeLabels[connection.platformCode] ?? `${connection.platformCode} Kod`
  return connections.filter(item => item.platformCode === connection.platformCode).length > 1 ? `${label} · ${connection.displayName}` : label
}

// The code a connection holds for a variant: the link id the import stored (Hepsiburada hbSku, Shopify variant id).
export function marketplaceCodeFor(codes: MarketplaceCodes, variantId: string, connectionId: string) {
  return codes.variants.find(variant => variant.variantId === variantId)?.codes.find(code => code.connectionId === connectionId)?.externalId ?? null
}

// Hepsiburada and Shopify codes of the variants the editor shows, as the last product import read them.
export function MarketplaceCodesDialog({ productId, rows, onClose }: { productId: string; rows: MarketplaceCodeRow[]; onClose: () => void }) {
  const codes = useQuery({ queryKey: ['product-marketplace-codes', productId], queryFn: () => hubApi<MarketplaceCodes>(`/products/${productId}/marketplace-codes`, { cache: 'no-store' }) })
  const connections = codes.data?.connections ?? []
  return <div className="workspace-modal-backdrop marketplace-codes-backdrop" role="presentation" onMouseDown={onClose}>
    <DialogSurface as="section" onClose={onClose} className="workspace-modal marketplace-codes-modal" aria-labelledby="marketplace-codes-title" onMouseDown={event => event.stopPropagation()}>
      <header><div><p className="eyebrow">VARYANTLAR</p><h2 id="marketplace-codes-title">Pazaryeri kodları</h2><p>Hepsiburada ve Shopify’ın her varyant için verdiği kodlar. Ürün aktarımında okunur; fiyat ve stok bu kodlarla gönderilir.</p></div><button type="button" className="modal-close" onClick={onClose} aria-label="Pencereyi kapat"><UiIcon name="close" /></button></header>
      <div className="marketplace-codes-body">
        {codes.isLoading ? <p className="marketplace-codes-note">Kodlar yükleniyor…</p>
          : codes.isError ? <p className="marketplace-codes-note" role="alert">Pazaryeri kodları okunamadı.</p>
            : !connections.length ? <p className="marketplace-codes-note">Etkin Hepsiburada veya Shopify bağlantısı yok.</p>
              : <div className="marketplace-codes-table-wrap"><table className="marketplace-codes-table">
                <thead><tr><th scope="col">Varyant</th>{connections.map(connection => <th scope="col" key={connection.connectionId}>{marketplaceCodeColumnLabel(connection, connections)}</th>)}</tr></thead>
                <tbody>{rows.map(row => <tr key={row.variantId}><th scope="row">{row.label}</th>{connections.map(connection => { const code = marketplaceCodeFor(codes.data!, row.variantId, connection.connectionId); return <td key={connection.connectionId} className={code ? 'technical-field' : 'is-empty'}>{code ?? '—'}</td> })}</tr>)}</tbody>
              </table></div>}
        <p className="marketplace-codes-note">Boş hücre: varyant o bağlantıdan henüz aktarılmadı. Kodlar “Ürün Eşleme” ile doldurulabilir.</p>
      </div>
      <footer><button type="button" className="secondary" onClick={onClose}>Kapat</button></footer>
    </DialogSurface>
  </div>
}
