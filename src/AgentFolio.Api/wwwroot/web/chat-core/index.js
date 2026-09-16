class h {
  constructor() {
    this.buffer = "";
  }
  /** Feed one raw network chunk; returns any events completed by it. */
  push(t) {
    this.buffer += t;
    const e = [];
    let r;
    for (; (r = this.buffer.search(/\r?\n\r?\n/)) !== -1; ) {
      const n = this.buffer.slice(0, r);
      this.buffer = this.buffer.slice(r).replace(/^\r?\n\r?\n/, "");
      const o = u(n);
      o && e.push(o);
    }
    return e;
  }
  /** Flush any buffered frame (call at end of stream). */
  flush() {
    if (!this.buffer.trim())
      return this.buffer = "", [];
    const t = u(this.buffer);
    return this.buffer = "", t ? [t] : [];
  }
}
function u(a) {
  const t = a.split(/\r?\n/).filter((n) => n.startsWith("data:")).map((n) => n.slice(5).replace(/^ /, ""));
  if (t.length === 0) return null;
  const e = t.join(`
`);
  if (e.trim() === "[DONE]") return null;
  let r;
  try {
    r = JSON.parse(e);
  } catch {
    return { kind: "unknown", raw: e };
  }
  return b(r);
}
function b(a) {
  if (typeof a == "object" && a !== null) {
    const t = a;
    if (typeof t.text == "string" && t.conversationId === void 0)
      return { kind: "token", text: t.text };
    if (typeof t.conversationId == "string") {
      const e = Array.isArray(t.citations) ? t.citations : [];
      return {
        kind: "done",
        conversationId: t.conversationId,
        tier: typeof t.tier == "string" ? t.tier : "unknown",
        citations: e.filter((r) => typeof r == "object" && r !== null && typeof r.url == "string").map((r) => ({
          url: r.url,
          title: typeof r.title == "string" ? r.title : r.url,
          snippet: typeof r.snippet == "string" ? r.snippet : null
        }))
      };
    }
  }
  return { kind: "unknown", raw: JSON.stringify(a) };
}
class m {
  constructor(t, e = fetch) {
    this.serverUrl = t, this.fetchImpl = e;
  }
  url(t) {
    return this.serverUrl.replace(/\/+$/, "") + t;
  }
  async login(t) {
    const e = await this.fetchImpl(this.url("/auth/login"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "include",
      body: JSON.stringify({ password: t })
    });
    if (!e.ok)
      throw new Error(`login failed: ${e.status}`);
    return !0;
  }
  async sendMessage(t, e) {
    let r;
    try {
      r = await this.fetchImpl(this.url("/chat"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        credentials: "include",
        body: JSON.stringify({ message: t })
      });
    } catch (i) {
      e.onError(i instanceof Error ? i : new Error(String(i)));
      return;
    }
    if (!r.ok || !r.body) {
      e.onError(new Error(`chat request failed: ${r.status}`));
      return;
    }
    const n = new h(), o = r.body.getReader(), s = new TextDecoder();
    try {
      for (; ; ) {
        const { done: i, value: l } = await o.read();
        if (i) break;
        for (const f of n.push(s.decode(l, { stream: !0 })))
          d(f, e);
      }
      for (const i of n.push(s.decode()))
        d(i, e);
      for (const i of n.flush())
        d(i, e);
    } catch (i) {
      e.onError(i instanceof Error ? i : new Error(String(i)));
    }
  }
}
function d(a, t) {
  a.kind === "token" ? t.onToken(a.text) : a.kind === "done" && t.onDone(a);
}
function p(a = navigator) {
  return typeof a?.gpu == "object" && a.gpu !== null;
}
let c = null;
async function g(a) {
  if (!p()) throw new Error("WebGPU unavailable");
  c || (a?.("loading"), c = (async () => {
    const e = await import(
      /* @vite-ignore */
      "@huggingface/transformers"
    );
    return e.env.allowLocalModels = !1, await e.pipeline(
      "text-generation",
      "onnx-community/LFM2-350M-ONNX",
      { dtype: "q4", device: "webgpu" }
    );
  })().catch((e) => {
    throw c = null, e;
  }));
  try {
    await c;
  } catch (e) {
    throw a?.("error"), e instanceof Error ? e : new Error(String(e));
  }
  a?.("ready");
  const t = await c;
  return {
    async reply(e) {
      let r = "";
      const n = await import("@huggingface/transformers"), o = new n.TextStreamer(
        t.tokenizer,
        { skip_prompt: !0, skip_special_tokens: !0, on_final_text: (s) => {
          r += s;
        } }
      );
      return await t(
        [{ role: "user", content: e }],
        { max_new_tokens: 256, do_sample: !1, streamer: o }
      ), r;
    },
    dispose() {
      c = null;
    }
  };
}
class x extends HTMLElement {
  constructor() {
    super(...arguments), this.serverUrl = "", this.password = "", this.forceServer = !1, this.client = null, this.loggedIn = !1, this.local = null, this.localStatus = "unavailable", this.busy = !1, this.transcript = null, this.tierBadge = null, this.statusEl = null, this.input = null, this.lastUrl = "", this.fetchImpl = null;
  }
  static get observedAttributes() {
    return ["server-url"];
  }
  connectedCallback() {
    this.attachShadow({ mode: "open" }), this.render();
    const t = this.getAttribute("server-url");
    t && !this.serverUrl && (this.serverUrl = t);
  }
  attributeChangedCallback(t, e, r) {
    t === "server-url" && (this.serverUrl = r ?? "");
  }
  ensureClient() {
    return (!this.client || this.lastUrl !== this.serverUrl) && (this.lastUrl = this.serverUrl, this.client = new m(this.serverUrl), this.loggedIn = !1), this.client;
  }
  setStatus(t) {
    this.statusEl && (this.statusEl.textContent = t), this.dispatchEvent(new CustomEvent("af-status", { detail: { status: t }, bubbles: !0, composed: !0 }));
  }
  setTier(t) {
    this.tierBadge && (this.tierBadge.textContent = t === "Browser" ? "browser" : t === "Server" ? "server" : t.toLowerCase(), this.tierBadge.dataset.tier = t);
  }
  appendBubble(t, e) {
    const r = document.createElement("div");
    return r.className = `bubble ${e}`, r.textContent = t, this.transcript?.appendChild(r), this.transcript && (this.transcript.scrollTop = this.transcript.scrollHeight), r;
  }
  renderError(t) {
    this.appendBubble(t, "error"), this.dispatchEvent(new CustomEvent("af-error", { detail: { message: t }, bubbles: !0, composed: !0 }));
  }
  /** Upload a document for the server-tier context (extracted text feeds /chat). */
  async upload(t) {
    if (this.busy) return;
    const e = new FormData();
    e.append("file", t), this.setStatus("uploading"), this.appendBubble(`(uploaded ${t.name})`, "user");
    try {
      const r = this.ensureClient();
      this.loggedIn || (await r.login(this.password), this.loggedIn = !0);
      const n = await this.fetchImpl?.(new URL("/api/uploads?sync=1", this.serverUrl || window.location.origin), { method: "POST", body: e, credentials: "include" }) ?? await fetch(new URL("/api/uploads?sync=1", this.serverUrl || window.location.origin), { method: "POST", body: e, credentials: "include" });
      if (!n.ok) {
        const s = await n.text();
        throw new Error(`upload ${n.status}: ${s.slice(0, 120)}`);
      }
      const o = await n.json();
      this.setStatus(o.extracted === !1 ? "doc: extraction failed" : "doc ready"), o.extracted === !1 && this.appendBubble(`document stored, but text extraction failed (${o.extractionError ?? "unknown"}) — server answers may ignore it`, "error");
    } catch (r) {
      this.renderError(r instanceof Error ? r.message : String(r));
    }
  }
  /** Send one user message; routes to browser LLM when possible, else server SSE. */
  async send(t) {
    const e = (t ?? this.input?.value ?? "").trim();
    if (!e || this.busy) return;
    this.input && (this.input.value = ""), this.appendBubble(e, "user"), this.busy = !0, this.setStatus("loading");
    const r = () => {
      this.busy = !1, this.setStatus("idle");
    };
    if (!this.forceServer && p())
      try {
        this.local || (this.setStatus("browser-loading"), this.local = await g((o) => this.localStatus = o));
        const n = await this.local.reply(e);
        this.setTier("Browser"), this.appendBubble(n, "assistant"), this.dispatchEvent(new CustomEvent("af-done", {
          detail: { conversationId: null, tier: "Browser", citations: [] },
          bubbles: !0,
          composed: !0
        })), r();
        return;
      } catch {
        this.local = null;
      }
    try {
      const n = this.ensureClient();
      this.loggedIn || (await n.login(this.password), this.loggedIn = !0);
      const o = this.appendBubble("", "assistant");
      this.setStatus("streaming"), await n.sendMessage(e, {
        onToken: (s) => {
          o.textContent += s, this.transcript && (this.transcript.scrollTop = this.transcript.scrollHeight), this.dispatchEvent(new CustomEvent("af-token", { detail: { text: s }, bubbles: !0, composed: !0 }));
        },
        onDone: (s) => {
          this.setTier(s.tier), this.renderCitations(s.citations), this.dispatchEvent(new CustomEvent("af-done", { detail: s, bubbles: !0, composed: !0 }));
        },
        onError: (s) => {
          this.renderError(`Error: ${s.message}`);
        }
      });
    } catch (n) {
      this.renderError(n instanceof Error ? n.message : String(n));
    } finally {
      r();
    }
  }
  renderCitations(t) {
    if (!t.length) return;
    const e = document.createElement("div");
    e.className = "citations";
    for (const r of t) {
      const n = document.createElement("a");
      n.href = r.url, n.target = "_blank", n.rel = "noopener noreferrer", n.textContent = r.title, n.title = r.snippet ?? r.title, e.appendChild(n);
    }
    this.transcript?.appendChild(e);
  }
  render() {
    const t = document.createElement("style");
    t.textContent = `
      :host {
        display: block; font: 0.9375rem/1.55 'Inter', system-ui, sans-serif;
        color: #E6EDF3; background: #151B23;
        --af-bg: #0D1117; --af-surface: #151B23; --af-raised: #1C2430; --af-border: #2A3441;
        --af-text: #E6EDF3; --af-text-2: #9BA8B7; --af-accent: #4C8DFF; --af-accent-btn: #2B67D4;
        --af-accent-btn-hover: #2E6ADB; --af-success: #3FB950; --af-danger: #F85149;
        --af-mono: 'JetBrains Mono', ui-monospace, monospace;
      }
      * { box-sizing: border-box; }
      .head { display: flex; align-items: center; gap: 10px; padding: 14px 18px;
              background: var(--af-raised); border-bottom: 1px solid var(--af-border); }
      .head strong { font-size: 0.9375rem; font-weight: 600; letter-spacing: -0.01em; }
      .tier { font: 500 0.6875rem/1.4 var(--af-mono); letter-spacing: 0.08em;
              text-transform: uppercase; padding: 3px 10px; border-radius: 999px;
              background: var(--af-raised); color: var(--af-text-2); border: 1px solid var(--af-border); }
      .tier[data-tier="Browser"] { background: #0E2A13; color: var(--af-success); border-color: #1B4724; }
      .tier[data-tier="Server"] { background: #12233F; color: var(--af-accent); border-color: #1F3A63; }
      .status { margin-left: auto; font: 500 0.6875rem/1.4 var(--af-mono); color: var(--af-text-2); }
      .log { height: 340px; overflow-y: auto; padding: 18px; display: flex;
             flex-direction: column; gap: 12px; background: var(--af-surface); }
      .log:empty::after { content: 'Ask anything - answers cite their sources.';
             color: var(--af-text-2); font-size: 0.875rem; margin: auto; }
      .bubble { max-width: 80%; padding: 10px 14px; border-radius: 14px; white-space: pre-wrap; }
      .bubble.user { align-self: flex-end; background: #12325E; border-bottom-right-radius: 4px; }
      .bubble.assistant { align-self: flex-start; background: var(--af-raised);
                          border: 1px solid var(--af-border); border-bottom-left-radius: 4px; }
      .bubble.error { align-self: stretch; background: #3A1214; color: var(--af-danger);
                      border: 1px solid #5C1E22; max-width: 100%; border-radius: 10px; }
      .citations { align-self: flex-start; display: flex; flex-wrap: wrap; gap: 6px; }
      .citations a { font: 500 0.75rem/1.4 var(--af-mono); color: var(--af-accent);
                     text-decoration: none; background: var(--af-bg);
                     border: 1px solid var(--af-border); padding: 4px 10px; border-radius: 6px; }
      .citations a:hover { border-color: var(--af-accent); }
      form { display: flex; gap: 8px; padding: 12px 14px; border-top: 1px solid var(--af-border);
             background: var(--af-raised); }
      input[type="text"] { flex: 1; padding: 10px 14px; border: 1px solid var(--af-border);
             border-radius: 10px; font: inherit; color: var(--af-text);
             background: #0B0F14; outline: none; }
      input[type="text"]:focus { border-color: var(--af-accent); }
      input[type="text"]::placeholder { color: var(--af-text-2); }
      button { padding: 10px 16px; border: 0; border-radius: 10px; font: 600 0.875rem/1 inherit;
               cursor: pointer; transition: background 0.15s ease; }
      button[type="submit"] { background: var(--af-accent-btn); color: #fff; }
      button[type="submit"]:hover { background: var(--af-accent-btn-hover); }
      button[type="submit"]:disabled { opacity: 0.55; cursor: default; }
      button[type="button"] { background: transparent; color: var(--af-text-2);
              border: 1px solid var(--af-border); font-size: 1rem; padding: 10px 12px; }
      button[type="button"]:hover { color: var(--af-text); border-color: var(--af-text-2); }
      @media (max-width: 640px) { .log { height: 280px; } .bubble { max-width: 92%; } }
    `, this.shadowRoot.append(t);
    const e = document.createElement("div");
    e.className = "head";
    const r = document.createElement("strong");
    r.textContent = "Portfolio Chat", this.tierBadge = document.createElement("span"), this.tierBadge.className = "tier", this.statusEl = document.createElement("span"), this.statusEl.className = "status", e.append(r, this.tierBadge, this.statusEl), this.transcript = document.createElement("div"), this.transcript.className = "log";
    const n = document.createElement("form");
    this.input = document.createElement("input"), this.input.type = "text", this.input.placeholder = "Ask about this portfolio…", this.input.setAttribute("aria-label", "Message");
    const o = document.createElement("button");
    o.type = "button", o.textContent = "📎", o.title = "Upload a document (pdf/txt/md/docx) for grounded answers", o.setAttribute("aria-label", "Upload document");
    const s = document.createElement("input");
    s.type = "file", s.accept = ".pdf,.txt,.md,.docx", s.hidden = !0, o.addEventListener("click", () => s.click()), s.addEventListener("change", () => {
      const l = s.files?.[0];
      l && this.upload(l), s.value = "";
    });
    const i = document.createElement("button");
    i.type = "submit", i.textContent = "Send", n.append(this.input, o, s, i), n.addEventListener("submit", (l) => {
      l.preventDefault(), this.send();
    }), this.shadowRoot.append(e, this.transcript, n);
  }
}
customElements.get("agentfolio-chat") || customElements.define("agentfolio-chat", x);
export {
  x as AgentFolioChatElement,
  m as AgentFolioClient,
  h as SseParser,
  p as hasWebGPU,
  g as loadLocalChat,
  u as parseFrame,
  b as parsePayload
};
//# sourceMappingURL=index.js.map
