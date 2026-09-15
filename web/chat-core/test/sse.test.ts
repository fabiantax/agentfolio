import { describe, it, expect } from 'vitest';
import { SseParser, parseFrame, parsePayload } from '../src/sse.js';

describe('SseParser', () => {
  it('parses a token event', () => {
    const p = new SseParser();
    const evs = p.push('data: {"text":"hel"}\n\n');
    expect(evs).toEqual([{ kind: 'token', text: 'hel' }]);
  });

  it('parses a token split across chunks', () => {
    const p = new SseParser();
    expect(p.push('data: {"te')).toEqual([]);
    expect(p.push('xt":"lo"}\n\n')).toEqual([{ kind: 'token', text: 'lo' }]);
  });

  it('parses multiple events in one chunk', () => {
    const p = new SseParser();
    const evs = p.push('data: {"text":"a"}\n\ndata: {"text":"b"}\n\n');
    expect(evs).toEqual([
      { kind: 'token', text: 'a' },
      { kind: 'token', text: 'b' },
    ]);
  });

  it('tolerates \\r\\n line endings', () => {
    const p = new SseParser();
    expect(p.push('data: {"text":"x"}\r\n\r\n')).toEqual([{ kind: 'token', text: 'x' }]);
  });

  it('parses the done event with tier and citations', () => {
    const p = new SseParser();
    const evs = p.push(
      'data: {"conversationId":"c1","tier":"Server","citations":[{"url":"https://x.y","title":"X","snippet":"s"}]}\n\n',
    );
    expect(evs).toEqual([
      {
        kind: 'done',
        conversationId: 'c1',
        tier: 'Server',
        citations: [{ url: 'https://x.y', title: 'X', snippet: 's' }],
      },
    ]);
  });

  it('ignores the [DONE] sentinel', () => {
    const p = new SseParser();
    expect(p.push('data: [DONE]\n\n')).toEqual([]);
  });

  it('flushes a trailing unterminated frame', () => {
    const p = new SseParser();
    expect(p.push('data: {"text":"end"}\n')).toEqual([]);
    expect(p.flush()).toEqual([{ kind: 'token', text: 'end' }]);
  });

  it('flags malformed JSON payloads as unknown', () => {
    expect(parseFrame('data: not-json\n\n')).toEqual({ kind: 'unknown', raw: 'not-json' });
  });
});

describe('parsePayload', () => {
  it('classifies token payloads', () => {
    expect(parsePayload({ text: 'hi' })).toEqual({ kind: 'token', text: 'hi' });
  });
  it('classifies done payloads and drops malformed citations', () => {
    const ev = parsePayload({ conversationId: 'c', tier: 'Browser', citations: [{ url: 'u' }, 'junk'] });
    expect(ev).toEqual({
      kind: 'done',
      conversationId: 'c',
      tier: 'Browser',
      citations: [{ url: 'u', title: 'u', snippet: null }],
    });
  });
  it('falls back to unknown for unrecognized shapes', () => {
    expect(parsePayload({ something: 1 }).kind).toBe('unknown');
  });
});
