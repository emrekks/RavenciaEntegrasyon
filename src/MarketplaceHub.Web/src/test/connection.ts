import type { ApiAnswer } from '../shared/api/api-answer'

type Connection = ApiAnswer<'getConnections'>['items'][number]

/** A GET /connections item for tests: an active production store with every member the contract writes. */
export function testConnection(overrides: Partial<Connection> & Pick<Connection, 'id' | 'platformCode'>): Connection {
  return {
    publicId: overrides.id,
    environment: 'PRODUCTION',
    displayName: `${overrides.platformCode} Mağaza`,
    externalStoreId: `${overrides.id}-store`,
    status: 'ACTIVE',
    apiVersion: 'V1',
    lastTestedAt: null,
    lastSuccessAt: null,
    lastErrorCode: null,
    hasCredential: true,
    externalWritesEnabled: true,
    version: 1,
    invoiceCreationEnabled: false,
    n11ShipmentTemplate: null,
    n11PreparingDay: null,
    disabledAt: null,
    lastErrorAt: null,
    webhookNotice: null,
    ...overrides
  }
}
