export type ProductSyncJobStatus = {
  id?: string
  jobType: string
  status: string
}

const productSyncJobTypes = new Set(['TRENDYOL_PRODUCT_SYNC', 'SHOPIFY_PRODUCT_SYNC'])
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
