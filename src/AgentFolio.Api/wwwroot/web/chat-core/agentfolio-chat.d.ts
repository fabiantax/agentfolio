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
    /** Force the server tier (attribute: force-server). */
    forceServer: boolean;
    /** Force the browser tier (attribute: force-browser) — explicit user choice, never a silent default. */
    forceBrowser: boolean;
    /** True once the server reports NIM unconfigured (drives the picker default + 503 copy). */
    private nimUnavailable;
    private capabilitiesProbe;
    private client;
    private loggedIn;
    private local;
    private localStatus;
    private busy;
    private transcript;
    private tierBadge;
    private tierSelect;
    private statusEl;
    private input;
    connectedCallback(): void;
    private probeCapabilities;
    /** True when the browser tier CAN run here (WebGPU present). */
    private browserCapable;
    /** True when the user's current picker selection routes a turn to the browser. */
    private browserSelected;
    private updateTierPicker;
    attributeChangedCallback(name: string, _old: string | null, value: string | null): void;
    private ensureClient;
    private lastUrl;
    private lastClient;
    private setStatus;
    private setTier;
    private appendBubble;
    private renderError;
    /** Upload a document for the server-tier context (extracted text feeds /chat). */
    upload(file: File): Promise<void>;
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
