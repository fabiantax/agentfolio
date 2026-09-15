import { describe, it, expect, vi } from 'vitest';
import { AgentFolioClient } from '../src/client.js';
import { hasWebGPU } from '../src/local.js';

function sseResponse(chunks: string[]): Response {
  const encoder = new TextEncoder();
  let i = 0;
  const body = new ReadableStream<Uint8Array>({
    pull(controller) {
      if (i < chunks.length) controller.enqueue(encoder.encode(chunks[i++]));
      else controller.close();
    },
  });
  return new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } });
}

describe('AgentFolioClient', () => {
  it('login posts the password and reports success', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const c = new AgentFolioClient('https://api.example', fetchImpl as unknown as typeof fetch);
    await expect(c.login('pw')).resolves.toBe(true);
    const [url, init] = fetchImpl.mock.calls[0];
    expect(url).toBe('https://api.example/auth/login');
    expect((init as RequestInit).credentials).toBe('include');
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({ password: 'pw' });
  });

  it('login throws on 401', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(new Response(null, { status: 401 }));
    const c = new AgentFolioClient('https://api.example', fetchImpl as unknown as typeof fetch);
    await expect(c.login('bad')).rejects.toThrow('login failed: 401');
  });

  it('sendMessage streams tokens and finishes with done', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(
      sseResponse([
        'data: {"text":"hello"}\n\n',
        'data: {"text":" world"}\n\n',
        'data: {"conversationId":"c9","tier":"Server","citations":[]}\n\n',
        'data: [DONE]\n\n',
      ]),
    );
    const c = new AgentFolioClient('https://api.example/', fetchImpl as unknown as typeof fetch);
    const tokens: string[] = [];
    const done = vi.fn();
    await c.sendMessage('hi', { onToken: (t) => tokens.push(t), onDone: done, onError: (_e) => expect.unreachable() });
    expect(tokens).toEqual(['hello', ' world']);
    expect(done).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'done', conversationId: 'c9', tier: 'Server' }),
    );
    // trailing slash normalized
    expect(fetchImpl.mock.calls[0][0]).toBe('https://api.example/chat');
  });

  it('reports network errors through onError instead of throwing', async () => {
    const fetchImpl = vi.fn().mockRejectedValue(new TypeError('boom'));
    const c = new AgentFolioClient('https://api.example', fetchImpl as unknown as typeof fetch);
    const onError = vi.fn();
    await c.sendMessage('hi', { onToken: vi.fn(), onDone: vi.fn(), onError });
    expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: 'boom' }));
  });

  it('reports HTTP failures through onError', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(new Response(null, { status: 401 }));
    const c = new AgentFolioClient('https://api.example', fetchImpl as unknown as typeof fetch);
    const onError = vi.fn();
    await c.sendMessage('hi', { onToken: vi.fn(), onDone: vi.fn(), onError });
    expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: 'chat request failed: 401' }));
  });
});

describe('WebGPU detection', () => {
  it('is true when navigator.gpu exists', () => {
    expect(hasWebGPU({ gpu: {} })).toBe(true);
  });
  it('is false when navigator.gpu is missing', () => {
    expect(hasWebGPU({})).toBe(false);
  });
  it('is false when gpu is null', () => {
    expect(hasWebGPU({ gpu: null })).toBe(false);
  });
});
