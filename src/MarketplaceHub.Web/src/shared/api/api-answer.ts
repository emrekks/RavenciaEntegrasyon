// The answer types the screens read, from the API contract (generated/api-types.ts).
//
// The contract marks every nullable member of an answer optional (`x?: null | string`): GET /products leaves its null
// members out (CatalogEndpoints.ProductListJson), and the API describes every answer the same way
// (OpenApiContract.DescribeAnswers). Every other answer is written with the web defaults, which write null members, so
// a screen reading one may treat a nullable member as present (`string | null`). ApiAnswer says exactly that and nothing
// more: it makes optional members present and keeps their null. GET /products (getProducts) is the one read that must
// use ApiResponse, where a missing member is real.
import type { ApiOperationId, ApiResponse } from './generated/api-types'

/** `T` as the serializer writes it: every member present, a nullable one as null. */
export type Written<T> =
  T extends string | number | boolean | null | undefined | void ? T
    : T extends readonly (infer Item)[] ? Written<Item>[]
      : { [Key in keyof T]-?: Written<Exclude<T[Key], undefined>> }

/** The success answer of an operation as the screens read it (see the note above). */
export type ApiAnswer<Id extends Exclude<ApiOperationId, 'getProducts'>> = Written<ApiResponse<Id>>
