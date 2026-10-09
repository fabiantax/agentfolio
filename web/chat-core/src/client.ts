import { SseParser, type ParsedDone, type ParsedToken } from './sse.js';

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

export class AgentFolioClient implements ChatClient {
  public readonly serverUrl: string;
  /**
   * Receiver-normalized fetch. `fetch` is a webIDL brand-checked window
   * method: any receiver-bound invocation — `this.fetchImpl(...)` inside
   * class methods — throws "Failed to execute 'fetch' on 'Window': Illegal
   * invocation" in Chromium. Binding ONCE in the constructor fixes every call
   * site at once; wrapping injected test doubles is a harmless no-op.
   * (Regression: test/fetch-receiver.test.ts.)
   */
  public readonly fetchImpl: typeof fetch;

  constructor(serverUrl: string, fetchImpl: typeof fetch = fetch) {
    this.serverUrl = serverUrl;
    this.fetchImpl = fetchImpl.bind(globalThis);
  }

  private url(path: string): string {
    return this.serverUrl.replace(/\/+$/, '') + path;
  }

  async login(password: string): Promise<boolean> {
    const res = await this.fetchImpl(this.url('/auth/login'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ password }),
    });
    if (!res.ok) {
      throw new Error(`login failed: ${res.status}`);
    }
    return true;
  }

  async capabilities(): Promise<ChatCapabilities> {
    const res = await this.fetchImpl(this.url('/chat/capabilities'), {
      credentials: 'include',
    });
    if (!res.ok) {
      throw new Error(`capabilities failed: ${res.status}`);
    }
    return (await res.json()) as ChatCapabilities;
  }

  async sendMessage(message: string, cb: StreamCallbacks): Promise<void> {
    let res: Response;
    try {
      res = await this.fetchImpl(this.url('/chat'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ message }),
      });
    } catch (err) {
      cb.onError(err instanceof Error ? err : new Error(String(err)));
      return;
    }
    if (!res.ok || !res.body) {
      cb.onError(new Error(`chat request failed: ${res.status}`));
      return;
    }

    const parser = new SseParser();
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        for (const ev of parser.push(decoder.decode(value, { stream: true }))) {
          dispatch(ev, cb);
        }
      }
      for (const ev of parser.push(decoder.decode())) {
        dispatch(ev, cb);
      }
      for (const ev of parser.flush()) {
        dispatch(ev, cb);
      }
    } catch (err) {
      cb.onError(err instanceof Error ? err : new Error(String(err)));
    }
  }
}

function dispatch(ev: ParsedToken | ParsedDone | { kind: 'unknown'; raw: string }, cb: StreamCallbacks): void {
  if (ev.kind === 'token') cb.onToken(ev.text);
  else if (ev.kind === 'done') cb.onDone(ev);
  // 'unknown' frames are ignored deliberately.
}
