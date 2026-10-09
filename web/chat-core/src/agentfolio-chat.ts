import { AgentFolioClient, type ChatClient } from './client.js';
import { hasWebGPU, loadLocalChat, type LocalChat, type LocalStatus } from './local.js';

/**
 * `<agentfolio-chat>` — framework-agnostic chat custom element.
 *
 * Attributes/properties:
 *  - `server-url` (property: serverUrl) — base URL of the AgentFolio API.
 *  - `password` — set before first send; used for POST /auth/login.
 *
 * Events (composed, bubbling):
 *  - `af-token`  detail: { text }
 *  - `af-done`   detail: { conversationId, tier, citations }
 *  - `af-error`  detail: { message }
 *  - `af-status` detail: { status: 'idle'|'loading'|'streaming'|'browser-loading' }
 */
export class AgentFolioChatElement extends HTMLElement {
  static get observedAttributes(): string[] {
    return ['server-url'];
  }

  /** API base URL. */
  serverUrl = '';
  /** Password used for the cookie login. */
  password = '';
  /** Force the server tier (attribute: force-server). */
  forceServer = false;
  /** Force the browser tier (attribute: force-browser) — explicit user choice, never a silent default. */
  forceBrowser = false;
  /** True once the server reports NIM unconfigured (drives the picker default + 503 copy). */
  private nimUnavailable = false;
  private capabilitiesProbe: Promise<void> | null = null;

  private client: ChatClient | null = null;
  private loggedIn = false;
  private local: LocalChat | null = null;
  private localStatus: LocalStatus = 'unavailable';
  private busy = false;
  private transcript: HTMLElement | null = null;
  private tierBadge: HTMLElement | null = null;
  private tierSelect: HTMLSelectElement | null = null;
  private statusEl: HTMLElement | null = null;
  private input: HTMLInputElement | null = null;

  connectedCallback(): void {
    this.attachShadow({ mode: 'open' });
    this.render();
    // Wire properties from attributes if set before upgrade.
    const url = this.getAttribute('server-url');
    if (url && !this.serverUrl) this.serverUrl = url;
    this.forceServer = this.hasAttribute('force-server');
    this.forceBrowser = this.hasAttribute('force-browser');
    // Probe server capabilities once: when NIM is unset and this browser can run
    // the WebGPU tier, default the picker to browser (still user-visible/switchable
    // — an explicit OFFER, not a silent substitution; AGENTS.md rule 1).
    this.probeCapabilities();
  }

  private probeCapabilities(): void {
    if (this.capabilitiesProbe) return;
    this.capabilitiesProbe = this.ensureClient()
      .capabilities()
      .then((caps) => {
        this.nimUnavailable = !caps.nim;
        this.updateTierPicker();
      })
      .catch(() => {
        // Probe failure is non-fatal: leave picker defaults, server errors
        // surface per-message as before.
      });
  }

  /** True when the browser tier CAN run here (WebGPU present). */
  private browserCapable(): boolean {
    return !this.forceServer && hasWebGPU();
  }

  /** True when the user's current picker selection routes a turn to the browser. */
  private browserSelected(): boolean {
    if (this.forceServer) return false;
    if (this.forceBrowser) return true;
    const sel = this.tierSelect?.value;
    if (sel === 'browser') return true;
    if (sel === 'server') return false;
    // Auto: browser only when the server explicitly lacks NIM.
    return this.nimUnavailable && this.browserCapable();
  }

  private updateTierPicker(): void {
    if (!this.tierSelect) return;
    if (this.nimUnavailable) {
      const auto = this.tierSelect.querySelector('option[value="auto"]');
      if (auto) auto.textContent = 'auto (server offline — browser model)';
    }
  }

  attributeChangedCallback(name: string, _old: string | null, value: string | null): void {
    if (name === 'server-url') this.serverUrl = value ?? '';
  }

  private ensureClient(): ChatClient {
    if (!this.lastClient || this.lastUrl !== this.serverUrl) {
      this.lastUrl = this.serverUrl;
      this.lastClient = new AgentFolioClient(this.serverUrl);
      this.loggedIn = false;
    }
    return this.lastClient;
  }

  private lastUrl = '';
  private lastClient: ChatClient | null = null;

  private setStatus(s: string): void {
    if (this.statusEl) this.statusEl.textContent = s;
    this.dispatchEvent(new CustomEvent('af-status', { detail: { status: s }, bubbles: true, composed: true }));
  }

