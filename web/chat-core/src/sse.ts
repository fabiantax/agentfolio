/**
 * Minimal SSE parser for the AgentFolio chat stream.
 *
 * Wire format (src/Slices/Chat/ChatEndpoints.cs): each event is
 * `data: {json}\n\n` where the JSON is either a ChatTokenEvent
 * `{ "text": "..." }`, a ChatCompletionEvent
 * `{ "conversationId", "tier", "citations": [{url,title,snippet}] }`,
 * or the literal `[DONE]` sentinel.
 */

export interface ParsedToken {
  kind: 'token';
  text: string;
}
export interface ParsedDone {
  kind: 'done';
  conversationId: string;
  tier: string;
  citations: Array<{ url: string; title: string; snippet?: string | null }>;
}
export interface ParsedUnknown {
  kind: 'unknown';
  raw: string;
}
export type ParsedEvent = ParsedToken | ParsedDone | ParsedUnknown;

/**
 * Incremental SSE line parser. Feed raw stream chunks into `push`; it emits
 * parsed events as soon as complete `data:` frames arrive.
 */
export class SseParser {
  private buffer = '';

  /** Feed one raw network chunk; returns any events completed by it. */
  push(chunk: string): ParsedEvent[] {
    this.buffer += chunk;
    const events: ParsedEvent[] = [];
    // SSE frames are separated by a blank line; tolerate \r\n as well.
    let idx: number;
    while ((idx = this.buffer.search(/\r?\n\r?\n/)) !== -1) {
      const frame = this.buffer.slice(0, idx);
      this.buffer = this.buffer.slice(idx).replace(/^\r?\n\r?\n/, '');
      const ev = parseFrame(frame);
      if (ev) events.push(ev);
    }
    return events;
  }

  /** Flush any buffered frame (call at end of stream). */
  flush(): ParsedEvent[] {
    if (!this.buffer.trim()) {
      this.buffer = '';
      return [];
    }
    const ev = parseFrame(this.buffer);
    this.buffer = '';
    return ev ? [ev] : [];
  }
}

/** Parse one SSE frame (the lines between blank lines). */
export function parseFrame(frame: string): ParsedEvent | null {
  const dataLines = frame
    .split(/\r?\n/)
    .filter((l) => l.startsWith('data:'))
    .map((l) => l.slice(5).replace(/^ /, ''));
  if (dataLines.length === 0) return null;
  const payload = dataLines.join('\n');
  if (payload.trim() === '[DONE]') return null;

  let json: unknown;
  try {
    json = JSON.parse(payload);
  } catch {
    return { kind: 'unknown', raw: payload };
  }
  return parsePayload(json);
}

/** Parse a JSON payload into a typed event. */
export function parsePayload(json: unknown): ParsedEvent {
  if (typeof json === 'object' && json !== null) {
    const obj = json as Record<string, unknown>;
    if (typeof obj.text === 'string' && obj.conversationId === undefined) {
      return { kind: 'token', text: obj.text };
    }
    if (typeof obj.conversationId === 'string') {
      const citations = Array.isArray(obj.citations) ? obj.citations : [];
      return {
        kind: 'done',
        conversationId: obj.conversationId,
        tier: typeof obj.tier === 'string' ? obj.tier : 'unknown',
        citations: citations
          .filter((c): c is { url: string; title: string; snippet?: string | null } =>
            typeof c === 'object' && c !== null && typeof (c as { url?: unknown }).url === 'string')
          .map((c) => ({
            url: c.url,
            title: typeof c.title === 'string' ? c.title : c.url,
            snippet: typeof c.snippet === 'string' ? c.snippet : null,
          })),
      };
    }
  }
  return { kind: 'unknown', raw: JSON.stringify(json) };
}
