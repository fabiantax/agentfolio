/*
 * queue.ts — offline queue of saved posts in chrome.storage.local, with
 * flush-on-reconnect semantics. Storage is injectable so tests can use an
 * in-memory fake instead of chrome.storage.
 */
import { postSavesBatch, type PostBatchOptions } from "./ingest";
import type { SavedPost } from "./types";

export const QUEUE_KEY = "af_saved_posts_queue";
export const QUEUE_CAP = 500;

export interface QueueEntry {
  saves: SavedPost[];
  queuedAt: number;
}

/** Minimal chrome.storage.local-shaped interface. */
export interface KVStorage {
  get(defaults: Record<string, unknown>): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
}

export const chromeStorage: KVStorage = {
  get: async (d) => chrome.storage.local.get(d),
  set: async (i) => {
    await chrome.storage.local.set(i);
  },
};

export async function readQueue(store: KVStorage = chromeStorage): Promise<QueueEntry[]> {
  const o = await store.get({ [QUEUE_KEY]: [] as QueueEntry[] });
  const v = o[QUEUE_KEY];
  return Array.isArray(v) ? (v as QueueEntry[]) : [];
}

async function writeQueue(items: QueueEntry[], store: KVStorage): Promise<void> {
  await store.set({ [QUEUE_KEY]: items });
}

/** Enqueue a batch of saves (capped so a long offline streak can't blow storage). */
export async function enqueue(
  saves: SavedPost[],
  store: KVStorage = chromeStorage,
): Promise<number> {
  const q = await readQueue(store);
  q.push({ saves, queuedAt: Date.now() });
  const capped = q.slice(-QUEUE_CAP);
  await writeQueue(capped, store);
  return capped.length;
}

/** Flush attempt deps — injectable fetch so tests stay offline. */
export type FlushDeps = PostBatchOptions & {
  serverUrl: string;
};

/**
 * Try to flush queued batches to the server. Network failure stops the flush
 * and keeps remaining entries; permanent 4xx drops the batch.
 */
export async function flushQueue(
  deps: FlushDeps,
  store: KVStorage = chromeStorage,
): Promise<{ flushed: number; remaining: number }> {
  const q = await readQueue(store);
  if (!q.length) return { flushed: 0, remaining: 0 };

  let flushed = 0;
  const survivors: QueueEntry[] = [];
  for (let i = 0; i < q.length; i++) {
    const entry = q[i]!;
    const r = await postSavesBatch(deps.serverUrl, entry.saves, deps);
    if (r.ok) {
      flushed++;
    } else if (r.network) {
      for (let j = i; j < q.length; j++) survivors.push(q[j]!);
      break;
    } else {
      // Permanent server rejection (4xx) — drop so it can't wedge the queue.
      console.warn("[agentfolio] dropping queued batch, server rejected:", r.status);
    }
  }
  await writeQueue(survivors, store);
  return { flushed, remaining: survivors.length };
}