  private setTier(tier: string): void {
    if (this.tierBadge) {
      this.tierBadge.textContent = tier === 'Browser' ? 'browser' : tier === 'Server' ? 'server' : tier.toLowerCase();
      this.tierBadge.dataset.tier = tier;
    }
  }

  private appendBubble(text: string, cls: string): HTMLElement {
    const div = document.createElement('div');
    div.className = `bubble ${cls}`;
    div.textContent = text;
    this.transcript?.appendChild(div);
    if (this.transcript) this.transcript.scrollTop = this.transcript.scrollHeight;
    return div;
  }

  private renderError(msg: string): void {
    this.appendBubble(msg, 'error');
    this.dispatchEvent(new CustomEvent('af-error', { detail: { message: msg }, bubbles: true, composed: true }));
  }

  /** Upload a document for the server-tier context (extracted text feeds /chat). */
  async upload(file: File): Promise<void> {
    if (this.busy) return;
    const form = new FormData();
    form.append('file', file);
    this.setStatus('uploading');
    this.appendBubble(`(uploaded ${file.name})`, 'user');
    try {
      const client = this.ensureClient();
      // Login ONLY when a password was explicitly provided. On the share page
      // the unlock POST already signed the visitor in (auth cookie, tenant
      // tid claim) — a password-less login attempt there would 401 and mask
      // an otherwise-working upload.
      if (!this.loggedIn && this.password) {
        await client.login(this.password);
        this.loggedIn = true;
      }
      const res = await client.fetchImpl(new URL('/api/uploads?sync=1', this.serverUrl || window.location.origin), { method: 'POST', body: form, credentials: 'include' } as RequestInit);
      if (!res.ok) {
        const body = await res.text();
        throw new Error(`upload ${res.status}: ${body.slice(0, 120)}`);
      }
      const receipt = await res.json() as { extracted?: boolean; extractionError?: string | null };
      this.setStatus(receipt.extracted === false ? 'doc: extraction failed' : 'doc ready');
      if (receipt.extracted === false) {
        this.appendBubble(`document stored, but text extraction failed (${receipt.extractionError ?? 'unknown'}) — server answers may ignore it`, 'error');
      }
    } catch (err) {
      this.renderError(err instanceof Error ? err.message : String(err));
    }
  }

  /** Send one user message; routes to browser LLM when possible, else server SSE. */
  async send(text?: string): Promise<void> {
    const message = (text ?? this.input?.value ?? '').trim();
    if (!message || this.busy) return;
    if (this.input) this.input.value = '';
    this.appendBubble(message, 'user');
    this.busy = true;
    this.setStatus('loading');

    const finish = () => {
      this.busy = false;
      this.setStatus('idle');
    };

    // Browser tier ONLY when explicitly selected (picker or force-browser attr).
    // NO silent browser-first fallback (AGENTS.md rule 1): the tier is the user's
    // choice, shown in the badge; failures are reported, never swallowed.
    if (this.browserSelected()) {
      try {
        if (!this.local) {
          this.setStatus('browser-loading');
          this.local = await loadLocalChat((s) => (this.localStatus = s));
        }
        const reply = await this.local.reply(message);
        this.setTier('Browser');
        this.appendBubble(reply, 'assistant');
        this.dispatchEvent(new CustomEvent('af-done', {
          detail: { conversationId: null, tier: 'Browser', citations: [] },
          bubbles: true, composed: true,
        }));
        finish();
        return;
      } catch (err) {
        // Load/run failure is REAL: surface it and stop. The user explicitly
        // chose the browser tier — quietly switching to the server would hide
        // the breakage (rule 1). Switching tiers is a visible user action.
        this.local = null;
        const msg = err instanceof Error ? err.message : String(err);
        this.renderError(`browser model failed: ${msg} — switch the model picker to "server" or reload to retry`);
        finish();
        return;
      }
    }

    try {
      const client = this.ensureClient();
      // Login ONLY when a password was explicitly provided (owner/host pages).
      // On the share page the unlock POST already signed the visitor in —
      // see the matching comment in upload().
      if (!this.loggedIn && this.password) {
        await client.login(this.password);
        this.loggedIn = true;
      }
      const bubble = this.appendBubble('', 'assistant');
      this.setStatus('streaming');
      await client.sendMessage(message, {
        onToken: (t) => {
          bubble.textContent += t;
          if (this.transcript) this.transcript.scrollTop = this.transcript.scrollHeight;
          this.dispatchEvent(new CustomEvent('af-token', { detail: { text: t }, bubbles: true, composed: true }));
        },
        onDone: (d) => {
          this.setTier(d.tier);
          this.renderCitations(d.citations);
          this.dispatchEvent(new CustomEvent('af-done', { detail: d, bubbles: true, composed: true }));
        },
        onError: (err) => {
          if (err.message.includes('503') || err.message.includes('nim_not_configured')) {
            this.renderError(
              this.browserCapable()
                ? 'Error: server chat is not configured (no NIM backend). Use the model picker above — "browser" runs a small LLM locally.'
                : 'Error: server chat is not configured (no NIM backend) and this browser cannot run the local model (WebGPU missing).'
            );
          } else {
            this.renderError(`Error: ${err.message}`);
          }
        },
      });
    } catch (err) {
      this.renderError(err instanceof Error ? err.message : String(err));
    } finally {
      finish();
    }
  }

