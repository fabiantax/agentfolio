/*
 * saved-posts.ts — LinkedIn /my-items/saved-posts/ DOM adapter.
 *
 * Resilient by design: LinkedIn rotates hashed class names between releases.
 * Every card field is extracted via ranked selector arrays, first match wins;
 * a missing node yields null, never a throw.
 *
 * PII policy: we capture post facts only. Profile image/avatar URLs are
 * explicitly never read.
 */

import { firstAttr, firstText, txt } from "../dom";
import { canonicalizePostUrl } from "../url";
import type { SavedPost } from "../types";

// ---------------------------------------------------------------------------
// Ranked selectors — primary → fallback. Exported so fixture-backed tests can
// audit them without touching extraction logic.
// ---------------------------------------------------------------------------

/** A saved-post card in the my-items list. */
export const CARD_SELECTORS = [
  // 2024+ saved items list: entity-result cards
  "div.entity-result",
  "div.feed-shared-update-v2",
  "li.saves-item",
  "[data-view-name='saved-item']",
];

export const POST_URL_SELECTORS = [
  "a[href*='/posts/'][href*='activity']",
  "a[href*='/feed/update/']",
  "a[href*='/ugcPost/']",
  "a.app-aware-link[href]",
  "a[href*='urn:li:activity']",
  // last resort: first content link in the card
  "a[href*='linkedin.com/feed/']",
  "a[href]",
];

export const TITLE_SELECTORS = [
  ".entity-result__title-text",
  "a.app-aware-link",
  "a[href*='/posts/']",
  "[data-view-name='saved-item-title']",
  "h3",
  "h2",
];

export const AUTHOR_SELECTORS = [
  ".entity-result__primary-subtitle span",
  ".entity-result__title-text a span",
  ".update-v2-social-activity__person-name",
  ".feed-shared-actor__name span",
  "[data-view-name='saved-item-author']",
  ".t-16.t-black.t-bold span",
];

export const SAVED_AT_SELECTORS = [
  "time[datetime]",
  ".entity-result__subtitle time",
  "[data-view-name='saved-item-timestamp'] time",
];

export const EXCERPT_SELECTORS = [
  ".entity-result__summary",
  ".feed-shared-update-v2__description .break-words",
  ".update-components-text",
  "[data-view-name='saved-item-excerpt']",
  "p",
];

// ---------------------------------------------------------------------------

/**
 * Extract one SavedPost from a card element. Null-never-throw: fields that
 * can't be found come back null (postUrl null means the card is skipped by
 * harvest()).
 */
export function extractSavedPost(card: Element): SavedPost {
  const rawUrl =
    firstAttr(POST_URL_SELECTORS, "href", card) ??
    // fallback: scan every anchor for a post-looking href
    (() => {
      const anchors = card.querySelectorAll("a[href]");
      for (const a of anchors) {
        const h = a.getAttribute("href") || "";
        if (/(\/posts\/|\/feed\/update\/|activity)/.test(h)) return h;
      }
      return null;
    })();

  return {
    postUrl: canonicalizePostUrl(rawUrl),
    title: firstText(TITLE_SELECTORS, card),
    authorName: firstText(AUTHOR_SELECTORS, card),
    savedAt: firstText(SAVED_AT_SELECTORS, card),
    excerpt: firstText(EXCERPT_SELECTORS, card),
  };
}

/** Extract all harvestable saved posts from a document root. */
export function harvestSavedPosts(doc: ParentNode): SavedPost[] {
  const out: SavedPost[] = [];
  const seen = new Set<string>();
  for (const sel of CARD_SELECTORS) {
    try {
      doc.querySelectorAll(sel).forEach((card) => {
        const p = extractSavedPost(card);
        const key = p.postUrl || (p.title || "") + "|" + (p.excerpt || "");
        if (!p.postUrl || seen.has(key)) return;
        seen.add(key);
        out.push(p);
      });
      if (out.length) break; // first card selector that yields wins
    } catch {
      /* skip */
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// Auto-scroll pagination
// ---------------------------------------------------------------------------

export interface ScrollOptions {
  /** Stop after this many consecutive scrolls that add no new cards. */
  maxIdleCycles?: number;
  /** Millis to wait after each scroll for new cards. */
  waitForNewMs?: number;
  /** Hard cap on total scroll cycles. */
  maxCycles?: number;
}

export const DEFAULT_SCROLL_OPTIONS: Required<ScrollOptions> = {
  maxIdleCycles: 3,
  waitForNewMs: 1200,
  maxCycles: 60,
};

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function countCards(): number {
  for (const sel of CARD_SELECTORS) {
    try {
      const n = document.querySelectorAll(sel).length;
      if (n) return n;
    } catch {
      /* skip */
    }
  }
  return 0;
}

/**
 * Scroll to bottom repeatedly until no new cards appear for `maxIdleCycles`
 * consecutive cycles (or `maxCycles` total). Returns the final card count.
 */
export async function autoScroll(opts: ScrollOptions = {}): Promise<number> {
  const o = { ...DEFAULT_SCROLL_OPTIONS, ...opts };
  let idle = 0;
  let last = countCards();
  for (let i = 0; i < o.maxCycles && idle < o.maxIdleCycles; i++) {
    window.scrollTo(0, document.documentElement.scrollHeight);
    await sleep(o.waitForNewMs);
    const now = countCards();
    if (now > last) {
      idle = 0;
      last = now;
    } else {
      idle++;
    }
  }
  return last;
}
