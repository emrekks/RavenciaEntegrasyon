import { useQuery } from '@tanstack/react-query'
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { hubApi } from '../../../shared/api'
import { queryKeys } from '../../../shared/api/query-keys'
import { TitleHint, UiIcon } from '../../../shared/components'
import type { PlatformUpdateField } from './platform-update-fields'
import { platformUpdatePreviewMaxProducts, previewStores, sameAsMarketplace, unknownMarketplaceValue, type PreviewStore } from './platform-update-preview'

// "Pazaryerinde Değişecekler": the summary above Gönder of what the update will change on each picked store (read only,
// POST /products/update-preview). It sends nothing and never holds Gönder back; a failed or refused summary says so.
export function PlatformUpdatePreview({ productIds, connectionIds, fields: picked }: { productIds: string[]; connectionIds: string[]; fields: PlatformUpdateField[] }) {
  // Price and stock are not product information: they go apart and are not compared here.
  const fields = picked.filter(field => field !== 'PRICE' && field !== 'STOCK')
  const tooMany = productIds.length > platformUpdatePreviewMaxProducts
  const enabled = !tooMany && productIds.length > 0 && connectionIds.length > 0 && fields.length > 0
  const preview = useQuery({
    queryKey: queryKeys.productUpdatePreview(productIds, connectionIds, fields),
    queryFn: ({ signal }) => hubApi<ApiAnswer<'postProductsUpdatePreview'>>('/products/update-preview', { method: 'POST', body: JSON.stringify({ productIds, connectionIds, fields }), signal }),
    enabled,
    staleTime: 15_000,
    retry: false
  })
  if (!tooMany && !enabled) return null
  const stores = preview.data ? previewStores(preview.data.connections) : []
  return <section className="platform-update-section platform-update-preview" aria-labelledby="platform-update-preview-title" aria-busy={preview.isFetching || undefined}>
    <header className="platform-update-section-head">
      <div><h3 id="platform-update-preview-title">Pazaryerinde Değişecekler<TitleHint label="Pazaryerinde Değişecekler">Gönder, panelde kayıtlı değeri pazaryerindekinin yerine yazar (kaydedilmemiş değişiklikler burada görünmez). Burada her mağaza için gidecek değer, panelin bildiği son pazaryeri değeriyle karşılaştırılır; bilinmeyen değer “{unknownMarketplaceValue}” olarak gösterilir. Bu özet hiçbir şey göndermez.</TitleHint></h3><small>{tooMany ? `${productIds.length.toLocaleString('tr-TR')} ürün seçili` : preview.isPending ? 'Hazırlanıyor…' : `${stores.length.toLocaleString('tr-TR')} mağaza`}</small></div>
    </header>
    {tooMany
      ? <p className="platform-update-preview-note" role="note"><UiIcon name="info" size={14} /><span>Değişiklik özeti en çok {platformUpdatePreviewMaxProducts} ürün için gösterilir; Gönder bundan etkilenmez.</span></p>
      : preview.isError
        ? <p className="platform-update-preview-note is-error" role="alert"><UiIcon name="alert" size={14} /><span>Değişiklik özeti alınamadı: {preview.error instanceof Error ? preview.error.message : 'bilinmeyen hata'} Gönder bundan etkilenmez.</span></p>
        : preview.isPending
          ? <p className="platform-update-preview-note" role="status"><span>Değişiklik özeti hazırlanıyor…</span></p>
          : <div className="platform-update-preview-stores">{stores.map(store => <PreviewStoreRows key={store.connectionId} store={store} />)}</div>}
  </section>
}

function PreviewStoreRows({ store }: { store: PreviewStore }) {
  return <article className="platform-update-preview-store">
    <strong>{store.name}</strong>
    {store.nothing && <p className="platform-update-preview-line"><span>Gönderilecek bilgi yok</span></p>}
    {store.allSame && <p className="platform-update-preview-line is-same"><span>{sameAsMarketplace}</span></p>}
    {!store.allSame && <ul>
      {store.rows.map(row => <li key={row.field} className={`is-${row.state.toLowerCase()}`}>
        {row.changes.length
          ? <details><summary><b>{row.label}</b><span>{row.text}</span><UiIcon name="chevronDown" size={12} /></summary>
            <ol>{row.changes.map((change, index) => <li key={`${change.model}-${change.label}-${index}`}>
              <small>{change.model} · {change.label}{change.lengths ? ` · ${change.lengths}` : ''}</small>
              <span><s title="Pazaryerindeki">{change.before || '—'}</s><UiIcon name="arrowRight" size={12} /><ins title="Gidecek">{change.after || '—'}</ins></span>
            </li>)}</ol>
          </details>
          : <p className="platform-update-preview-line"><b>{row.label}</b><span>{row.text}</span></p>}
      </li>)}
      {store.unknownFields && <li className="is-unknown"><p className="platform-update-preview-line"><b>{store.unknownFields}</b><span>{unknownMarketplaceValue}</span></p></li>}
    </ul>}
    {store.problems.length > 0 && <details className="platform-update-preview-problems"><summary><UiIcon name="alert" size={12} /><span>{store.problems.length.toLocaleString('tr-TR')} kart için özet hazırlanamadı</span><UiIcon name="chevronDown" size={12} /></summary>
      <ul>{store.problems.map((problem, index) => <li key={`${problem.label}-${index}`}><b>{problem.label}</b> {problem.message}</li>)}</ul>
    </details>}
  </article>
}
