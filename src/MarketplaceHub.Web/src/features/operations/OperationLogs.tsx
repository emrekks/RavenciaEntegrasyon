import { lazy, Suspense, useRef } from 'react'
import { useSearchParams } from 'react-router'
import { Busy } from '../../shared/components/AsyncState'
import { TabSlidePanel } from '../../shared/tab-slide'
import { jobsLogView, withJobsLogView, type JobsLogView } from './jobs-url-state'
import ProductActivityLog, { type KeptActivityView } from './ProductActivityLog'
import type { KeptSendLogView } from './PriceStockSendLog'

// The send log loads with its own chunk the first time its view opens.
const PriceStockSendLog = lazy(() => import('./PriceStockSendLog'))

const views: Array<{ value: JobsLogView; label: string }> = [
  { value: 'activity', label: 'Ürün Hareketleri' },
  { value: 'sends', label: 'Pazaryeri Gönderimleri' }
]
const viewOrder = views.map(item => item.value)

// İşlem Takibi › Loglar: Ürün Hareketleri (every stock, price, status and return change of the products; the default)
// and Pazaryeri Gönderimleri (the price/stock lines sent to the marketplaces), switched by a segment above the view.
// Each view has its own filter card (filter-fields.tsx) above its list.
export function OperationLogs({ platformOptions, onOpenJob }: { platformOptions: string[]; onOpenJob: (jobId: string) => void }) {
  const [searchParams, setSearchParams] = useSearchParams()
  const view = jobsLogView(searchParams)
  // The filters of each view survive a switch to the other one.
  const keptActivity = useRef<KeptActivityView | null>(null)
  const keptSends = useRef<KeptSendLogView | null>(null)
  const select = (next: JobsLogView) => setSearchParams(current => withJobsLogView(current, next), { replace: true })
  return <>
    <div className="jobs-category-segment pal-view-segment" role="tablist" aria-label="Log görünümü">
      {views.map((item, index) => <button key={item.value} type="button" role="tab" tabIndex={item.value === view ? 0 : -1} aria-selected={item.value === view} className={item.value === view ? 'is-active' : undefined}
        onClick={() => select(item.value)}
        onKeyDown={event => {
          if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
          event.preventDefault()
          const next = views[(index + 1) % views.length]
          select(next.value)
          event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[views.indexOf(next)]?.focus()
        }}>
        <span className="jobs-category-segment-label" data-text={item.label}>{item.label}</span>
      </button>)}
    </div>
    <TabSlidePanel active={view} order={viewOrder} className="rv-tab-slide-grid" role="tabpanel">{view === 'sends'
      ? <Suspense fallback={<Busy text="Gönderim kayıtları yükleniyor…" />}><PriceStockSendLog key="sends" kept={keptSends} platformOptions={platformOptions} onOpenJob={onOpenJob} /></Suspense>
      : <ProductActivityLog key="activity" kept={keptActivity} platformOptions={platformOptions} />}</TabSlidePanel>
  </>
}

export default OperationLogs