  private renderCitations(citations: Array<{ url: string; title: string; snippet?: string | null }>): void {
    if (!citations.length) return;
    const wrap = document.createElement('div');
    wrap.className = 'citations';
    for (const c of citations) {
      const a = document.createElement('a');
      a.href = c.url;
      a.target = '_blank';
      a.rel = 'noopener noreferrer';
      a.textContent = c.title;
      a.title = c.snippet ?? c.title;
      wrap.appendChild(a);
    }
    this.transcript?.appendChild(wrap);
  }

    private render(): void {
      const style = document.createElement('style');
      // Design tokens per DESIGN.md (AA-verified); shadow-scoped copies of the page vars.
      style.textContent = `
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
    `;
      this.shadowRoot!.append(style);

      const head = document.createElement('div');
      head.className = 'head';
      const title = document.createElement('strong');
      title.textContent = 'Portfolio Chat';
      this.tierBadge = document.createElement('span');
      this.tierBadge.className = 'tier';
      this.statusEl = document.createElement('span');
      this.statusEl.className = 'status';

      // Model picker: explicit tier choice (AGENTS.md rule 1 — an offer, not a
      // substitution). auto = server when configured, browser when it is not.
      this.tierSelect = document.createElement('select');
      this.tierSelect.className = 'tier-select';
      this.tierSelect.setAttribute('aria-label', 'Chat model');
      for (const [v, label] of [
        ['auto', 'auto (server model)'],
        ['browser', 'browser (local LLM)'],
        ['server', 'server'],
      ] as const) {
        const opt = document.createElement('option');
        opt.value = v;
        opt.textContent = label;
        this.tierSelect.appendChild(opt);
      }

      head.append(title, this.tierBadge, this.statusEl, this.tierSelect);

      this.transcript = document.createElement('div');
      this.transcript.className = 'log';

      const form = document.createElement('form');
      this.input = document.createElement('input');
      this.input.type = 'text';
      this.input.placeholder = 'Ask about this portfolio\u2026';
      this.input.setAttribute('aria-label', 'Message');

      const uploadBtn = document.createElement('button');
      uploadBtn.type = 'button';
      uploadBtn.textContent = '\u{1F4CE}';
      uploadBtn.title = 'Upload a document (pdf/txt/md/docx) for grounded answers';
      uploadBtn.setAttribute('aria-label', 'Upload document');
      const fileInput = document.createElement('input');
      fileInput.type = 'file';
      fileInput.accept = '.pdf,.txt,.md,.docx';
      fileInput.hidden = true;
      uploadBtn.addEventListener('click', () => fileInput.click());
      fileInput.addEventListener('change', () => {
        const f = fileInput.files?.[0];
        if (f) void this.upload(f);
        fileInput.value = '';
      });

      const button = document.createElement('button');
      button.type = 'submit';
      button.textContent = 'Send';
      form.append(this.input, uploadBtn, fileInput, button);
      form.addEventListener('submit', (e) => {
        e.preventDefault();
        void this.send();
      });

      this.shadowRoot!.append(head, this.transcript, form);
    }
  }

if (!customElements.get('agentfolio-chat')) {
  customElements.define('agentfolio-chat', AgentFolioChatElement);
}

declare global {
  interface HTMLElementTagNameMap {
    'agentfolio-chat': AgentFolioChatElement;
  }
}
