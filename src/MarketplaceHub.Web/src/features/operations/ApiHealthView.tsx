import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { CardBackgroundIcon, type CardIconName, type CardTone } from '../../shared/components/CardBackgroundIcon'
import { RrIcon } from '../marketplace/returns-ui'
import { dashboardBootstrapQuery, IntegrationStatusPanel, knownCount, useIntegrationHealth } from '../dashboard/IntegrationStatusPanel'
import { ApiHealthSignalsPanel } from './ApiHealthSignalsPanel'
import { EmergencyStopButton, usePriceStockMonitor } from '../marketplace/integrations/PriceStockMonitorPanel'
import type { IntegrationFeedback } from '../marketplace/shared'
import './api-health.css'

export type ApiHealthStat = { key: string; icon: CardIconName; label: string; value: number | null; tone?: CardTone; to?: string }

/**
 * The summary over the panel: stores that need a look (as Entegrasyon Durumu marks them) and the last 24 hours' jobs.
 * A figure not read yet stays unknown ("—"), never zero; a colour only shows when there is something to see.
 */
export function apiHealthStats(attention: number | null, recentJobs: number | null, deadJobs: number | null, manualReviewJobs: number | null): ApiHealthStat[] {
  return [
    { key: 'attention', icon: 'connect', label: 'Sorunlu Bağlantı', value: attention, tone: attention ? 'warn' : undefined },
    { key: 'recent', icon: 'activity', label: 'Son 24 Saatte İstek', value: recentJobs, to: '/jobs' },
    { key: 'dead', icon: 'jobs', label: 'Tamamlanamayan İş', value: deadJobs, tone: deadJobs ? 'bad' : undefined, to: '/jobs?statu=DEAD&aralik=tum' },
    { key: 'review', icon: 'clock', label: 'Karar Bekleyen İş', value: manualReviewJobs, tone: manualReviewJobs ? 'warn' : undefined, to: '/jobs?statu=MANUAL_REVIEW&aralik=tum' }
  ]
}

// İşlem Takibi › API Sağlık: Genel Bakış's figure cards as the summary, Genel Bakış's own Entegrasyon Durumu panel,
// with the automatic price/stock write state and sending problems folded into its rows. Each part is the shared one, so the two pages never drift apart.
export function ApiHealthView({ onFeedback }: { onFeedback: (feedback: IntegrationFeedback) => void }) {
  const integration = useIntegrationHealth()
  const bootstrap = useQuery(dashboardBootstrapQuery)
  const metrics = bootstrap.data?.metrics
  const monitor = usePriceStockMonitor()
  const stats = apiHealthStats(integration.matrix.isLoading || integration.matrix.isError ? null : integration.attention, knownCount(bootstrap.isLoading, metrics?.recentJobCount), knownCount(bootstrap.isLoading, metrics?.deadJobCount), knownCount(bootstrap.isLoading, metrics?.manualReviewJobCount))
  return <div className="api-health">
    <div className="rr rr-dashboard api-health-rr">
      <section className="rr-dsect" aria-label="API sağlık özeti"><div className="rr-opgrid">
        {stats.map(stat => {
          const body = <><CardBackgroundIcon name={stat.icon} tone={stat.tone} /><span className="rr-op-t"><small>{stat.label}</small><strong className="rr-num">{stat.value == null ? '—' : stat.value.toLocaleString('tr-TR')}</strong></span>{stat.to ? <RrIcon name="arrowR" /> : null}</>
          const className = `rr-opcard${stat.tone ? ` rr-${stat.tone}` : ''}`
          return stat.to ? <Link key={stat.key} className={className} to={stat.to}>{body}</Link> : <div key={stat.key} className={className}>{body}</div>
        })}
      </div></section>
      <IntegrationStatusPanel integration={integration} extras={monitor.extras} headerAction={<EmergencyStopButton anyOpen={monitor.anyOpen} onFeedback={onFeedback} />} notice={monitor.notice} />
      <ApiHealthSignalsPanel />
    </div>
  </div>
}
