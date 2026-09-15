/*
 * dom.ts — tiny DOM helpers. Never throw; missing nodes yield null.
 */

/** Collapsed, trimmed textContent of an element, or null if empty. */
export function txt(el: Element | null | undefined): string | null {
  if (!el) return null;
  const s = (el.textContent || "").replace(/\s+/g, " ").trim();
  return s || null;
}

/** Text of the first matching selector, scoped to `ctx` (default document). */
export function firstText(
  selectors: string[],
  ctx?: ParentNode | null,
): string | null {
  const scope: ParentNode = ctx || document;
  for (const sel of selectors) {
    try {
      const el = scope.querySelector(sel);
      const t = txt(el);
      if (t) return t;
    } catch {
      /* bad selector in some Chrome versions — skip */
    }
  }
  return null;
}

/** First non-empty `attr` value across selectors, scoped to `ctx`. */
export function firstAttr(
  selectors: string[],
  attr: string,
  ctx?: ParentNode | null,
): string | null {
  const scope: ParentNode = ctx || document;
  for (const sel of selectors) {
    try {
      const el = scope.querySelector(sel);
      const v = el?.getAttribute(attr);
      if (v && v.trim()) return v.trim();
    } catch {
      /* skip */
    }
  }
  return null;
}
