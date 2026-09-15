import { defineConfig } from "wxt";

export default defineConfig({
  manifestVersion: 3,
  manifest: {
    name: "AgentFolio — LinkedIn Saved Posts Harvester",
    description:
      "Harvests your LinkedIn saved posts into AgentFolio (offline queue, batched ingest).",
    permissions: ["storage"],
    host_permissions: ["*://*.linkedin.com/*", "http://localhost/*", "http://127.0.0.1/*"],
    action: {
      default_title: "AgentFolio Harvester",
    },
    options_ui: {
      open_in_tab: true,
    },
  },
});
