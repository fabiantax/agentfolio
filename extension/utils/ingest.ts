/*
 * ingest.ts — batch POST of saved posts to {serverUrl}/api/ingest/saves
 * with Bearer-token auth and bounded exponential-backoff retry.
 * Pure functions (fetch-injectable) so queue logic is testable.
 */
import type { SavedPost } from "./types";

export const INGEST_PATH = "/api/ingest/saves";

export const MAX_RETRIES = 3;
export const RETRY_BASE_MS = 500;

export interface PostResult {
  ok: boolean;
  status: number;
  error: string | null;
  /** True when the failure looks like a network/offline problem (retriable, queueable). */
  network: boolean;
  data?: unknown;
}

export interface PostDeps {
  fetchFn?: typeof fetch;
  sleepFn?: (ms: number) => Promise<void>;
}

const defaultSleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export interface PostBatchOptions extends PostDeps {
  token?: string;
}

async function postOnce(
  serverUrl: string,
  body: { saves: SavedPost[] },
  opts: PostBatchOptions,
): Promise<PostResult> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    Accept: "application/json",
  };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  const base = serverUrl.replace(/\/+$/, "");
  try {
    const res = await (opts.fetchFn || fetch)(base + INGEST_PATH, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
    });
    let data: unknown = null;
    const text = await res.text();
    if (text) {
      try {
        data = JSON.parse(text);
      } catch {
        data = { raw: text };
      }
    }
    if (!res.ok) {
      return {
        ok: false,
        status: res.status,
        error: `Server ${res.status}`,
        network: false,
        data,
      };
    }
    return { ok: true, status: res.status, error: null, network: false, data };
  } catch (e) {
    return {
      ok: false,
      status: 0,
      error: e instanceof Error ? e.message : String(e),
      network: true,
    };
  }
}

/** POST with bounded exponential-backoff retry on network/5xx. */
export async function postSavesBatch(
  serverUrl: string,
  saves: SavedPost[],
  opts: PostBatchOptions = {},
): Promise<PostResult> {
  const sleepFn = opts.sleepFn || defaultSleep;
  let last: PostResult = {
    ok: false,
    status: 0,
    error: "no attempt",
    network: true,
  };
  for (let attempt = 0; attempt <= MAX_RETRIES; attempt++) {
    last = await postOnce(serverUrl, { saves }, opts);
    if (last.ok) return last;
    const retriable = last.network || (last.status >= 500 && last.status <= 599);
    if (!retriable) return last;
    if (attempt < MAX_RETRIES) await sleepFn(RETRY_BASE_MS * Math.pow(2, attempt));
  }
  return last;
}
