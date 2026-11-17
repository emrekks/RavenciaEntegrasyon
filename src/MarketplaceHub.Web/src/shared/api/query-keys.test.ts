// @vitest-environment node
import { ESLint } from 'eslint'
import { describe, expect, it } from 'vitest'
import { queryKeys, queryRootList } from './query-keys'

describe('query key factory', () => {
  it('builds the same arrays the screens wrote by hand', () => {
    const filters = { status: 'NEW' }
    expect(queryKeys.orders()).toEqual(['orders'])
    expect(queryKeys.orders('page', filters, 2)).toEqual(['orders', 'page', filters, 2])
    expect(queryKeys.invoiceWorkspacePage('x')).toEqual(['invoice-workspace-page', 'x'])
    expect(queryKeys.job(undefined)).toEqual(['job', undefined])
  })

  it('names every root once, under its camel-case name', () => {
    const roots = queryRootList.map(root => root.root)
    expect(new Set(roots).size).toBe(roots.length)
    for (const [name, root] of Object.entries(queryKeys)) expect(name).toBe(root.root.replace(/-([a-z0-9])/g, (_, c: string) => c.toUpperCase()))
  })
})

describe('query key lint guard', () => {
  const eslint = new ESLint()
  const problems = async (code: string, filePath = 'src/features/example/Example.tsx') => {
    const [result] = await eslint.lintText(code, { filePath })
    return result.messages.filter(message => message.ruleId === 'no-restricted-syntax').length
  }

  // One lint run per file: loading the lint config is the slow part.
  it('fails on a key written as a literal array outside the factory, and only there', async () => {
    const literalKeys = [
      "useQuery({ queryKey: ['orders', 'page'], queryFn })",
      "client.invalidateQueries({ queryKey: ['orders'] as const })",
      "client.setQueryData(['connection', id], value)",
      "const statusKey = ['publication-status', id]",
      "export const instantNotificationsQueryKey = (id: string) => ['instant-notifications', id] as const",
      "export function connectionFullResetQueryKey(id: string) { return ['connection-full-reset', id] as const }",
    ]
    expect(await problems(literalKeys.join('\n'))).toBe(literalKeys.length)
    expect(await problems("useQuery({ queryKey: queryKeys.orders('page'), queryFn })\nclient.invalidateQueries({ queryKey: queryKeys.orders() })")).toBe(0)
    expect(await problems("expect(key).toEqual(['orders'])\nuseQuery({ queryKey: ['orders'] })", 'src/features/example/Example.test.tsx')).toBe(0)
    expect(await problems("const statusKey = ['orders']", 'src/shared/api/query-keys.ts')).toBe(0)
  }, 120_000)
})
