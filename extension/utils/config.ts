/*
 * config.ts — typed read/write of harvester config (serverUrl + ingest token)
 * from chrome.storage.sync.
 */
import { DEFAULT_CONFIG, type HarvesterConfig } from "./types";
import type { KVStorage } from "./queue";
import { chromeStorage } from "./queue";

const CONFIG_KEY = "af_config";

export async function getConfig(store: KVStorage = chromeStorage): Promise<HarvesterConfig> {
  const o = await store.get({ [CONFIG_KEY]: DEFAULT_CONFIG });
  const v = o[CONFIG_KEY] as Partial<HarvesterConfig> | undefined;
  return { ...DEFAULT_CONFIG, ...v };
}

export async function setConfig(
  cfg: Partial<HarvesterConfig>,
  store: KVStorage = chromeStorage,
): Promise<void> {
  const cur = await getConfig(store);
  await store.set({ [CONFIG_KEY]: { ...cur, ...cfg } });
}
