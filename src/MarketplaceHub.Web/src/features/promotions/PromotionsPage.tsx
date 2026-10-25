import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { loadAllPages } from '../../shared/api'
import { Busy, ErrorBox } from '../../shared/components'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { CommissionTariffsPanel, commissionTariffPlatforms } from '../marketplace/integrations/CommissionTariffsPanel'
import { PromotionOffersPanel, promotionTitles, type PromotionKind } from '../marketplace/integrations/PromotionOffersPanel'
import { bySalesChannel, platformName } from '../reports/report-format'
import '../reports/reports.css'

type Connection = { id: string; platformCode: string; displayName: string; status: string; environment: string }

// The promotion pages: the weekly commission tariff first, then Trendyol's offers; the open one is kept in the address
// (?tab=…) so a refresh or a shared link lands on it.
type Section = 'TARIFF' | PromotionKind
const sections: Array<[Section, string]> = [['TARIFF', 'Ürün Komisyon Tarifesi'], ...(Object.entries(promotionTitles) as Array<[PromotionKind, string]>)]
const tabKeys: Record<Section, string> = { TARIFF: 'tariff', PLUS: 'plus', ADVANTAGE: 'advantage', FLASH: 'flash' }
export const sectionOf = (tab: string | null): Section => sections.find(([key]) => tabKeys[key] === tab)?.[0] ?? 'TARIFF'

// The stores whose files the panel reads: not deleted, not a sandbox, of a platform it reads them for.
export const promotionStores = (connections: Connection[]) => connections
  .filter(item => item.status !== 'DELETED' && item.environment !== 'SANDBOX' && commissionTariffPlatforms.has(item.platformCode.toUpperCase()))
  .sort((a, b) => bySalesChannel(a.platformCode, b.platformCode) || a.displayName.localeCompare(b.displayName, 'tr'))

// Promosyon Kârlılık Analizi: what each price the marketplace offers (its commission tariff bands, Plus, the advantage
// label, flash sales) leaves after commission and the product's cost, per store. The seller uploads the files; nothing
// is sent to the marketplace.
export function PromotionsPage() {
  const [params, setParams] = useSearchParams()
  const section = sectionOf(params.get('tab'))
  const connections = useQuery({ queryKey: ['connections', 'promotions'], queryFn: () => loadAllPages<Connection>('/connections') })
  const stores = promotionStores(connections.data?.items ?? [])
  const store = stores.find(item => item.id === params.get('store')) ?? stores[0]
  const go = (next: { tab?: Section; store?: string }) => {
    const query = new URLSearchParams(params)
    if (next.tab) query.set('tab', tabKeys[next.tab])
    if (next.store) query.set('store', next.store)
    setParams(query, { replace: true })
  }
  const platforms = [...new Set(stores.map(item => item.platformCode))]
  const sameStores = store ? stores.filter(item => item.platformCode === store.platformCode) : []
  return <section className="content reports-page promotions-page">
    <div className="page-heading reports-heading">
      <div><p className="eyebrow">Analiz</p><h1>Promosyon Kârlılık Analizi</h1><p className="reports-lead">Komisyon tarifesi bantları, Plus, Avantajlı Ürün Etiketi ve Flash fiyatlarında komisyon ve maliyet düştükten sonra ürün başına ne kaldığı.</p></div>
    </div>
    {connections.isLoading && <Busy />}
    {connections.isError && <ErrorBox error={connections.error} onRetry={() => void connections.refetch()} />}
    {connections.data && !store && <p className="reports-empty">Promosyon dosyası yüklenebilecek bir mağaza yok; şimdilik Trendyol dosyaları okunuyor.</p>}
    {store && <div className="reports-tariffs">
      <div className="promotions-bar">
        <div className="reports-segmented reports-tariff-platforms" role="group" aria-label="Pazaryeri">
          {platforms.map(code => <button key={code} type="button" aria-pressed={code === store.platformCode} onClick={() => go({ store: stores.find(item => item.platformCode === code)?.id })}>
            <PlatformSquareMark code={code} name={platformName(code)} />{platformName(code)}
          </button>)}
        </div>
        {sameStores.length > 1 && <label className="reports-field"><span>Mağaza</span>
          <select value={store.id} onChange={event => go({ store: event.target.value })}>
            {sameStores.map(item => <option key={item.id} value={item.id}>{item.displayName}</option>)}
          </select>
        </label>}
      </div>
      <div className="reports-segmented reports-promotion-tabs" role="group" aria-label="Promosyon sayfaları">
        {sections.map(([key, label]) => <button key={key} type="button" aria-pressed={key === section} onClick={() => go({ tab: key })}>{label}</button>)}
      </div>
      {section === 'TARIFF' ? <CommissionTariffsPanel key={store.id} connectionId={store.id} platformName={platformName(store.platformCode)} />
        : <PromotionOffersPanel key={`${store.id}-${section}`} connectionId={store.id} kind={section} />}
    </div>}
  </section>
}
