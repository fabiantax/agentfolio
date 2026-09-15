/*
 * url.ts — canonicalization of LinkedIn post URLs.
 *
 * LinkedIn decorates internal links with tracking params that change per
 * click/session (li_ucat.*, trackingId, origin, etc.). Canonical URLs keep
 * the path; everything query-tracking-ish is stripped.
 */

/** Params that are pure tracking/session noise on LinkedIn URLs. */
export const TRACKING_PARAM_PATTERNS: RegExp[] = [
  /^li_ucat/i,
  /^tracking/i,
  /^trk/i,
  /^(origin|originalSubdomain|original_referer|ref|refId|src|source|lipi|licu|trkInfo)$/i,
  /^utm_/i,
  /^midToken|midSig| eid(=|$)/i,
  /^eid$/i,
];

export function isTrackingParam(key: string): boolean {
  return TRACKING_PARAM_PATTERNS.some((re) => re.test(key));
}

/**
 * Canonicalize a LinkedIn post URL: absolute-ize, keep path, drop tracking
 * query params and hash. Returns null on unparseable input. Relative paths
 * (starting with /) are resolved against https://www.linkedin.com.
 */
export function canonicalizePostUrl(raw: string | null | undefined): string | null {
  if (!raw) return null;
  let s = raw.trim();
  if (!s) return null;
  // protocol-relative or bare path
  if (s.startsWith("//")) s = "https:" + s;
  else if (s.startsWith("/")) s = "https://www.linkedin.com" + s;
  if (!/^https?:\/\//i.test(s)) return null;

  let u: URL;
  try {
    u = new URL(s);
  } catch {
    return null;
  }
  u.hash = "";
  const kept: string[] = [];
  for (const [k, v] of u.searchParams.entries()) {
    if (!isTrackingParam(k)) kept.push(`${k}=${v}`);
  }
  u.search = kept.length ? "?" + kept.join("&") : "";
  return u.toString();
}
