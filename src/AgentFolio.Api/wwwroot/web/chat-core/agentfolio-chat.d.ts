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
export declare class AgentFolioChatElement extends HTMLElement {
    static get observedAttributes(): string[];
    /** API base URL. */
    serverUrl: string;
    /** Password used for the cookie login. */
    password: string;
    /** Force the server tier even when WebGPU is available. */
    forceServer: boolean;
    private client;
    private loggedIn;
    private local;
    private localStatus;
    private busy;
    private transcript;
    private tierBadge;
    private statusEl;
    private input;
    connectedCallback(): void;
    attributeChangedCallback(name: string, _old: string | null, value: string | null): void;
    private ensureClient;
    private lastUrl;
    private setStatus;
    private setTier;
    private appendBubble;
    private renderError;
    /** Send one user message; routes to browser LLM when possible, else server SSE. */
    send(text?: string): Promise<void>;
    private renderCitations;
    private render;
}
declare global {
    interface HTMLElementTagNameMap {
        'agentfolio-chat': AgentFolioChatElement;
    }
}
