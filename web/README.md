# web/

Frontend workspace for AgentFolio.

- `chat-core/` — `@agentfolio/chat-core`: the framework-agnostic `<agentfolio-chat>` custom
  element (shadow DOM, SSE streaming via fetch reader, cookie login, WebGPU/browser-tier
  local LLM with graceful server fallback). Zero runtime deps; `@huggingface/transformers`
  is loaded lazily from CDN only when WebGPU is detected.
- `hosts/vue` — Vue 3 + Vite (vue-ts template) demo host.
- `hosts/react` — React + Vite (react-ts template) demo host.
- `hosts/angular` — minimal Angular 20 host (hand-rolled bootstrap + `CUSTOM_ELEMENTS_SCHEMA`).

Build/test each package: `bun install && bun run build` (`bun run test` in chat-core).
