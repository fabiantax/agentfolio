import { type ParsedDone } from './sse.js';
export interface StreamCallbacks {
    onToken: (text: string) => void;
    onDone: (done: ParsedDone) => void;
    onError: (err: Error) => void;
}
export interface ChatCapabilities {
    /** Server-side NIM chat client is configured and resolvable. */
    nim: boolean;
    /** The widget ships a WebGPU browser tier (static capability). */
    browserInference: boolean;
}
export interface ChatClient {
    /** Receiver-normalized fetch used for all transport (DI point for tests). */
    readonly fetchImpl: typeof fetch;
    /** POST /auth/login; resolves true when the session cookie is set. */
    login(password: string): Promise<boolean>;
    /** GET /chat/capabilities; reports which inference surfaces the server offers. */
    capabilities(): Promise<ChatCapabilities>;
    /** POST /chat and stream SSE tokens through the callbacks. */
    sendMessage(message: string, cb: StreamCallbacks): Promise<void>;
}
export declare class AgentFolioClient implements ChatClient {
    readonly serverUrl: string;
    /**
     * Receiver-normalized fetch. `fetch` is a webIDL brand-checked window
     * method: any receiver-bound invocation — `this.fetchImpl(...)` inside
     * class methods — throws "Failed to execute 'fetch' on 'Window': Illegal
     * invocation" in Chromium. Binding ONCE in the constructor fixes every call
     * site at once; wrapping injected test doubles is a harmless no-op.
     * (Regression: test/fetch-receiver.test.ts.)
     */
    readonly fetchImpl: typeof fetch;
    constructor(serverUrl: string, fetchImpl?: typeof fetch);
    private url;
    login(password: string): Promise<boolean>;
    capabilities(): Promise<ChatCapabilities>;
    sendMessage(message: string, cb: StreamCallbacks): Promise<void>;
}
