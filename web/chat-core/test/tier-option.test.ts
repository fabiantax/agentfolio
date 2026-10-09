import { describe, it, expect, vi } from 'vitest';
import { AgentFolioClient } from '../src/client.js';
import { hasWebGPU } from '../src/local.js';

describe('tier option (explicit, no silent substitution)', () => {
  it('capabilities endpoint is fetched with credentials included', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ nim: false, browserInference: true }), { status: 200 }),
    );
    const c = new AgentFolioClient('https://api.example', fetchImpl as unknown as typeof fetch);
    const caps = await c.capabilities();
    expect(fetchImpl.mock.calls[0][0]).toBe('https://api.example/chat/capabilities');
    expect((fetchImpl.mock.calls[0][1] as RequestInit).credentials).toBe('include');
    expect(caps.nim).toBe(false);
    expect(caps.browserInference).toBe(true);
  });

  it('capabilities propagates non-OK as an error', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(new Response(null, { status: 500 }));
    const c = new AgentFolioClient('https://api.example', fetchImpl as unknown as typeof fetch);
    await expect(c.capabilities()).rejects.toThrow('capabilities failed: 500');
  });
});
