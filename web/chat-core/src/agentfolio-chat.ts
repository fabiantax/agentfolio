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
  /** Force the server tier even when WebGPU is available. */
  forceServer = false;

  private client: ChatClient | null = null;
  private loggedIn = false;
  private local: LocalChat | null = null;
  private localStatus: LocalStatus = 'unavailable';
  private busy = false;
  private transcript: HTMLElement | null = null;
  private tierBadge: HTMLElement | null = null;
  private statusEl: HTMLElement | null = null;
  private input: HTMLInputElement | null = null;

  connectedCallback(): void {
    this.attachShadow({ mode: 'open' });
    this.render();
    // Wire properties from attributes if set before upgrade.
    const url = this.getAttribute('server-url');
    if (url && !this.serverUrl) this.serverUrl = url;
  }

  attributeChangedCallback(name: string, _old: string | null, value: string | null): void {
    if (name === 'server-url') this.serverUrl = value ?? '';
  }

  private ensureClient(): ChatClient {
    if (!this.client || this.lastUrl !== this.serverUrl) {
      this.lastUrl = this.serverUrl;
      this.client = new AgentFolioClient(this.serverUrl);
      this.loggedIn = false;
    }
    return this.client;
  }

  private lastUrl = '';

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

    // Browser tier: WebGPU + successfully loaded local pipeline.
    if (!this.forceServer && hasWebGPU()) {
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
      } catch {
        // Graceful fallback to the server tier.
        this.local = null;
      }
    }

    try {
      const client = this.ensureClient();
      if (!this.loggedIn) {
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
          this.renderError(`Error: ${err.message}`);
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
    style.textContent = `
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
    `;
    this.shadowRoot!.append(style);

    const head = document.createElement('div');
    head.className = 'head';
    const title = document.createElement('strong');
    title.textContent = 'AgentFolio Chat';
    this.tierBadge = document.createElement('span');
    this.tierBadge.className = 'tier';
    this.statusEl = document.createElement('span');
    this.statusEl.className = 'status';
    head.append(title, this.tierBadge, this.statusEl);

    this.transcript = document.createElement('div');
    this.transcript.className = 'log';

    const form = document.createElement('form');
    this.input = document.createElement('input');
    this.input.type = 'text';
    this.input.placeholder = 'Ask about the portfolio…';
    const button = document.createElement('button');
    button.type = 'submit';
    button.textContent = 'Send';
    form.append(this.input, button);
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
