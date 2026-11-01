export type ProductSyncJobStatus = {
  id?: string
  jobType: string
  status: string
}

const productSyncJobTypeList = ['PRODUCT_SYNC', 'SHOPIFY_PRODUCT_SYNC', 'HEPSIBURADA_PRODUCT_SYNC']
const productSyncJobTypes = new Set(productSyncJobTypeList)
// Only the newest product sync jobs are needed to follow an import, not the whole job history.
export const productSyncJobsPath = `/jobs?jobTypes=${productSyncJobTypeList.join(',')}&limit=100`
const terminalProductSyncStatuses = new Set(['SUCCEEDED', 'CANCELLED', 'BLOCKED', 'MANUAL_REVIEW', 'DEAD'])

export function isProductSyncJob(job: ProductSyncJobStatus) {
  return productSyncJobTypes.has(job.jobType.trim().toUpperCase())
}

export function isActiveProductSyncJob(job: ProductSyncJobStatus) {
  return isProductSyncJob(job) && !terminalProductSyncStatuses.has(job.status.trim().toUpperCase())
}

export function activeProductSyncJobs<T extends ProductSyncJobStatus>(jobs: T[]) {
  return jobs.filter(isActiveProductSyncJob)
}

// The import jobs the products page follows: a second import adds its jobs to those of the first instead of
// replacing them, so the first one's end still refreshes the list.
export function trackProductSyncJobs(tracked: readonly string[], added: readonly string[]) {
  return [...new Set([...tracked, ...added])]
}

// The followed jobs split by whether they ended: the ended ones refresh the list and are no longer followed.
export function settleTrackedProductSyncJobs(jobIds: readonly string[], jobs: ProductSyncJobStatus[]) {
  const finished = jobIds.filter(jobId => productSyncJobsFinished([jobId], jobs))
  return { finished, remaining: jobIds.filter(jobId => !finished.includes(jobId)) }
}

export function productSyncJobsFinished(jobIds: string[], jobs: ProductSyncJobStatus[]) {
  if (!jobIds.length) return false
  const terminalById = new Map(jobs.filter(job => job.id).map(job => [job.id!, terminalProductSyncStatuses.has(job.status.trim().toUpperCase())]))
  return jobIds.every(jobId => terminalById.get(jobId) === true)
}
