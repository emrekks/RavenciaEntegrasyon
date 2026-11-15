// A marketplace card of the editor's publish step, with its status, gaps, refresh and job.
import { lazy, Suspense, useEffect, useEffectEvent, useRef, useState } from 'react'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll } from '../../../shared/api'
import { UiIcon } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { NoticeToast } from '../../../shared/components/NoticeToast'
import { platformLogoClass, platformLogoSource } from '../../../shared/platform-logos'
import { appendNotification, notificationError } from '../../../shared/notifications'
import { connectionCapabilitiesQueryKey, loadConnectionCapabilities, loadPublicationAttributeIssues, shopifyCapabilityIssues, missingPublicationChecks, publicationBlockingIssues, publicationReadinessQueryKey } from './publication-readiness'
import { fixableValueMappings, type PublicationAttributeSelection } from '../publication-attribute-readiness'
import { MissingValueMappingDialog } from '../MissingValueMappingDialog'
import { CategoryMappingDialog } from '../CategoryMappingDialog'
import { PublishIssueBadge, type PublishIssueAction } from '../PublishIssueBadge'
import { PublishStatusSteps } from './PublishStatusSteps'
import { publishStatusSteps, publishStepsAriaLabel, publishStepsFit } from './publish-status-steps'
import { attributeIssueTip } from '../publish-issue-chips'
import { attributeMappingPlatform, mappingFocusItems } from '../../marketplace/mappings/mapping-focus'
import { PublicationJobDialog } from './PublicationJobDialog'
import { publishCardVariantPresence } from './publish-card-variants'
import { offSaleSummary } from '../sale-state'
import { isApprovalDeadlineExpired, isPttAvmChecking, isPttAvmFamilyRetryable, isPublicationLive, isPublicationSelectionDisabled, isPublicationStatusJobRunning, publicationStatusLabel, publicationStatusNote, publicationStatusTone, pttAvmRetryLabel, shouldCheckPublicationAttributes } from '../publication-status'
import { publicationJobPollInterval } from '../publication-job-poll'
import { isRefreshJobRunning, publicationJobInline } from '../publication-job-inline'
import { statusLabel } from '../../../shared/status-labels'
import { publicationCancelMessage, publicationRefreshMessage } from '../product-publication-submit'
import { type OperationFeedback } from '../operation-feedback-toast'
import { AcceptedJob, MarketplaceConnection } from '../shared'
import { queryKeys } from '../../../shared/api/query-keys'
import { PublicationStatus, PublicationJobDetail } from './editor-types'
import { publishPlatformName } from './editor-helpers'

// The Eşleştirmeler page's "Kategori Alanları ve Değerleri" window, loaded with that page's code when a gap opens it.
export const ProductCategoryFieldsDialog = lazy(() => import('../../marketplace/mappings/MappingPages').then(module => ({ default: module.ProductCategoryFieldsDialog })))
// Opens the issue tooltip upwards when it would be clipped by the bottom of the viewport.
// A card with no publication record of its own (matched by an import, or never sent) is refreshed by looking the
// product up on the marketplace: Trendyol and N11 by model code, Pazarama and PttAVM by barcode, Shopify by barcode or
// stock code. Hepsiburada looks a single product up by its Hepsiburada product id, which the editor does not hold.
export function cardLookupKey(platformCode: string, keys: { modelCode: string; barcode: string; sku: string }) {
  const code = platformCode.trim().toUpperCase()
  const value = code === 'TRENDYOL' || code === 'N11' ? keys.modelCode
    : code === 'PAZARAMA' || code === 'PTTAVM' ? keys.barcode
      : code === 'SHOPIFY' ? keys.barcode || keys.sku
        : ''
  return value.trim() || null
}

// Live is green, being sent or awaiting the marketplace is yellow, refused is red; a card missing required fields is
// still unpublished (its pill names the gap).
export function publishCardState({ tone, jobRunning, requiredBlock, retryable }: { tone: string; jobRunning: boolean; requiredBlock: boolean; retryable: boolean }): 'unpublished' | 'error' | 'working' | 'live' {
  if (requiredBlock) return 'unpublished'
  if (jobRunning) return 'working'
  if (retryable || tone === 'danger') return 'error'
  if (tone === 'success') return 'live'
  if (tone === 'info' || tone === 'warning') return 'working'
  return 'unpublished'
}

// The connections are not in yet: blank card frames instead of "Aktif Bağlantı Bulunamadı" or a guessed set of cards.
export function PublishPlatformCardSkeletons() {
  return <>{[0, 1, 2].map(index => <article key={index} className="publish-platform-card is-state-settling is-settling is-skeleton" aria-busy="true" aria-label="Bağlantı yükleniyor">
    <div className="publish-platform-card-head"><span className="publish-platform-mark" /><span><strong>Platform</strong><small>Yayın durumu yükleniyor</small></span><i className="publish-platform-toggle" aria-hidden="true"><b /></i></div>
    <dl className="publish-platform-facts"><div><dt>Mağaza</dt><dd>—</dd></div><div><dt>Platform</dt><dd>—</dd></div><div><dt>Bağlantı</dt><dd>—</dd></div><div className="publish-platform-fact-publication"><dt>Yayın Durumu</dt><dd>—</dd></div></dl>
  </article>)}</>
}

