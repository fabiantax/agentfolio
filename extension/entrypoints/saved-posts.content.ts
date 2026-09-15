/*
 * saved-posts.content.ts — content script on linkedin.com/my-items/saved-posts/*.
 *
 * Harvests saved-post cards via ranked selectors, auto-scrolls to load the
 * full list, and hands the batch to the background worker for queueing/POST.
 */
import { autoScroll, harvestSavedPosts } from "../utils/adapters/saved-posts";
import type { SavedPost } from "../utils/types";

const DONE_MSG = "af-harvest-done";

export default defineContentScript({
  matches: ["*://www.linkedin.com/my-items/saved-posts/*"],
  runAt: "document_idle",
  main() {
    let running = false;

    async function harvest() {
      if (running) return;
      running = true;
      try {
        await autoScroll();
        const posts: SavedPost[] = harvestSavedPosts(document);
        // PII-light: drop any field that accidentally captured a profile image URL.
        const clean = posts.map(({ postUrl, title, authorName, savedAt, excerpt }) => ({
          postUrl,
          title,
          authorName,
          savedAt,
          excerpt,
        }));
        const res = await chrome.runtime.sendMessage({
          type: "af:save-batch",
          saves: clean,
        });
        // Surface the outcome for manual inspection / e2e debugging.
        document.dispatchEvent(
          new CustomEvent(DONE_MSG, { detail: res ?? null }),
        );
      } finally {
        running = false;
      }
    }

    // Trigger: toolbar action click is routed by the background worker as a
    // message; also expose a keyboard escape hatch via a message listener.
    chrome.runtime.onMessage.addListener((msg: { type?: string }) => {
      if (msg?.type === "af:harvest") {
        void harvest();
      }
      return false;
    });

    // Auto-harvest once per page load (infinite scroll page loads are rare).
    if (document.readyState === "complete") void harvest();
    else window.addEventListener("load", () => void harvest(), { once: true });
  },
});
