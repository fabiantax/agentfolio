class f {
  constructor() {
    this.buffer = "";
  }
  /** Feed one raw network chunk; returns any events completed by it. */
  push(t) {
    this.buffer += t;
    const e = [];
    let r;
    for (; (r = this.buffer.search(/\r?\n\r?\n/)) !== -1; ) {
      const s = this.buffer.slice(0, r);
      this.buffer = this.buffer.slice(r).replace(/^\r?\n\r?\n/, "");
      const o = u(s);
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
function u(i) {
  const t = i.split(/\r?\n/).filter((s) => s.startsWith("data:")).map((s) => s.slice(5).replace(/^ /, ""));
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
function b(i) {
  if (typeof i == "object" && i !== null) {
    const t = i;
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
  return { kind: "unknown", raw: JSON.stringify(i) };
}
class g {
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
    } catch (a) {
      e.onError(a instanceof Error ? a : new Error(String(a)));
      return;
    }
    if (!r.ok || !r.body) {
      e.onError(new Error(`chat request failed: ${r.status}`));
      return;
    }
    const s = new f(), o = r.body.getReader(), n = new TextDecoder();
    try {
      for (; ; ) {
        const { done: a, value: l } = await o.read();
        if (a) break;
        for (const h of s.push(n.decode(l, { stream: !0 })))
          c(h, e);
      }
      for (const a of s.push(n.decode()))
        c(a, e);
      for (const a of s.flush())
        c(a, e);
    } catch (a) {
      e.onError(a instanceof Error ? a : new Error(String(a)));
    }
  }
}
function c(i, t) {
  i.kind === "token" ? t.onToken(i.text) : i.kind === "done" && t.onDone(i);
}
function p(i = navigator) {
  return typeof i?.gpu == "object" && i.gpu !== null;
}
let d = null;
async function m(i) {
  if (!p()) throw new Error("WebGPU unavailable");
  d || (i?.("loading"), d = (async () => {
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
    throw d = null, e;
  }));
  try {
    await d;
  } catch (e) {
    throw i?.("error"), e instanceof Error ? e : new Error(String(e));
  }
  i?.("ready");
  const t = await d;
  return {
    async reply(e) {
      let r = "";
      const s = await import("@huggingface/transformers"), o = new s.TextStreamer(
        t.tokenizer,
        { skip_prompt: !0, skip_special_tokens: !0, on_final_text: (n) => {
          r += n;
        } }
      );
      return await t(
        [{ role: "user", content: e }],
        { max_new_tokens: 256, do_sample: !1, streamer: o }
      ), r;
    },
    dispose() {
      d = null;
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
    return (!this.client || this.lastUrl !== this.serverUrl) && (this.lastUrl = this.serverUrl, this.client = new g(this.serverUrl), this.loggedIn = !1), this.client;
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
      const s = await this.fetchImpl?.(new URL("/api/uploads?sync=1", this.serverUrl || window.location.origin), { method: "POST", body: e, credentials: "include" }) ?? await fetch(new URL("/api/uploads?sync=1", this.serverUrl || window.location.origin), { method: "POST", body: e, credentials: "include" });
      if (!s.ok) {
        const n = await s.text();
        throw new Error(`upload ${s.status}: ${n.slice(0, 120)}`);
      }
      const o = await s.json();
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
        this.local || (this.setStatus("browser-loading"), this.local = await m((o) => this.localStatus = o));
        const s = await this.local.reply(e);
        this.setTier("Browser"), this.appendBubble(s, "assistant"), this.dispatchEvent(new CustomEvent("af-done", {
          detail: { conversationId: null, tier: "Browser", citations: [] },
          bubbles: !0,
          composed: !0
        })), r();
        return;
      } catch {
        this.local = null;
      }
    try {
      const s = this.ensureClient();
      this.loggedIn || (await s.login(this.password), this.loggedIn = !0);
      const o = this.appendBubble("", "assistant");
      this.setStatus("streaming"), await s.sendMessage(e, {
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
      r();
    }
  }
  renderCitations(t) {
    if (!t.length) return;
    const e = document.createElement("div");
    e.className = "citations";
    for (const r of t) {
      const s = document.createElement("a");
      s.href = r.url, s.target = "_blank", s.rel = "noopener noreferrer", s.textContent = r.title, s.title = r.snippet ?? r.title, e.appendChild(s);
    }
    this.transcript?.appendChild(e);
  }
  render() {
    const t = document.createElement("style");
    t.textContent = `
      :host { display:block; font: 14px/1.5 system-ui, sans-serif; color:#1a1a2e;
              border:1px solid #e2e2ec; border-radius:12px; overflow:hidden; background:#fff; }
      .head { display:flex; align-items:center; gap:8px; padding:10px 14px;
              background:#f6f6fb; border-bottom:1px solid #e2e2ec; }
      .head strong { font-size:13px; letter-spacing:.02em; }
      .tier { font-size:11px; padding:2px 8px; border-radius:999px; background:#e8e8f5; color:#555; }
      .tier[data-tier="Browser"] { background:#e0f5e8; color:#186a3b; }
      .tier[data-tier="Server"] { background:#e8f0fb; color:#1a4d8f; }
      .status { margin-left:auto; font-size:11px; color:#888; }
      .log { height:280px; overflow-y:auto; padding:12px; display:flex;
             flex-direction:column; gap:8px; background:#fcfcfe; }
      .bubble { max-width:78%; padding:8px 12px; border-radius:12px; white-space:pre-wrap; }
      .bubble.user { align-self:flex-end; background:#3b5bdb; color:#fff; border-bottom-right-radius:4px; }
      .bubble.assistant { align-self:flex-start; background:#eeeef6; border-bottom-left-radius:4px; }
      .bubble.error { align-self:stretch; background:#fdecec; color:#a02020; max-width:100%; }
      .citations { align-self:flex-start; display:flex; flex-wrap:wrap; gap:6px; }
      .citations a { font-size:12px; color:#3b5bdb; text-decoration:none; background:#eef1fd;
                     padding:3px 8px; border-radius:6px; }
      form { display:flex; gap:8px; padding:10px; border-top:1px solid #e2e2ec; }
      input { flex:1; padding:8px 10px; border:1px solid #d8d8e6; border-radius:8px; font:inherit; }
      button { padding:8px 14px; border:0; border-radius:8px; background:#3b5bdb; color:#fff;
               font:inherit; cursor:pointer; }
      button:disabled { opacity:.5; cursor:default; }
    `, this.shadowRoot.append(t);
    const e = document.createElement("div");
    e.className = "head";
    const r = document.createElement("strong");
    r.textContent = "AgentFolio Chat", this.tierBadge = document.createElement("span"), this.tierBadge.className = "tier", this.statusEl = document.createElement("span"), this.statusEl.className = "status", e.append(r, this.tierBadge, this.statusEl), this.transcript = document.createElement("div"), this.transcript.className = "log";
    const s = document.createElement("form");
    this.input = document.createElement("input"), this.input.type = "text", this.input.placeholder = "Ask about the portfolio…";
    const o = document.createElement("button");
    o.type = "button", o.textContent = "📎", o.title = "Upload a document (pdf/txt/md/docx) for grounded answers";
    const n = document.createElement("input");
    n.type = "file", n.accept = ".pdf,.txt,.md,.docx", n.hidden = !0, o.addEventListener("click", () => n.click()), n.addEventListener("change", () => {
      const l = n.files?.[0];
      l && this.upload(l), n.value = "";
    });
    const a = document.createElement("button");
    a.type = "submit", a.textContent = "Send", s.append(this.input, o, n, a), s.addEventListener("submit", (l) => {
      l.preventDefault(), this.send();
    }), this.shadowRoot.append(e, this.transcript, s);
  }
}
customElements.get("agentfolio-chat") || customElements.define("agentfolio-chat", x);
export {
  x as AgentFolioChatElement,
  g as AgentFolioClient,
  f as SseParser,
  p as hasWebGPU,
  m as loadLocalChat,
  u as parseFrame,
  b as parsePayload
};
//# sourceMappingURL=index.js.map
