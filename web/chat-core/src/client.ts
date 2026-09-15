import { SseParser, type ParsedDone, type ParsedToken } from './sse.js';

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

export class AgentFolioClient implements ChatClient {
  constructor(
    private serverUrl: string,
    private fetchImpl: typeof fetch = fetch,
  ) {}

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