export function PublishPlatformCard({ card, selected, settling: pageSettling = false, productId, familyProductIds, variantLinks, lookupKeys, categoryId, categoryName = '', productChecks, selectedAttributes, onSelect, onDeselect, onBlockedChange, onFeedback: emitFeedback, onEditProductData }: {
  // A gap in the product's own data: back to the form (the publish popup has no form, its gap links to the product).
  onEditProductData?: () => void
  /** The card's required gaps once known (0 when none, or while it is still reading them), so Yayınla can say why it left the card out. */
  onBlockedChange?: (requiredGaps: number) => void
  familyProductIds?: string[] | null
  lookupKeys?: { modelCode: string; barcode: string; sku: string }
  // How many of the family's variants are present on this marketplace (matched or live), and how many it no longer knows.
  variantLinks?: { linked: number; total: number; missing?: number; offSale?: number; offSaleStockZero?: number; offSaleReasons?: Record<string, number> }
  card: { code: string; name: string; initial: string; tone: string; connection: MarketplaceConnection; mode: 'PUBLISH' | 'READ_ONLY' }
  selected: boolean
  /** The product (its variants and colour family) is still loading: the card's state is not known yet. */
  settling?: boolean
  productId?: string
  categoryId: string
  /** The panel category's name, for the "not mapped" gap and "Kategori Eşle". */
  categoryName?: string
  productChecks: Array<{ title: string; detail: string; ok: boolean; shopifyOk?: boolean; shopifyDetail?: string }>
  selectedAttributes: PublicationAttributeSelection[]
  onSelect: () => void
  onDeselect: () => void
  /** The page's toast: the refresh and stop controls say on screen what they started, naming the marketplace. */
  onFeedback: (feedback: OperationFeedback) => void
}) {
  const client = useQueryClient()
  const platformName = publishPlatformName(card.connection.platformCode)
  // Every notice of this card names the marketplace and the store it is about.
  const onFeedback = (feedback: OperationFeedback) => emitFeedback({ ...feedback, context: { platformCode: card.connection.platformCode, connectionId: card.connection.id, storeName: card.connection.displayName, ...feedback.context } })
  const statusKey = queryKeys.publicationStatus(productId, card.connection.id)
  // The other colour cards of the family (same model code): the card stays selectable while any of them is not live
  // here, and Yayınla then sends only those (live or in-progress cards are skipped).
  const otherFamilyIds = (familyProductIds ?? []).filter(id => id !== productId)
  const publication = useQuery({
    queryKey: statusKey,
    queryFn: ({ signal }) => hubPoll<PublicationStatus>(`/products/${productId}/publication-status/${card.connection.id}`, { cache: 'no-store', signal }),
    enabled: !!productId && card.mode === 'PUBLISH',
    refetchInterval: query => isPublicationStatusJobRunning(query.state.data?.lastJobStatus) ? 3000 : false,
    refetchIntervalInBackground: true,
    refetchOnWindowFocus: true,
    staleTime: 0,
    retry: false
  })
  // The job the card's refresh started (a status check or a product lookup): the icon turns while it runs, a stop
  // button sits beside it, and when it ends the card's, its family's and the variants' states are read again.
  const [refreshJobId, setRefreshJobId] = useState<string | null>(null)
  const refreshPublication = useMutation({
    mutationFn: async () => {
      if (!productId) throw new Error('Ürün kaydı bulunamadı.')
      return hubApi<AcceptedJob>(`/products/${productId}/publication-status/${card.connection.id}/refresh`, {
        method: 'POST',
        headers: { 'Idempotency-Key': newIdempotencyKey() },
        body: '{}'
      })
    },
    onSuccess: async accepted => {
      // The start is said before the statuses are read again, so a check that ends quickly never says it ended first.
      onFeedback({ title: 'Durum Sorgulanıyor', message: publicationRefreshMessage(platformName, card.connection.platformCode, (familyProductIds ?? []).filter(id => id !== productId).length + 1), kind: 'info' })
      setRefreshJobId(accepted?.jobId ?? null)
      // The refresh checks every colour card of the family: their statuses are read again too, not only this card's
      // (live 2026-10-28: all eight MZ054YNC cards LIVE on the server, the card showed "1/8 renk" from cached siblings).
      await Promise.all([statusKey, ...otherFamilyIds.map(id => queryKeys.publicationStatus(id, card.connection.id))]
        .map(queryKey => client.invalidateQueries({ queryKey })))
    },
    onError: reason => {
      // Several cards refused in one go (a family, repeated clicks) collapse into one notice listing the products.
      const failure = notificationError(reason, 'Pazaryeri yanıt vermedi.')
      onFeedback({ title: 'Yayın Durumu Sorgulanamadı', message: `${platformName} yayın durumu sorgulanamadı: ${failure.message}`, kind: 'error', persistent: true, context: { operation: 'Yayın durumu sorgulama', code: failure.code, groupKey: `publication-refresh-failed:${card.connection.id}:${failure.code ?? failure.message}`, groupMessage: `{count} ürün için ${platformName} yayın durumu sorgulanamadı.` } })
    }
  })

  const lookupKey = lookupKeys ? cardLookupKey(card.connection.platformCode, lookupKeys) : null
  const lookupProduct = useMutation({
    mutationFn: () => hubApi<AcceptedJob>(`/connections/${card.connection.id}/product-sync-jobs?${new URLSearchParams({ lookup: lookupKey ?? '', includeArchived: 'true' }).toString()}`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async accepted => {
      onFeedback({ title: 'Ürün Pazaryerinde Aranıyor', message: `${platformName} mağazasında ${lookupKey} aranıyor; bulunursa varyantlar eşleştirilir ve durum güncellenir. Ürün bilgileri değiştirilmez.`, kind: 'info' })
      setRefreshJobId(accepted?.jobId ?? null)
      await client.invalidateQueries({ queryKey: statusKey })
    },
    onError: reason => {
      const failure = notificationError(reason, 'Pazaryeri yanıt vermedi.')
      onFeedback({ title: 'Ürün Aranamadı', message: `${platformName} mağazasında ürün aranamadı: ${failure.message}`, kind: 'error', persistent: true, context: { operation: 'Ürün arama', code: failure.code } })
    }
  })

  const publicationLabel = card.mode === 'READ_ONLY'
    ? 'Ürün Yayını Desteklenmiyor'
    : publication.isPending
    ? 'Yayın Durumu Yükleniyor…'
    : publication.isError
      ? 'Yayın Durumu Alınamadı'
      : publicationStatusLabel(publication.data?.actualStatus, publication.data?.lastJobStatus, card.connection.platformCode, publication.data?.lastRejectionCode)
  const publicationTone = publicationStatusTone(publication.data?.actualStatus, publication.data?.lastJobStatus, publication.data?.lastRejectionCode)
  const familyStatuses = useQueries({
    queries: otherFamilyIds.map(id => ({
      queryKey: queryKeys.publicationStatus(id, card.connection.id),
      queryFn: ({ signal }) => hubPoll<PublicationStatus>(`/products/${id}/publication-status/${card.connection.id}`, { cache: 'no-store', signal }),
      enabled: card.mode === 'PUBLISH',
      // A sibling's own send or status check is followed until it ends, like this card's.
      refetchInterval: (query: { state: { data?: PublicationStatus } }) => isPublicationStatusJobRunning(query.state.data?.lastJobStatus) ? 3000 : false,
      refetchIntervalInBackground: true,
      retry: false,
      staleTime: 0
    }))
  })
  const refreshJob = useCardRefreshJob(refreshJobId, async job => {
    await Promise.all([statusKey, ...otherFamilyIds.map(id => queryKeys.publicationStatus(id, card.connection.id)), queryKeys.product(productId)]
      .map(queryKey => client.invalidateQueries({ queryKey })))
    const status = job?.status.trim().toUpperCase()
    // The start said "aranıyor" / "sorgulanıyor"; the end says so too, not only when it failed.
    if (status === 'SUCCEEDED')
      onFeedback({ title: 'Sorgu Tamamlandı', message: `${platformName} durumu güncellendi; kart ve varyantlar yeniden okundu.`, kind: 'success' })
    else if (status && status !== 'CANCELLED')
      onFeedback({ title: 'Sorgu Tamamlanamadı', message: `${platformName}: ${job?.lastErrorSummary?.trim() || statusLabel(status)}`, kind: 'error', persistent: true, context: { operation: 'Yayın durumu sorgulama', code: job?.lastErrorCode ?? undefined } })
  }, {
    onCancelled: () => onFeedback({ title: 'Sorgu Durduruldu', message: `${platformName} sorgusu durduruldu.`, kind: 'info' }),
    onCancelFailed: message => onFeedback({ title: 'Sorgu Durdurulamadı', message: `${platformName}: sorgu durdurulamadı — ${message}`, kind: 'error', persistent: true })
  })
  // A family refresh queues a status check for every colour card: the icon keeps turning until the last one ends,
  // not only this card's own check.
  const familyChecksRunning = Boolean(refreshJobId) && [publication, ...familyStatuses].some(status => isPublicationStatusJobRunning(status.data?.lastJobStatus))
  const refreshBusy = refreshJob.running || familyChecksRunning || refreshPublication.isPending || lookupProduct.isPending
  const ownLive = isPublicationLive(publication.data?.actualStatus)
  const familyLiveCount = (ownLive ? 1 : 0) + familyStatuses.filter(status => isPublicationLive(status.data?.actualStatus)).length
  const familyTotal = otherFamilyIds.length + 1
  // On PttAVM one request carries the whole family: a colour card the family's send carried has no record of its own
  // until the status refresh takes it into that send, so it refreshes (not looks up) once a sibling is out there.
  const pttAvmFamilySent = card.mode === 'PUBLISH' && card.connection.platformCode.trim().toUpperCase() === 'PTTAVM'
    && familyStatuses.some(status => Boolean(status.data?.profileId) && Boolean(status.data?.actualStatus) && status.data?.actualStatus !== 'UNKNOWN')
  // Matched variants are the truth when they exist: every variant matched = live and not selectable; some = partly
  // live and selectable (Yayınla then sends only the cards none of whose variants is matched here).
  // A variant the marketplace no longer knows (link NOT_FOUND or REJECTED) is not live there: the card is partly live, or
  // "Bulunamadı" when none is left, and stays selectable so Yayınla sends it again.
  const linkedTotal = variantLinks?.total ?? 0
  const linkedCount = variantLinks?.linked ?? 0
  const missingCount = variantLinks?.missing ?? 0
  const { allLinked, someLinked, noneFound } = publishCardVariantPresence(variantLinks)
  // The sale state beside the publication, by the products list's rule (owner, 6 Nov 2026): a stock of 0 is never a
  // closure. A card whose every variant is there but closed for another reason reads "Satışa Kapalı · {reason}", one
  // with only some closed "Kısmen Satışta"; a blacklisted or locked variant is red. It never changes what Yayınla sends.
  const saleSummary = allLinked || someLinked ? offSaleSummary(linkedCount, variantLinks?.offSale ?? 0, variantLinks?.offSaleStockZero ?? 0, variantLinks?.offSaleReasons) : null
  const saleClosed = allLinked && Boolean(saleSummary?.allClosed)
  const salePartlyClosed = allLinked && Boolean(saleSummary) && !saleSummary?.allClosed
  const saleLabel = saleClosed && saleSummary ? saleSummary.label : salePartlyClosed ? 'Kısmen Satışta' : null
  const saleTone = saleSummary?.blocking ? 'danger' : 'warning'
  const familyPartial = !noneFound && (someLinked || (!allLinked && ownLive && otherFamilyIds.length > 0 && familyLiveCount < familyTotal && familyStatuses.every(status => !status.isPending)))
  const alreadyPublished = missingCount === 0 && (allLinked || (ownLive && !familyPartial))
  const selectionStatus = noneFound || familyPartial ? null : allLinked ? 'LIVE' : publication.data?.actualStatus
  const partialLabel = someLinked ? `${linkedCount}/${linkedTotal} yayında` : `${familyLiveCount}/${familyTotal} renk yayında`
  // PttAVM keeps a product with variants as the whole model family under its model code: every colour card goes in
  // one request. The variant count is this card's when the family is this card alone.
  const isPttAvmFamily = card.mode === 'PUBLISH' && card.connection.platformCode.trim().toUpperCase() === 'PTTAVM' && (familyTotal > 1 || linkedTotal > 1)
  // A PttAVM family card waiting for an approval that never comes (or stopped) says "Tekrar Deneyin" and stays
  // selectable: Yayınla sends the family again.
  const pttAvmRetry = isPttAvmFamily && Boolean(productId) && isPttAvmFamilyRetryable(card.connection.platformCode, publication.data?.actualStatus, publication.data?.lastJobStatus)
  const publicationCheckPending = Boolean(card.mode === 'PUBLISH' && productId && publication.isPending)
  // Until the product is in and this open has read the card's own and its colour family's statuses, the card is masked:
  // a status cached from an earlier open, or a family not read yet, would show "Zaten yayında" or "Kısmen yayında · 1/8"
  // for a moment and then change ("değer flaşı yok").
  const statusSettling = pageSettling || (card.mode === 'PUBLISH' && Boolean(productId) && (!publication.isFetchedAfterMount || familyStatuses.some(status => !status.isFetchedAfterMount)))
  const publicationCheckUnavailable = Boolean(card.mode === 'PUBLISH' && productId && publication.isError && !publication.data)
  // card.code is lower case (it also names the logo tone); the platform code decides the Shopify-only checks.
  const isShopifyCard = card.connection.platformCode.trim().toUpperCase() === 'SHOPIFY'
  const shopifyWriteCapabilities = useQuery({
    queryKey: connectionCapabilitiesQueryKey(card.connection.id),
    queryFn: ({ signal }) => loadConnectionCapabilities(card.connection.id, signal),
    enabled: card.mode === 'PUBLISH' && isShopifyCard,
    staleTime: 15_000,
    retry: false
  })
  const shopifyCapabilityCheckPending = card.mode === 'PUBLISH' && isShopifyCard && shopifyWriteCapabilities.isPending
  useEffect(() => {
    if (!statusSettling && alreadyPublished && selected) onDeselect()
  }, [alreadyPublished, onDeselect, selected, statusSettling])
  const publicationNote = publicationStatusNote(publication.data?.lastRejectionCode, card.connection.platformCode)
  const { view: jobView, cancel: cancelJob, jobType: lastJobType } = usePublicationJobInline(publication.data?.lastJobId, productId, card.connection.id, {
    onCancelled: status => onFeedback({ title: status?.trim().toUpperCase() === 'CANCELLED' ? 'Yayın İşi Durduruldu' : 'Durdurma İstendi', message: publicationCancelMessage(platformName, status), kind: 'info' }),
    onCancelFailed: message => onFeedback({ title: 'Yayın İşi Durdurulamadı', message: `${platformName}: yayın işi durdurulamadı — ${message}`, kind: 'error', persistent: true })
  })
  const jobRunning = jobView.state === 'running'
  const missingChecks = card.mode === 'PUBLISH' ? missingPublicationChecks(productChecks, card.connection.platformCode) : []
  const mappingReadinessEnabled = card.mode === 'PUBLISH' && (!productId || !publication.isPending)
    && (pttAvmRetry ? Boolean(categoryId) : shouldCheckPublicationAttributes(categoryId, selectionStatus, publication.data?.lastJobStatus))
  const mappingReadiness = useQuery({
    queryKey: publicationReadinessQueryKey(card.connection.id, categoryId, selectedAttributes),
    queryFn: () => loadPublicationAttributeIssues(client, card.connection, card.name, categoryId, selectedAttributes, categoryName),
    enabled: mappingReadinessEnabled,
    retry: false,
    staleTime: 15_000
  })
  const requiredMappingIssues = mappingReadinessEnabled ? mappingReadiness.data?.requiredIssues ?? [] : []
  const optionalMappingWarnings = mappingReadinessEnabled ? mappingReadiness.data?.optionalWarnings ?? [] : []
  const mappingCheckFailed = mappingReadinessEnabled && mappingReadiness.isError
  const mappingCheckPending = mappingReadinessEnabled && mappingReadiness.isPending
  // The card shows its state once, when everything it rests on is in: the publish statuses (also the colour family's), the
  // editor's product and category requirements, the mapping check and (Shopify) the write permissions. Until then it is
  // masked ("Yayın durumu yükleniyor"), so no first state turns into another a moment later.
  const settling = statusSettling || mappingCheckPending || shopifyCapabilityCheckPending
  const valueMappingFixes = mappingReadinessEnabled ? fixableValueMappings(mappingReadiness.data) : []
  const readinessCategoryScope = mappingReadinessEnabled ? mappingReadiness.data?.categoryScope : undefined
  const [valueFixOpen, setValueFixOpen] = useState(false)
  const [categoryFixOpen, setCategoryFixOpen] = useState(false)
  // The category fields window ("Kategori Alanları ve Değerleri") opened from a gap: the marketplace field it opens on.
  const [fieldsFix, setFieldsFix] = useState<{ externalAttributeId?: string; fieldName?: string; missing: { title: string; detail: string } } | null>(null)
  const fieldsWindowPlatform = attributeMappingPlatform(card.connection.platformCode)
  const mappingFocus = mappingFocusItems(requiredMappingIssues, optionalMappingWarnings)
  // On close (saved or not) the card reads its publish check again, so the badge counts follow what was mapped.
  const closeFieldsFix = () => {
    setFieldsFix(null)
    void client.invalidateQueries({ queryKey: queryKeys.publicationAttributeReadiness() })
  }
  const categoryMappingGap = mappingReadinessEnabled ? mappingReadiness.data?.categoryMapping : undefined
  // The status opens the API job behind it; a card whose required fields are missing keeps its issue tooltip instead.
  const [jobDetailOpen, setJobDetailOpen] = useState(false)
  const [valueFixNotice, setValueFixNotice] = useState('')
  const blockedIssues = publicationBlockingIssues(missingChecks, shopifyCapabilityIssues(card.connection.platformCode, shopifyWriteCapabilities.data, shopifyWriteCapabilities.isError), requiredMappingIssues, mappingCheckFailed ? mappingReadiness.error : null, card.name)
  const selectionBlocked = blockedIssues.length > 0 || mappingCheckPending || shopifyCapabilityCheckPending
  const issueListId = `publish-issues-${card.connection.id}`
  const issueDescriptionIds = [
    blockedIssues.length ? `${issueListId}-required` : '',
    mappingCheckPending ? `${issueListId}-pending` : '',
    shopifyCapabilityCheckPending ? `${issueListId}-shopify-pending` : '',
    optionalMappingWarnings.length ? `${issueListId}-optional` : ''
  ].filter(Boolean).join(' ') || undefined
  const hasRequiredPublicationBlock = blockedIssues.length > 0 && !alreadyPublished
  // A card with a required gap cannot be published: its toggle goes off as soon as the gap is known (a card picked
  // before its check finished included), and the page learns the gap so Yayınla names it instead of saying nothing.
  const requiredGaps = !settling && card.mode === 'PUBLISH' && hasRequiredPublicationBlock ? blockedIssues.length : 0
  useEffect(() => {
    if (requiredGaps > 0 && selected) onDeselect()
  }, [requiredGaps, onDeselect, selected])
  useEffect(() => {
    onBlockedChange?.(requiredGaps)
  }, [requiredGaps, onBlockedChange])
  // Each gap chip of the badge's popover opens its own fix: the category, the category fields window on the gap's
  // marketplace field (where the store has one), the value mappings, a new check, or the attribute mappings page; a
  // product field gap is information (the form above fixes it). The
  // optional warnings (attributes the store would get without a value) sit below the required gaps; with no required gap
  // a yellow "N uyarı" badge stands beside the status.
  // A mapping gap's chip says what is missing, from the publish check's own finding: the panel values with no
  // marketplace value (which colours, sizes, materials) and, in its tooltip, a field with no mapping or an outdated one.
  const issueInfo = (issue: { attribute: string; detail: string }): { values?: string[]; tip?: string[] } => {
    const found = [...requiredMappingIssues, ...optionalMappingWarnings].find(item => item.attribute === issue.attribute && item.detail === issue.detail)
    if (!found) return {}
    const label = found.fix?.valueLabel
    return { values: label ? [label] : undefined, tip: attributeIssueTip(found) }
  }
  const issueFix = (issue: { attribute: string; detail: string }): { href?: string; onRun?: () => void } => {
    const mappingIssue = [...requiredMappingIssues, ...optionalMappingWarnings].find(item => item.attribute === issue.attribute && item.detail === issue.detail)
    const productCheck = missingChecks.find(check => check.title === issue.attribute && check.detail === issue.detail)
    if (issue.attribute === 'Kategori eşlemesi' && categoryMappingGap && categoryId) return { onRun: () => setCategoryFixOpen(true) }
    if (mappingCheckFailed && issue.attribute === `${card.name} özellik kontrolü`) return { onRun: () => { void mappingReadiness.refetch() } }
    if (/^Shopify API izin/.test(issue.attribute)) return { href: '/integrations' }
    const missing = { title: issue.attribute, detail: issue.detail }
    // A marketplace field's gap opens the category fields window on that field.
    if (mappingIssue) {
      if (fieldsWindowPlatform && categoryId && mappingIssue.externalAttributeId) { const externalAttributeId = mappingIssue.externalAttributeId; return { onRun: () => setFieldsFix({ externalAttributeId, missing }) } }
      if (mappingIssue.fix && readinessCategoryScope) return { onRun: () => setValueFixOpen(true) }
      return { href: '/mappings/categories' }
    }
    // Trendyol's Web Color (only Trendyol cards read it) opens the window on Web Color, the Renk ↔ Web Color card.
    if (productCheck?.title === 'Web Color') {
      if (fieldsWindowPlatform && categoryId && !categoryMappingGap) return { onRun: () => setFieldsFix({ fieldName: 'Web Color', missing }) }
      return { href: '/mappings/categories' }
    }
    // The product's own data (name, images, variants, the category's attributes and options) is fixed in its form.
    if (productCheck) return onEditProductData ? { onRun: onEditProductData } : productId ? { href: `/products/${productId}` } : {}
    if (categoryMappingGap && categoryId) return { onRun: () => setCategoryFixOpen(true) }
    return { href: '/mappings/categories' }
  }
  const optionalBadgeProps = { optionalCount: optionalMappingWarnings.length || undefined, optionalHeading: optionalMappingWarnings.length === 1 ? 'İsteğe bağlı özellik uyarısı' : `${optionalMappingWarnings.length} isteğe bağlı özellik uyarısı`, optionalRows: optionalMappingWarnings.map((issue, index) => ({ key: `${issue.attribute}-${index}`, title: issue.attribute, detail: issue.detail, ...issueInfo(issue), ...issueFix(issue) })) }
  // Partly live: the Yayında step is a ring of the live share and the pill reads "21/42"; the popover's top line says what
  // the rest would meet if sent, read from the same publish check: "Kalan 21 varyant yayına hazır", or "Kalan 21
  // varyantta 2 eksik" above the gap chips.
  const partialRest = !familyPartial ? 0 : someLinked ? Math.max(0, linkedTotal - linkedCount) : Math.max(0, familyTotal - familyLiveCount)
  const partialUnit = someLinked ? 'varyant' : 'renk'
  const partialBlocked = familyPartial && hasRequiredPublicationBlock
  const partialReady = familyPartial && partialRest > 0 && card.mode === 'PUBLISH' && !hasRequiredPublicationBlock && !mappingCheckPending && !shopifyCapabilityCheckPending && !jobRunning
  const lastJobId = (!hasRequiredPublicationBlock || partialBlocked) && productId ? publication.data?.lastJobId ?? null : null
  const issueBadgeRows = blockedIssues.slice(0, 8).map(issue => ({ key: `${issue.attribute}:${issue.detail}`, title: issue.attribute, detail: issue.detail, ...issueInfo(issue), ...issueFix(issue) }))
  const issueBadgeHeading = blockedIssues.length === 1 ? 'Yayın için 1 eksik var' : `Yayın için ${blockedIssues.length} eksik var`
  const issueBadgeMore = blockedIssues.length > 8 ? `Ve ${blockedIssues.length - 8} eksik daha.` : undefined
  // A product that is not saved yet has no platform record: it shows what keeps it from publishing, or that it is not out yet.
  const displayedPublicationLabel = partialBlocked ? partialLabel : hasRequiredPublicationBlock ? 'Zorunlu Eksik' : noneFound ? 'Bulunamadı' : familyPartial ? partialLabel : saleLabel ? saleLabel : allLinked ? 'Yayında' : pttAvmRetry && !isPttAvmChecking(card.connection.platformCode, publication.data?.actualStatus) ? pttAvmRetryLabel : productId ? publicationLabel : 'Henüz yayınlanmadı'
  const missingVariantsTitle = noneFound ? 'Pazaryerinde Bulunamadı' : missingCount > 0 ? `${missingCount} varyant pazaryerinde bulunamadı` : undefined
  const displayedPublicationTone = partialBlocked ? 'info' : hasRequiredPublicationBlock ? 'danger' : noneFound ? 'danger' : familyPartial ? 'info' : saleLabel ? saleTone : allLinked ? 'success' : pttAvmRetry && !isPttAvmChecking(card.connection.platformCode, publication.data?.actualStatus) ? 'warning' : productId ? publicationTone : 'neutral'
  // The status as four steps (Hazırlık → Gönderim → Onay → Yayında) with the counts beside them, in one pill: hover or
  // focus shows the gaps popover, a press pins it; its top line says the state and why (the job's reason, a refusal
  // code), its foot holds the card's actions (stop, İş Ayrıntısı); the refresh sits in the header.
  const pttAvmChecking = isPttAvmChecking(card.connection.platformCode, publication.data?.actualStatus)
  const lastJobKind = lastJobType?.trim().toUpperCase() ?? ''
  const publishSteps = publishStatusSteps({
    readOnly: card.mode === 'READ_ONLY',
    settling,
    statusUnavailable: publicationCheckUnavailable,
    actualStatus: productId ? publication.data?.actualStatus : null,
    lastJobStatus: productId ? publication.data?.lastJobStatus : null,
    rejectionCode: productId ? publication.data?.lastRejectionCode : null,
    requiredGaps: hasRequiredPublicationBlock ? blockedIssues.length : 0,
    optionalWarnings: optionalMappingWarnings.length,
    live: alreadyPublished,
    saleClosedLabel: saleLabel,
    partial: familyPartial ? someLinked ? { live: linkedCount, total: linkedTotal, unit: 'varyant' } : { live: familyLiveCount, total: familyTotal, unit: 'renk' } : null,
    notFound: noneFound,
    missingVariants: missingCount,
    retry: Boolean(pttAvmRetry) && !pttAvmChecking,
    checking: pttAvmChecking,
    job: { running: jobRunning, failed: jobView.state === 'settled' && jobView.tone === 'danger', step: lastJobKind === 'PRODUCT_APPROVAL_RECONCILE' ? 'approval' : ['LISTING_VERIFY', 'LISTING_LOOKUP', 'PRODUCT_SYNC', 'PRODUCT_STATUS_REFRESH'].includes(lastJobKind) ? null : 'send' },
    fallbackLabel: displayedPublicationLabel
  })
  const stepsLabel = publishStepsAriaLabel(card.name, publishSteps, partialUnit)
  // The popover's top line: the state in words, then why (the running or failed job's reason, a refusal code, what the
  // rest of a partly live card would meet).
  const leadReasons = [
    jobView.state !== 'hidden' ? { key: 'job', text: jobView.message, tone: jobView.state === 'running' ? 'info' : jobView.tone } : null,
    publicationNote ? { key: 'note', text: publicationNote, tone: isApprovalDeadlineExpired(publication.data?.actualStatus, publication.data?.lastRejectionCode) ? 'warning' : 'danger' } : null,
    missingVariantsTitle && !noneFound ? { key: 'missing', text: missingVariantsTitle, tone: 'danger' } : null,
    saleSummary && !saleClosed ? { key: 'sale', text: saleSummary.allClosed ? `Yayındaki ${saleSummary.matched} varyantın tümü satışa kapalı${saleSummary.reasonLabel ? ` (${saleSummary.reasonLabel})` : ''}` : saleSummary.label, tone: saleTone } : null,
    saleClosed && saleSummary ? { key: 'sale', text: `Pazaryeri bu ürünü satışa kapalı gösteriyor${saleSummary.reasonLabel ? ` (${saleSummary.reasonLabel})` : ''}`, tone: saleTone } : null,
    partialReady ? { key: 'rest', text: `Kalan ${partialRest} ${partialUnit} yayına hazır`, tone: 'success' } : null,
    partialBlocked && partialRest > 0 ? { key: 'rest', text: `Kalan ${partialRest} ${partialUnit}${partialUnit === 'renk' ? 'te' : 'ta'} ${blockedIssues.length} eksik`, tone: 'danger' } : null
  ].filter((reason): reason is { key: string; text: string; tone: string } => Boolean(reason))
  const leadTitle = familyPartial && someLinked ? `${linkedCount}/${linkedTotal} varyant yayında` : displayedPublicationLabel
  const statusLead = <><strong>{leadTitle}</strong>{leadReasons.map(reason => <span key={reason.key} className={`is-${reason.tone}`}>{reason.text}</span>)}</>
  const statusRecorded = Boolean(publication.data?.profileId && publication.data.actualStatus && publication.data.actualStatus !== 'UNKNOWN') || pttAvmFamilySent
  const stopIcon = <i className="publish-issue-action-stop" aria-hidden="true" />
  // The card's refresh (a status check, or a store lookup while no record is there): a borderless icon in the header,
  // beside the selection toggle, never inside it.
  const refreshAction: PublishIssueAction | null = statusRecorded
      ? { key: 'refresh', label: 'Durumu Yenile', icon: null, busy: refreshBusy, ariaLabel: refreshBusy ? `${card.name} yayın durumu sorgulanıyor` : `${card.name} yayın durumunu yenile`, title: refreshBusy ? 'Platformdaki durum sorgulanıyor…' : 'Platformdaki mevcut durumu yeniden sorgula', disabled: refreshBusy || isPublicationStatusJobRunning(publication.data?.lastJobStatus), onRun: () => refreshPublication.mutate() }
      : productId && card.mode !== 'READ_ONLY'
        ? { key: 'refresh', label: 'Durumu Yenile', icon: null, busy: refreshBusy, ariaLabel: refreshBusy ? `${card.name} mağazasında ürün aranıyor` : `${card.name} yayın durumunu yenile`, title: refreshBusy ? `${card.name} mağazasında ${lookupKey} aranıyor…` : lookupKey ? `${card.name} mağazasında ${lookupKey} ara ve varyantları eşleştir` : `${card.name} tek ürünü kendi ürün numarasıyla arar; bu ürün için o numara bilinmiyor. Ürün Aktar'dan arayın.`, disabled: !lookupKey || refreshBusy || publication.isPending, onRun: () => lookupProduct.mutate() }
        : null
  const statusActionSlots: Array<PublishIssueAction | null> = [
    refreshJob.running && !(jobRunning && publication.data?.lastJobId === refreshJobId)
      ? { key: 'stop-refresh', label: 'Sorguyu Durdur', icon: stopIcon, tone: 'danger', ariaLabel: refreshJob.cancel.isPending ? `${card.name} sorgusu durduruluyor` : `${card.name} sorgusunu durdur`, disabled: refreshJob.cancel.isPending, onRun: () => refreshJob.cancel.mutate() }
      : null,
    jobRunning
      ? { key: 'stop-job', label: 'Yayını Durdur', icon: stopIcon, tone: 'danger', ariaLabel: cancelJob.isPending ? `${card.name} yayın işlemi durduruluyor` : `${card.name} yayın işlemini durdur`, title: 'Durdur — platforma gönderilmiş bir isteği geri alamayabilir.', disabled: cancelJob.isPending, onRun: () => cancelJob.mutate() }
      : null,
    lastJobId ? { key: 'job', label: 'İş Ayrıntısı', icon: <UiIcon name="jobs" size={14} />, title: 'Bu yayın işinin ayrıntısını aç', onRun: () => setJobDetailOpen(true) } : null
  ]
  const statusActions = statusActionSlots.filter((action): action is PublishIssueAction => Boolean(action))
  // "Eksik kalmadı" where the card waits to be sent: no gap is left (also once the last one was fixed in the open popover).
  const stepsEmptyText = publishSteps.steps[1] === 'next' ? 'Eksik kalmadı.' : null
  const readOnlyIsN11 = card.connection.platformCode.trim().toUpperCase() === 'N11'
  const readOnlyTitle = readOnlyIsN11 ? 'N11 ürün açma için Entegrasyonlar bölümünde N11 bağlantısının dış yazma anahtarını açın.' : 'Shopify ürün yayını için Entegrasyonlar bölümünde dış yazma anahtarını açın ve gerekli API izinlerini doğrulayın.'
  // The card's frame and place in the grid: not yet published first, then refused, in progress, and live last.
  const cardState = publishCardState({ tone: displayedPublicationTone, jobRunning, requiredBlock: hasRequiredPublicationBlock, retryable: Boolean(pttAvmRetry) })
  return <article className={`publish-platform-card is-state-${settling ? 'settling' : cardState}${selected && !settling ? ' selected' : ''}${settling ? ' is-settling' : ''}${card.mode === 'READ_ONLY' ? ' is-read-only' : ''}`} aria-busy={settling || undefined}>
    <div className="publish-platform-card-top">
    <button type="button" className={`publish-platform-card-head${refreshAction ? ' has-refresh' : ''}`} onClick={onSelect} aria-pressed={selected} aria-describedby={issueDescriptionIds} title={card.mode === 'READ_ONLY' ? readOnlyTitle : alreadyPublished ? 'Ürün bu platformda zaten yayında.' : undefined} disabled={settling || card.mode === 'READ_ONLY' || isPublicationSelectionDisabled(selectionStatus, selected, publicationCheckPending, publicationCheckUnavailable, selectionBlocked)}>
      <span className={`publish-platform-mark ${card.tone}`}><img className={`publish-platform-logo ${platformLogoClass(card.connection.platformCode)}`} src={platformLogoSource(card.connection.platformCode) ?? '/platforms/trendyol.webp'} alt="" aria-hidden="true" /></span>
      <span><strong>{card.name}</strong><small>{settling ? 'Yayın durumu yükleniyor' : card.mode === 'READ_ONLY' ? 'Dış yazma kapalı · yayın kuyruğu devre dışı' : alreadyPublished ? 'Zaten yayında · yeniden seçilemez' : publicationCheckPending ? 'Yayın durumu kontrol ediliyor…' : publicationCheckUnavailable ? 'Yayın durumu doğrulanamadı' : shopifyCapabilityCheckPending ? 'Shopify yazma izinleri kontrol ediliyor…' : selected ? 'Yayın için seçildi' : mappingCheckPending ? 'Zorunlu alanlar kontrol ediliyor…' : selectionBlocked ? 'Zorunlu eksikler giderilmeden seçilemez' : pttAvmRetry ? "Tekrar denemek için seçip Yayınla'ya basın" : 'Yayın için seçilmedi'}</small></span>
      {refreshAction && <span className="publish-platform-head-gap" aria-hidden="true" />}
      <i className={`publish-platform-toggle ${selected && !settling ? 'on' : ''}`} aria-hidden="true"><b /></i>
    </button>
    {refreshAction && <button type="button" className={`publish-platform-card-refresh${refreshAction.busy ? ' is-busy' : ''}`} aria-label={refreshAction.ariaLabel} aria-busy={refreshAction.busy || undefined} title={refreshAction.title} disabled={refreshAction.disabled} onClick={refreshAction.onRun}><UiIcon name="refresh" size={16} /></button>}
    </div>
    <dl className="publish-platform-facts">
      <div><dt>Mağaza</dt><dd title={card.connection.externalStoreId ? `Mağaza no: ${card.connection.externalStoreId}` : undefined}>{card.connection.displayName || card.connection.externalStoreId || '—'}</dd></div>
      <div><dt>Platform</dt><dd>{publishPlatformName(card.connection.platformCode)}</dd></div>
      <div><dt>Bağlantı</dt><dd><span className="publish-platform-status active"><i aria-hidden="true" />Aktif Bağlantı</span></dd></div>
      {(productId || card.mode !== 'READ_ONLY') && <div className="publish-platform-fact-publication" aria-live="polite"><dt>Yayın Durumu</dt><dd><PublishIssueBadge variant="steps" id={`${issueListId}-popover`} label={stepsLabel} trigger={<PublishStatusSteps steps={publishSteps} fit={publishStepsFit(publishSteps)} unit={partialUnit} />} lead={statusLead} actions={statusActions} emptyText={stepsEmptyText} requiredCount={blockedIssues.length} heading={issueBadgeHeading} rows={hasRequiredPublicationBlock ? issueBadgeRows : []} moreText={hasRequiredPublicationBlock ? issueBadgeMore : undefined} {...optionalBadgeProps} />{jobRunning && <span className="sr-only">{jobView.message}</span>}</dd></div>}
    </dl>
    {categoryFixOpen && categoryId && <CategoryMappingDialog connectionId={card.connection.id} platformName={card.name} categoryId={categoryId} categoryName={categoryName || 'Ürün'} onClose={() => setCategoryFixOpen(false)} onSaved={message => { setValueFixNotice(message) }} />}
    {valueFixOpen && readinessCategoryScope && <MissingValueMappingDialog connectionId={card.connection.id} platformName={card.name} categoryScope={readinessCategoryScope} fixes={valueMappingFixes} onClose={() => setValueFixOpen(false)} onSaved={message => { setValueFixNotice(message); appendNotification({ title: 'Değer Eşlemeleri Kaydedildi', message, kind: 'success', operation: 'Özellik değeri eşleştirme', platformCode: card.connection.platformCode, connectionId: card.connection.id, storeName: card.connection.displayName, target: { href: '/mappings/attributes', label: 'Eşleştirmelere Git' } }) }} />}
    {fieldsFix && fieldsWindowPlatform && categoryId && <Suspense fallback={null}><ProductCategoryFieldsDialog platformCode={fieldsWindowPlatform} connectionId={card.connection.id} localCategoryId={categoryId} focus={{ items: mappingFocus, initialExternalAttributeId: fieldsFix.externalAttributeId, initialFieldName: fieldsFix.fieldName, missing: fieldsFix.missing }} onClose={closeFieldsFix} /></Suspense>}
    {jobDetailOpen && lastJobId && <PublicationJobDialog jobId={lastJobId} platformCode={card.connection.platformCode} platformName={card.name} storeName={card.connection.displayName} onClose={() => setJobDetailOpen(false)} />}
    {valueFixNotice && <NoticeToast kind="success" title="Değer Eşlemeleri Kaydedildi" message={valueFixNotice} onClose={() => setValueFixNotice('')} durationMs={5500} />}
    {card.mode === 'READ_ONLY' && <div className="publish-platform-warning" role="note"><UiIcon name="alert" /><div><strong>Shopify Dış Yazması Kapalı</strong><p>Ürün yayınını açmak için Entegrasyonlar bölümünde Shopify dış yazma anahtarını etkinleştirin; bağlantı testinde write_products, write_inventory, read_publications ve write_publications izinleri doğrulanmalıdır.</p></div></div>}
    {shopifyCapabilityCheckPending && <span id={`${issueListId}-shopify-pending`} className="sr-only">Shopify izinleri kontrol ediliyor.</span>}
    {blockedIssues.length > 0 && <span id={`${issueListId}-required`} className="sr-only">{blockedIssues.map(issue => `${issue.attribute}: ${issue.detail}`).join('. ')}</span>}
    {mappingCheckPending && <span id={`${issueListId}-pending`} className="sr-only">Yayın gereksinimleri kontrol ediliyor. Güncel zorunlu özellik eşlemeleri doğrulanıyor.</span>}
    {optionalMappingWarnings.length > 0 && <span id={`${issueListId}-optional`} className="sr-only">{optionalMappingWarnings.map(issue => `${issue.attribute}: ${issue.detail}`).join('. ')}</span>}
    
  </article>
}
// The card's last publication job, read while it runs; the card shows it inline so its height never changes.
export function usePublicationJobInline(jobId: string | null | undefined, productId: string | undefined, connectionId: string, notify: { onCancelled: (status: string | null | undefined) => void; onCancelFailed: (message: string) => void }) {
  const client = useQueryClient()
  const queryKey = queryKeys.publicationJob(jobId ?? '')
  const enabled = Boolean(productId && jobId)
  const job = useQuery({
    queryKey,
    queryFn: ({ signal }) => hubPoll<PublicationJobDetail>(`/jobs/${jobId}`, { cache: 'no-store', signal }),
    enabled,
    refetchInterval: query => publicationJobPollInterval(query.state.data?.job.status, query.state.status === 'error'),
    refetchIntervalInBackground: true,
    refetchOnWindowFocus: true,
    staleTime: 0,
    retry: false
  })
  const cancel = useMutation({
    mutationFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}/cancel`, {
      method: 'POST',
      headers: { 'Idempotency-Key': newIdempotencyKey() },
      body: '{}'
    }),
    onSuccess: async result => {
      client.setQueryData(queryKey, result)
      notify.onCancelled(result?.job?.status)
      await Promise.all([
        client.invalidateQueries({ queryKey: queryKeys.publicationStatus(productId, connectionId) }),
        client.invalidateQueries({ queryKey })
      ])
    },
    onError: reason => notify.onCancelFailed(reason instanceof Error ? reason.message : 'bilinmeyen hata.')
  })
  return { view: enabled ? publicationJobInline(job.data?.job) : { state: 'hidden' as const }, cancel, jobType: enabled ? job.data?.job.jobType ?? null : null }
}
// The job a card's refresh started, read until it ends; onSettled runs once when it does (or when it can no longer be read).
export function useCardRefreshJob(jobId: string | null, onSettled: (job: PublicationJobDetail['job'] | undefined) => void | Promise<void>, notify: { onCancelled: () => void; onCancelFailed: (message: string) => void }) {
  const client = useQueryClient()
  const queryKey = queryKeys.publicationJob(jobId ?? '')
  const job = useQuery({
    queryKey,
    queryFn: ({ signal }) => hubPoll<PublicationJobDetail>(`/jobs/${jobId}`, { cache: 'no-store', signal }),
    enabled: Boolean(jobId),
    refetchInterval: query => publicationJobPollInterval(query.state.data?.job.status, query.state.status === 'error'),
    refetchIntervalInBackground: true,
    staleTime: 0,
    retry: false
  })
  const running = Boolean(jobId) && !job.isError && (job.isPending || isRefreshJobRunning(job.data?.job))
  const settledJobId = useRef<string | null>(null)
  const settle = useEffectEvent(() => { void onSettled(job.data?.job) })
  useEffect(() => {
    if (!jobId || running || settledJobId.current === jobId) return
    settledJobId.current = jobId
    settle()
  }, [jobId, running])
  const cancel = useMutation({
    mutationFn: () => hubApi<PublicationJobDetail>(`/jobs/${jobId}/cancel`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: result => {
      client.setQueryData(queryKey, result)
      notify.onCancelled()
    },
    onError: reason => notify.onCancelFailed(reason instanceof Error ? reason.message : 'bilinmeyen hata.')
  })
  return { running, cancel }
}
