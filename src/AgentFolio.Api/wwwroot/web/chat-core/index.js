class h {
  constructor() {
    this.buffer = "";
  }
  /** Feed one raw network chunk; returns any events completed by it. */
  push(t) {
    this.buffer += t;
    const r = [];
    let e;
    for (; (e = this.buffer.search(/\r?\n\r?\n/)) !== -1; ) {
      const s = this.buffer.slice(0, e);
      this.buffer = this.buffer.slice(e).replace(/^\r?\n\r?\n/, "");
      const o = u(s);
      o && r.push(o);
    }
    return r;
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
  const t = a.split(/\r?\n/).filter((s) => s.startsWith("data:")).map((s) => s.slice(5).replace(/^ /, ""));
  if (t.length === 0) return null;
  const r = t.join(`
`);
  if (r.trim() === "[DONE]") return null;
  let e;
  try {
    e = JSON.parse(r);
  } catch {
    return { kind: "unknown", raw: r };
  }
  return b(e);
}
function b(a) {
  if (typeof a == "object" && a !== null) {
    const t = a;
    if (typeof t.text == "string" && t.conversationId === void 0)
      return { kind: "token", text: t.text };
    if (typeof t.conversationId == "string") {
      const r = Array.isArray(t.citations) ? t.citations : [];
      return {
        kind: "done",
        conversationId: t.conversationId,
        tier: typeof t.tier == "string" ? t.tier : "unknown",
        citations: r.filter((e) => typeof e == "object" && e !== null && typeof e.url == "string").map((e) => ({
          url: e.url,
          title: typeof e.title == "string" ? e.title : e.url,
          snippet: typeof e.snippet == "string" ? e.snippet : null
        }))
      };
    }
  }
  return { kind: "unknown", raw: JSON.stringify(a) };
}
class m {
  constructor(t, r = fetch) {
    this.serverUrl = t, this.fetchImpl = r.bind(globalThis);
  }
  url(t) {
    return this.serverUrl.replace(/\/+$/, "") + t;
  }
  async login(t) {
    const r = await this.fetchImpl(this.url("/auth/login"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "include",
      body: JSON.stringify({ password: t })
    });
    if (!r.ok)
      throw new Error(`login failed: ${r.status}`);
    return !0;
  }
  async sendMessage(t, r) {
    let e;
    try {
      e = await this.fetchImpl(this.url("/chat"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        credentials: "include",
        body: JSON.stringify({ message: t })
      });
    } catch (i) {
      r.onError(i instanceof Error ? i : new Error(String(i)));
      return;
    }
    if (!e.ok || !e.body) {
      r.onError(new Error(`chat request failed: ${e.status}`));
      return;
    }
    const s = new h(), o = e.body.getReader(), n = new TextDecoder();
    try {
      for (; ; ) {
        const { done: i, value: l } = await o.read();
        if (i) break;
        for (const f of s.push(n.decode(l, { stream: !0 })))
          d(f, r);
      }
      for (const i of s.push(n.decode()))
        d(i, r);
      for (const i of s.flush())
        d(i, r);
    } catch (i) {
      r.onError(i instanceof Error ? i : new Error(String(i)));
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
    const r = await import(
      /* @vite-ignore */
      "@huggingface/transformers"
    );
    return r.env.allowLocalModels = !1, await r.pipeline(
      "text-generation",
      "onnx-community/LFM2-350M-ONNX",
      { dtype: "q4", device: "webgpu" }
    );
  })().catch((r) => {
    throw c = null, r;
  }));
  try {
    await c;
  } catch (r) {
    throw a?.("error"), r instanceof Error ? r : new Error(String(r));
  }
  a?.("ready");
  const t = await c;
  return {
    async reply(r) {
      let e = "";
      const s = await import("@huggingface/transformers"), o = new s.TextStreamer(
        t.tokenizer,
        { skip_prompt: !0, skip_special_tokens: !0, on_final_text: (n) => {
          e += n;
        } }
      );
      return await t(
        [{ role: "user", content: r }],
        { max_new_tokens: 256, do_sample: !1, streamer: o }
      ), e;
    },
    dispose() {
      c = null;
    }
  };
}
class x extends HTMLElement {
  constructor() {
    super(...arguments), this.serverUrl = "", this.password = "", this.forceServer = !1, this.client = null, this.loggedIn = !1, this.local = null, this.localStatus = "unavailable", this.busy = !1, this.transcript = null, this.tierBadge = null, this.statusEl = null, this.input = null, this.lastUrl = "", this.lastClient = null;
  }
  static get observedAttributes() {
    return ["server-url"];
  }
  connectedCallback() {
    this.attachShadow({ mode: "open" }), this.render();
    const t = this.getAttribute("server-url");
    t && !this.serverUrl && (this.serverUrl = t);
  }
  attributeChangedCallback(t, r, e) {
    t === "server-url" && (this.serverUrl = e ?? "");
  }
  ensureClient() {
    return (!this.lastClient || this.lastUrl !== this.serverUrl) && (this.lastUrl = this.serverUrl, this.lastClient = new m(this.serverUrl), this.loggedIn = !1), this.lastClient;
  }
  setStatus(t) {
    this.statusEl && (this.statusEl.textContent = t), this.dispatchEvent(new CustomEvent("af-status", { detail: { status: t }, bubbles: !0, composed: !0 }));
  }
  setTier(t) {
    this.tierBadge && (this.tierBadge.textContent = t === "Browser" ? "browser" : t === "Server" ? "server" : t.toLowerCase(), this.tierBadge.dataset.tier = t);
  }
  appendBubble(t, r) {
    const e = document.createElement("div");
    return e.className = `bubble ${r}`, e.textContent = t, this.transcript?.appendChild(e), this.transcript && (this.transcript.scrollTop = this.transcript.scrollHeight), e;
  }
  renderError(t) {
    this.appendBubble(t, "error"), this.dispatchEvent(new CustomEvent("af-error", { detail: { message: t }, bubbles: !0, composed: !0 }));
  }
  /** Upload a document for the server-tier context (extracted text feeds /chat). */
  async upload(t) {
    if (this.busy) return;
    const r = new FormData();
    r.append("file", t), this.setStatus("uploading"), this.appendBubble(`(uploaded ${t.name})`, "user");
    try {
      const e = this.ensureClient();
      this.loggedIn || (await e.login(this.password), this.loggedIn = !0);
      const s = await e.fetchImpl(new URL("/api/uploads?sync=1", this.serverUrl || window.location.origin), { method: "POST", body: r, credentials: "include" });
      if (!s.ok) {
        const n = await s.text();
        throw new Error(`upload ${s.status}: ${n.slice(0, 120)}`);
      }
      const o = await s.json();
      this.setStatus(o.extracted === !1 ? "doc: extraction failed" : "doc ready"), o.extracted === !1 && this.appendBubble(`document stored, but text extraction failed (${o.extractionError ?? "unknown"}) — server answers may ignore it`, "error");
    } catch (e) {
      this.renderError(e instanceof Error ? e.message : String(e));
    }
  }
  /** Send one user message; routes to browser LLM when possible, else server SSE. */
  async send(t) {
    const r = (t ?? this.input?.value ?? "").trim();
    if (!r || this.busy) return;
    this.input && (this.input.value = ""), this.appendBubble(r, "user"), this.busy = !0, this.setStatus("loading");
    const e = () => {
      this.busy = !1, this.setStatus("idle");
    };
    if (!this.forceServer && p())
      try {
        this.local || (this.setStatus("browser-loading"), this.local = await g((o) => this.localStatus = o));
        const s = await this.local.reply(r);
        this.setTier("Browser"), this.appendBubble(s, "assistant"), this.dispatchEvent(new CustomEvent("af-done", {
          detail: { conversationId: null, tier: "Browser", citations: [] },
          bubbles: !0,
          composed: !0
        })), e();
        return;
      } catch {
        this.local = null;
      }
    try {
      const s = this.ensureClient();
      this.loggedIn || (await s.login(this.password), this.loggedIn = !0);
      const o = this.appendBubble("", "assistant");
      this.setStatus("streaming"), await s.sendMessage(r, {
        onToken: (n) => {
          o.textContent += n, this.transcript && (this.transcript.scrollTop = this.transcript.scrollHeight), this.dispatchEvent(new CustomEvent("af-token", { detail: { text: n }, bubbles: !0, composed: !0 }));
        },
        onDone: (n) => {
          this.setTier(n.tier), this.renderCitations(n.citations), this.dispatchEvent(new CustomEvent("af-done", { detail: n, bubbles: !0, composed: !0 }));
        },
        onError: (n) => {
          this.renderError(`Error: ${n.message}`);
        }
      });
    } catch (s) {
      this.renderError(s instanceof Error ? s.message : String(s));
    } finally {
      e();
    }
  }
  renderCitations(t) {
    if (!t.length) return;
    const r = document.createElement("div");
    r.className = "citations";
    for (const e of t) {
      const s = document.createElement("a");
      s.href = e.url, s.target = "_blank", s.rel = "noopener noreferrer", s.textContent = e.title, s.title = e.snippet ?? e.title, r.appendChild(s);
    }
    this.transcript?.appendChild(r);
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
    const r = document.createElement("div");
    r.className = "head";
    const e = document.createElement("strong");
    e.textContent = "Portfolio Chat", this.tierBadge = document.createElement("span"), this.tierBadge.className = "tier", this.statusEl = document.createElement("span"), this.statusEl.className = "status", r.append(e, this.tierBadge, this.statusEl), this.transcript = document.createElement("div"), this.transcript.className = "log";
    const s = document.createElement("form");
    this.input = document.createElement("input"), this.input.type = "text", this.input.placeholder = "Ask about this portfolio…", this.input.setAttribute("aria-label", "Message");
    const o = document.createElement("button");
    o.type = "button", o.textContent = "📎", o.title = "Upload a document (pdf/txt/md/docx) for grounded answers", o.setAttribute("aria-label", "Upload document");
    const n = document.createElement("input");
    n.type = "file", n.accept = ".pdf,.txt,.md,.docx", n.hidden = !0, o.addEventListener("click", () => n.click()), n.addEventListener("change", () => {
      const l = n.files?.[0];
      l && this.upload(l), n.value = "";
    });
    const i = document.createElement("button");
    i.type = "submit", i.textContent = "Send", s.append(this.input, o, n, i), s.addEventListener("submit", (l) => {
      l.preventDefault(), this.send();
    }), this.shadowRoot.append(r, this.transcript, s);
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
