/*
 * types.ts — shared types for the AgentFolio saved-posts harvester.
 */

/** One saved LinkedIn post, extracted from a saved-posts card. */
export interface SavedPost {
  /** Canonicalized post URL (tracking query params stripped). */
  postUrl: string | null;
  title: string | null;
  authorName: string | null;
  /** Human-visible "saved on" text, if LinkedIn renders one. */
  savedAt: string | null;
  /** Preview text snippet of the post body. */
  excerpt: string | null;
}

/** Harvester configuration stored in chrome.storage.sync. */
export interface HarvesterConfig {
  serverUrl: string;
  ingestToken: string;
}

export const DEFAULT_CONFIG: HarvesterConfig = {
  serverUrl: "http://127.0.0.1:8080",
  ingestToken: "",
};
