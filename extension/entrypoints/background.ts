/*
 * background.ts — AgentFolio harvester service worker (MV3).
 *
 * Owns: config, batch POST to {serverUrl}/api/ingest/saves with Bearer auth,
 * offline queue in chrome.storage.local, bounded retry, flush on reconnect.
 */
import { getConfig } from "../utils/config";
import { enqueue, flushQueue, readQueue } from "../utils/queue";
import { postSavesBatch } from "../utils/ingest";
import type { SavedPost } from "../utils/types";

interface SaveBatchMsg {
  type: "af:save-batch";
  saves: SavedPost[];
}
interface FlushMsg {
  type: "af:flush";
}
interface GetQueueMsg {
  type: "af:get-queue";
}
type HarvesterMsg = SaveBatchMsg | FlushMsg | GetQueueMsg;

async function handleSaveBatch(saves: SavedPost[]) {
  const valid = saves.filter((s) => s.postUrl);
  if (!valid.length) {
    return { ok: false, queued: false, error: "no valid post URLs" };
  }
  const config = await getConfig();
  // Opportunistic flush of anything stuck before this save.
  if (config.serverUrl) {
    await flushQueue(config).catch(() => {});
  }
  const r = await postSavesBatch(config.serverUrl, valid, { token: config.ingestToken });
  if (r.ok) {
    return { ok: true, queued: false, ingested: valid.length, data: r.data };
  }
  if (r.network) {
    const n = await enqueue(valid);
    return { ok: false, queued: true, queueLength: n, error: r.error };
  }
  return { ok: false, queued: false, status: r.status, error: r.error };
}

export default defineBackground(() => {
  chrome.runtime.onMessage.addListener(
    (msg: HarvesterMsg, _sender, sendResponse) => {
      (async () => {
        switch (msg.type) {
          case "af:save-batch":
            sendResponse(await handleSaveBatch(msg.saves));
            break;
          case "af:flush": {
            const config = await getConfig();
            sendResponse(
              await flushQueue(config).catch(async (e) => ({
                flushed: 0,
                remaining: (await readQueue()).length,
                error: String(e),
              })),
            );
            break;
          }
          case "af:get-queue":
            sendResponse({ queue: await readQueue() });
            break;
          default:
            sendResponse({ ok: false, error: "unknown message" });
        }
      })();
      return true; // async sendResponse
    },
  );

  // Flush when the network comes back.
  try {
    // chrome.online lands in recent Chrome MV3 typings; guard for older ones.
    const online = (chrome as unknown as {
      online?: { addEventListener?: (t: string, cb: () => void) => void };
    }).online;
    online?.addEventListener?.("online", () => {
      void getConfig()
        .then((config) => flushQueue(config))
        .catch(() => {});
    });
  } catch {
    // chrome.online is unavailable in some test contexts — non-fatal.
  }

  // Also flush on worker startup (MV3 workers wake frequently).
  void getConfig()
    .then((config) => (config.serverUrl ? flushQueue(config) : undefined))
    .catch(() => {});
});
