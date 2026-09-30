export type ReturnSyncConnection = { id: string; platformCode: string; status: string }
export type ReturnSyncResult = { succeeded: number; failed: number }

export function activeReturnSyncConnections<T extends ReturnSyncConnection>(connections: T[]) {
  return connections.filter(connection =>
    (connection.platformCode === 'TRENDYOL' || connection.platformCode === 'HEPSIBURADA')
    && (connection.status === 'ACTIVE' || connection.status === 'VERIFIED'))
}

export async function enqueueReturnSyncs<T extends ReturnSyncConnection>(
  connections: T[],
  enqueue: (connection: T) => Promise<unknown>
): Promise<ReturnSyncResult> {
  if (connections.length === 0) throw new Error('Aktif veya doğrulanmış bir iade bağlantısı bulunamadı.')

  const results = await Promise.allSettled(connections.map(enqueue))
  const succeeded = results.filter(result => result.status === 'fulfilled').length
  const failed = results.length - succeeded
  if (succeeded === 0) throw new Error('Hiçbir iade bağlantısında eşitleme başlatılamadı.')
  return { succeeded, failed }
}
