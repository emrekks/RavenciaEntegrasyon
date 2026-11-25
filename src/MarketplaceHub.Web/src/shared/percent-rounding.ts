// A percentage the panel works out itself rounds half away from zero, as the server's PercentRounding and the money
// figures do (owner, 16 Nov 2026): −12,5 % is −13 %, not −12 % as Math.round (half towards +∞) would give.
// The scaled value is read to 12 significant digits first, so a half the double cannot hold exactly (1,005 × 100 is
// 100,4999…) still rounds up as the server's decimal does (scan D-11).
export function roundPercent(value: number, digits = 0) {
  const scale = 10 ** digits
  const rounded = Math.round(Number((Math.abs(value) * scale).toPrecision(12))) / scale
  return value < 0 ? -rounded : rounded
}
