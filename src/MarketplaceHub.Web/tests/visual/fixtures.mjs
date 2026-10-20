// The mocked API's data, one module per area. Each entry is [matcher, answer]: the matcher is "METHOD /path" (GET when
// the method is left out) with the path after /api/v1, or a "^regex"; the answer is a JSON body or a function of
// { url, path, match, body } that returns one (undefined answers 204). The first match wins.
import { shell } from './fixtures/shell.mjs'
import { sales } from './fixtures/sales.mjs'
import { catalog } from './fixtures/catalog.mjs'
import { backOffice } from './fixtures/back-office.mjs'

export const fixtures = [...shell, ...sales, ...catalog, ...backOffice]
