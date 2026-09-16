import { type ParsedDone } from './sse.js';
export interface StreamCallbacks {
    onToken: (text: string) => void;
    onDone: (done: ParsedDone) => void;
    onError: (err: Error) => void;
}
export interface ChatClient {
    /** POST /auth/login; resolves true when the session cookie is set. */
    login(password: string): Promise<boolean>;
    /** POST /chat and stream SSE tokens through the callbacks. */
    sendMessage(message: string, cb: StreamCallbacks): Promise<void>;
}
export declare class AgentFolioClient implements ChatClient {
    private serverUrl;
    private fetchImpl;
    constructor(serverUrl: string, fetchImpl?: typeof fetch);
    private url;
    login(password: string): Promise<boolean>;
    sendMessage(message: string, cb: StreamCallbacks): Promise<void>;
}
