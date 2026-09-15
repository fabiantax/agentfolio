/*
 * queue.test.ts — offline queue + retry behavior with an in-memory storage
 * fake and injected fetch. No chrome.* dependency.
 */
import { describe, expect, it, vi } from "vitest";
import { enqueue, flushQueue, readQueue, QUEUE_CAP, type KVStorage } from "./queue";
import { postSavesBatch, RETRY_BASE_MS } from "./ingest";
import type { SavedPost } from "./types";

function memStore(): KVStorage & { data: Record<string, unknown> } {
  const data: Record<string, unknown> = {};
  return {
    data,
    get: async (d) => ({ ...d, ...data }),
    set: async (i) => void Object.assign(data, i),
  };
}

const post = (url: string, excerpt = "body"): SavedPost => ({
  postUrl: url,
  title: `t-${url}`,
  authorName: "A",
  savedAt: null,
  excerpt,
});

function fetchOk(): typeof fetch {
  return vi.fn(async () =>
    new Response(JSON.stringify({ ingested: 1 }), { status: 200 }),
  ) as unknown as typeof fetch;
}

function fetchNetworkFail(): typeof fetch {
  return vi.fn(async () => {
    throw new TypeError("Failed to fetch");
  }) as unknown as typeof fetch;
}

describe("enqueue / readQueue", () => {
  it("stores batches FIFO", async () => {
    const s = memStore();
    await enqueue([post("https://x/1")], s);
    await enqueue([post("https://x/2")], s);
    const q = await readQueue(s);
    expect(q.length).toBe(2);
    expect(q[0]!.saves[0]!.postUrl).toBe("https://x/1");
  });

  it("caps the queue at QUEUE_CAP", async () => {
    const s = memStore();
    for (let i = 0; i < QUEUE_CAP + 5; i++) {
      await enqueue([post(`https://x/${i}`)], s);
    }
    expect((await readQueue(s)).length).toBe(QUEUE_CAP);
  });
});

describe("flushQueue", () => {
  it("flushes everything when the server is up", async () => {
    const s = memStore();
    await enqueue([post("https://x/1")], s);
    const r = await flushQueue({ serverUrl: "http://svr", fetchFn: fetchOk() }, s);
    expect(r).toEqual({ flushed: 1, remaining: 0 });
    expect((await readQueue(s)).length).toBe(0);
  });

  it("keeps entries and stops on network failure", async () => {
    const s = memStore();
    await enqueue([post("https://x/1")], s);
    await enqueue([post("https://x/2")], s);
    const r = await flushQueue(
      { serverUrl: "http://svr", fetchFn: fetchNetworkFail(), sleepFn: async () => {} },
      s,
    );
    expect(r.flushed).toBe(0);
    expect(r.remaining).toBe(2);
  });

  it("drops permanently-rejected (4xx) batches but keeps later ones", async () => {
    const s = memStore();
    await enqueue([post("https://x/1")], s); // will 400
    await enqueue([post("https://x/2")], s); // will succeed
    let n = 0;
    const fetchFn = vi.fn(async () =>
      new Response(JSON.stringify({}), { status: ++n === 1 ? 400 : 200 }),
    ) as unknown as typeof fetch;
    const r = await flushQueue({ serverUrl: "http://svr", fetchFn }, s);
    expect(r.flushed).toBe(1);
    expect(r.remaining).toBe(0);
  });

  it("does nothing on an empty queue", async () => {
    const s = memStore();
    const r = await flushQueue({ serverUrl: "http://svr", fetchFn: fetchNetworkFail() }, s);
    expect(r).toEqual({ flushed: 0, remaining: 0 });
  });
});

describe("postSavesBatch retry behavior", () => {
  const saves = [post("https://x/1")];

  it("sends Authorization: Bearer token", async () => {
    const fetchFn = vi.fn(async (_u: any, init: any) => {
      expect(init.headers.Authorization).toBe("Bearer tok-123");
      return new Response("{}", { status: 200 });
    }) as unknown as typeof fetch;
    const r = await postSavesBatch("http://svr", saves, { token: "tok-123", fetchFn });
    expect(r.ok).toBe(true);
  });

  it("posts to {serverUrl}/api/ingest/saves", async () => {
    let calledUrl = "";
    const fetchFn = vi.fn(async (u: any) => {
      calledUrl = String(u);
      return new Response("{}", { status: 200 });
    }) as unknown as typeof fetch;
    await postSavesBatch("http://svr/", saves, { fetchFn });
    expect(calledUrl).toBe("http://svr/api/ingest/saves");
  });

  it("retries on network failure with exponential backoff, then succeeds", async () => {
    let calls = 0;
    const fetchFn = vi.fn(async () => {
      if (++calls < 3) throw new TypeError("offline");
      return new Response("{}", { status: 200 });
    }) as unknown as typeof fetch;
    const sleeps: number[] = [];
    const r = await postSavesBatch("http://svr", saves, {
      fetchFn,
      sleepFn: async (ms) => void sleeps.push(ms),
    });
    expect(r.ok).toBe(true);
    expect(calls).toBe(3);
    expect(sleeps).toEqual([RETRY_BASE_MS, RETRY_BASE_MS * 2]);
  });

  it("gives up after MAX_RETRIES and reports network failure", async () => {
    const fetchFn = fetchNetworkFail();
    const r = await postSavesBatch("http://svr", saves, {
      fetchFn,
      sleepFn: async () => {},
    });
    expect(r.ok).toBe(false);
    expect(r.network).toBe(true);
    expect(fetchFn).toHaveBeenCalledTimes(4); // initial + 3 retries
  });

  it("does NOT retry a 400", async () => {
    const fetchFn = vi.fn(async () =>
      new Response("{}", { status: 400 }),
    ) as unknown as typeof fetch;
    const r = await postSavesBatch("http://svr", saves, { fetchFn });
    expect(r.ok).toBe(false);
    expect(fetchFn).toHaveBeenCalledTimes(1);
  });

  it("retries a 503 and gives up after bounds", async () => {
    const fetchFn = vi.fn(async () =>
      new Response("{}", { status: 503 }),
    ) as unknown as typeof fetch;
    const r = await postSavesBatch("http://svr", saves, {
      fetchFn,
      sleepFn: async () => {},
    });
    expect(r.ok).toBe(false);
    expect(fetchFn).toHaveBeenCalledTimes(4);
  });
});
