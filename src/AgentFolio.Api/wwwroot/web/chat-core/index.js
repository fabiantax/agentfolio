class f {
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
      const i = h(s);
      i && r.push(i);
    }
    return r;
  }
  /** Flush any buffered frame (call at end of stream). */
  flush() {
    if (!this.buffer.trim())
      return this.buffer = "", [];
    const t = h(this.buffer);
    return this.buffer = "", t ? [t] : [];
  }
}
function h(n) {
  const t = n.split(/\r?\n/).filter((s) => s.startsWith("data:")).map((s) => s.slice(5).replace(/^ /, ""));
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
  return m(e);
}
function m(n) {
  if (typeof n == "object" && n !== null) {
    const t = n;
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
  return { kind: "unknown", raw: JSON.stringify(n) };
}
class g {
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
  async capabilities() {
    const t = await this.fetchImpl(this.url("/chat/capabilities"), {
      credentials: "include"
    });
    if (!t.ok)
      throw new Error(`capabilities failed: ${t.status}`);
    return await t.json();
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
    } catch (o) {
      r.onError(o instanceof Error ? o : new Error(String(o)));
      return;
    }
    if (!e.ok || !e.body) {
      r.onError(new Error(`chat request failed: ${e.status}`));
      return;
    }
    const s = new f(), i = e.body.getReader(), a = new TextDecoder();
    try {
      for (; ; ) {
        const { done: o, value: l } = await i.read();
        if (o) break;
        for (const d of s.push(a.decode(l, { stream: !0 })))
          p(d, r);
      }
      for (const o of s.push(a.decode()))
        p(o, r);
      for (const o of s.flush())
        p(o, r);
    } catch (o) {
      r.onError(o instanceof Error ? o : new Error(String(o)));
    }
  }
}
function p(n, t) {
  n.kind === "token" ? t.onToken(n.text) : n.kind === "done" && t.onDone(n);
}
function b(n = navigator) {
  return typeof n?.gpu == "object" && n.gpu !== null;
}
let c = null;
async function v(n) {
  if (!b()) throw new Error("WebGPU unavailable");
  c || (n?.("loading"), c = (async () => {
    const r = await import(
      /* @vite-ignore */
      new URL("data:video/mp2t;base64,LyoqCiAqIFNhbWUtb3JpZ2luIHZlbmRvciByZS1leHBvcnQgb2YgQGh1Z2dpbmdmYWNlL3RyYW5zZm9ybWVycyAoYnJvd3Nlci1sb2NhbC1tbAogKiBza2lsbDogQ0ROIGR5bmFtaWMgaW1wb3J0cyBkaWUgc2lsZW50bHkgb24gaU9TIFNhZmFyaSBiZWhpbmQgY29udGVudAogKiBibG9ja2VycyDigJQgZXZlcnl0aGluZyBtdXN0IGJlIHNlcnZlZCBmcm9tIHRoZSB3aWRnZXQncyBvd24gb3JpZ2luKS4KICogQnVpbHQgYXMgYSBzZXBhcmF0ZSAndHJhbnNmb3JtZXJzLXZlbmRvcicgY2h1bmsgKH4xIE1CLCBsb2FkZWQgb25seSB3aGVuCiAqIHRoZSB1c2VyIGV4cGxpY2l0bHkgcGlja3MgdGhlIGJyb3dzZXIgbW9kZWwpLgogKi8KZXhwb3J0ICogZnJvbSAnQGh1Z2dpbmdmYWNlL3RyYW5zZm9ybWVycyc7Cg==", import.meta.url).href
    );
    return r.env.allowLocalModels = !1, r.env.backends.onnx.wasm.wasmPaths = new URL("./dist/", import.meta.url).href, await r.pipeline(
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
    throw n?.("error"), r instanceof Error ? r : new Error(String(r));
  }
  n?.("ready");
  const t = await c;
  return {
    async reply(r) {
      let e = "";
      const s = await import(
        /* @vite-ignore */
        new URL("data:video/mp2t;base64,LyoqCiAqIFNhbWUtb3JpZ2luIHZlbmRvciByZS1leHBvcnQgb2YgQGh1Z2dpbmdmYWNlL3RyYW5zZm9ybWVycyAoYnJvd3Nlci1sb2NhbC1tbAogKiBza2lsbDogQ0ROIGR5bmFtaWMgaW1wb3J0cyBkaWUgc2lsZW50bHkgb24gaU9TIFNhZmFyaSBiZWhpbmQgY29udGVudAogKiBibG9ja2VycyDigJQgZXZlcnl0aGluZyBtdXN0IGJlIHNlcnZlZCBmcm9tIHRoZSB3aWRnZXQncyBvd24gb3JpZ2luKS4KICogQnVpbHQgYXMgYSBzZXBhcmF0ZSAndHJhbnNmb3JtZXJzLXZlbmRvcicgY2h1bmsgKH4xIE1CLCBsb2FkZWQgb25seSB3aGVuCiAqIHRoZSB1c2VyIGV4cGxpY2l0bHkgcGlja3MgdGhlIGJyb3dzZXIgbW9kZWwpLgogKi8KZXhwb3J0ICogZnJvbSAnQGh1Z2dpbmdmYWNlL3RyYW5zZm9ybWVycyc7Cg==", import.meta.url).href
      ), i = new s.TextStreamer(
        t.tokenizer,
        { skip_prompt: !0, skip_special_tokens: !0, on_final_text: (a) => {
          e += a;
        } }
      );
      return await t(
        [{ role: "user", content: r }],
        { max_new_tokens: 256, do_sample: !1, streamer: i }
      ), e;
    },
    dispose() {
      c = null;
    }
  };
}
class x extends HTMLElement {
  constructor() {
    super(...arguments), this.serverUrl = "", this.password = "", this.forceServer = !1, this.forceBrowser = !1, this.nimUnavailable = !1, this.capabilitiesProbe = null, this.client = null, this.loggedIn = !1, this.local = null, this.localStatus = "unavailable", this.busy = !1, this.transcript = null, this.tierBadge = null, this.tierSelect = null, this.statusEl = null, this.input = null, this.lastUrl = "", this.lastClient = null;
  }
  static get observedAttributes() {
    return ["server-url"];
  }
  connectedCallback() {
    this.attachShadow({ mode: "open" }), this.render();
    const t = this.getAttribute("server-url");
    t && !this.serverUrl && (this.serverUrl = t), this.forceServer = this.hasAttribute("force-server"), this.forceBrowser = this.hasAttribute("force-browser"), this.probeCapabilities();
  }
  probeCapabilities() {
    this.capabilitiesProbe || (this.capabilitiesProbe = this.ensureClient().capabilities().then((t) => {
      this.nimUnavailable = !t.nim, this.updateTierPicker();
    }).catch(() => {
    }));
  }
  /** True when the browser tier CAN run here (WebGPU present). */
  browserCapable() {
    return !this.forceServer && b();
  }
  /** True when the user's current picker selection routes a turn to the browser. */
  browserSelected() {
    if (this.forceServer) return !1;
    if (this.forceBrowser) return !0;
    const t = this.tierSelect?.value;
    return t === "browser" ? !0 : t === "server" ? !1 : this.nimUnavailable && this.browserCapable();
  }
  updateTierPicker() {
    if (this.tierSelect && this.nimUnavailable) {
      const t = this.tierSelect.querySelector('option[value="auto"]');
      t && (t.textContent = "auto (server offline — browser model)");
    }
  }
  attributeChangedCallback(t, r, e) {
    t === "server-url" && (this.serverUrl = e ?? "");
  }
  ensureClient() {
    return (!this.lastClient || this.lastUrl !== this.serverUrl) && (this.lastUrl = this.serverUrl, this.lastClient = new g(this.serverUrl), this.loggedIn = !1), this.lastClient;
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
      !this.loggedIn && this.password && (await e.login(this.password), this.loggedIn = !0);
      const s = await e.fetchImpl(new URL("/api/uploads?sync=1", this.serverUrl || window.location.origin), { method: "POST", body: r, credentials: "include" });
      if (!s.ok) {
        const a = await s.text();
        throw new Error(`upload ${s.status}: ${a.slice(0, 120)}`);
      }
      const i = await s.json();
      this.setStatus(i.extracted === !1 ? "doc: extraction failed" : "doc ready"), i.extracted === !1 && this.appendBubble(`document stored, but text extraction failed (${i.extractionError ?? "unknown"}) — server answers may ignore it`, "error");
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
    if (this.browserSelected())
      try {
        this.local || (this.setStatus("browser-loading"), this.local = await v((i) => this.localStatus = i));
        const s = await this.local.reply(r);
        this.setTier("Browser"), this.appendBubble(s, "assistant"), this.dispatchEvent(new CustomEvent("af-done", {
          detail: { conversationId: null, tier: "Browser", citations: [] },
          bubbles: !0,
          composed: !0
        })), e();
        return;
      } catch (s) {
        this.local = null;
        const i = s instanceof Error ? s.message : String(s);
        this.renderError(`browser model failed: ${i} — switch the model picker to "server" or reload to retry`), e();
        return;
      }
    try {
      const s = this.ensureClient();
      !this.loggedIn && this.password && (await s.login(this.password), this.loggedIn = !0);
      const i = this.appendBubble("", "assistant");
      this.setStatus("streaming"), await s.sendMessage(r, {
        onToken: (a) => {
          i.textContent += a, this.transcript && (this.transcript.scrollTop = this.transcript.scrollHeight), this.dispatchEvent(new CustomEvent("af-token", { detail: { text: a }, bubbles: !0, composed: !0 }));
        },
        onDone: (a) => {
          this.setTier(a.tier), this.renderCitations(a.citations), this.dispatchEvent(new CustomEvent("af-done", { detail: a, bubbles: !0, composed: !0 }));
        },
        onError: (a) => {
          a.message.includes("503") || a.message.includes("nim_not_configured") ? this.renderError(
            this.browserCapable() ? 'Error: server chat is not configured (no NIM backend). Use the model picker above — "browser" runs a small LLM locally.' : "Error: server chat is not configured (no NIM backend) and this browser cannot run the local model (WebGPU missing)."
          ) : this.renderError(`Error: ${a.message}`);
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
      .tier-select { font: 500 0.6875rem/1.4 var(--af-mono); color: var(--af-text-2);
              background: var(--af-bg); border: 1px solid var(--af-border); border-radius: 6px;
              padding: 3px 6px; cursor: pointer; outline: none; }
      .tier-select:focus { border-color: var(--af-accent); }
      .tier-select option { background: var(--af-bg); color: var(--af-text); }
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
    e.textContent = "Portfolio Chat", this.tierBadge = document.createElement("span"), this.tierBadge.className = "tier", this.statusEl = document.createElement("span"), this.statusEl.className = "status", this.tierSelect = document.createElement("select"), this.tierSelect.className = "tier-select", this.tierSelect.setAttribute("aria-label", "Chat model");
    for (const [l, d] of [
      ["auto", "auto (server model)"],
      ["browser", "browser (local LLM)"],
      ["server", "server"]
    ]) {
      const u = document.createElement("option");
      u.value = l, u.textContent = d, this.tierSelect.appendChild(u);
    }
    r.append(e, this.tierBadge, this.statusEl, this.tierSelect), this.transcript = document.createElement("div"), this.transcript.className = "log";
    const s = document.createElement("form");
    this.input = document.createElement("input"), this.input.type = "text", this.input.placeholder = "Ask about this portfolio…", this.input.setAttribute("aria-label", "Message");
    const i = document.createElement("button");
    i.type = "button", i.textContent = "📎", i.title = "Upload a document (pdf/txt/md/docx) for grounded answers", i.setAttribute("aria-label", "Upload document");
    const a = document.createElement("input");
    a.type = "file", a.accept = ".pdf,.txt,.md,.docx", a.hidden = !0, i.addEventListener("click", () => a.click()), a.addEventListener("change", () => {
      const l = a.files?.[0];
      l && this.upload(l), a.value = "";
    });
    const o = document.createElement("button");
    o.type = "submit", o.textContent = "Send", s.append(this.input, i, a, o), s.addEventListener("submit", (l) => {
      l.preventDefault(), this.send();
    }), this.shadowRoot.append(r, this.transcript, s);
  }
}
customElements.get("agentfolio-chat") || customElements.define("agentfolio-chat", x);
export {
  x as AgentFolioChatElement,
  g as AgentFolioClient,
  f as SseParser,
  b as hasWebGPU,
  v as loadLocalChat,
  h as parseFrame,
  m as parsePayload
};
//# sourceMappingURL=index.js.map
