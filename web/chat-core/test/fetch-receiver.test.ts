import { describe, it, expect } from 'vitest';
import { AgentFolioClient } from '../src/client.js';

/**
 * Regression: "Failed to execute 'fetch' on 'Window': Illegal invocation".
 *
 * `AgentFolioClient` stored the global `fetch` in a constructor field and later
 * invoked it as `this.fetchImpl(...)` — a receiver-bound call. In Chromium the
 * webIDL brand check rejects receiver-bound window methods, so every upload
 * (and every fresh-session server-tier chat) crashes inside login() with
 * "Illegal invocation". Bun/Node fetch does not brand-check, so plain vitest
 * runs never saw it — which is why this test pins the CALL SHAPE (the receiver
 * the client uses) instead of the browser error text.
 */
describe('fetch receiver binding (Illegal invocation regression)', () => {
  it('client invokes the injected fetch without rebinding it to the instance', async () => {
    let thisArg: unknown | undefined;
    let calls = 0;
    // Function EXPRESSION with `this` typed: in strict-mode modules `this`
    // inside it is the actual call receiver (undefined when called bare, the
    // client instance when called as this.fetchImpl(...) — the bug).
    const fetchImpl = function (this: unknown, ..._args: unknown[]) {
      calls++;
      thisArg = this;
      return Promise.resolve(new Response(null, { status: 204 }));
    } as unknown as typeof fetch;

    const client = new AgentFolioClient('https://api.example', fetchImpl);
    await expect(client.login('pw')).resolves.toBe(true);

    expect(calls).toBe(1);
    // The bug: receiver was the CLIENT instance itself — a foreign object the
    // webIDL brand check rejects in Chromium ("Illegal invocation"). Any of
    // undefined/globalThis is fine; only the instance receiver is the defect.
    expect(thisArg).not.toBe(client);
  });
});
