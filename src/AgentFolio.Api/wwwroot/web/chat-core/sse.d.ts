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
    citations: Array<{
        url: string;
        title: string;
        snippet?: string | null;
    }>;
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
export declare class SseParser {
    private buffer;
    /** Feed one raw network chunk; returns any events completed by it. */
    push(chunk: string): ParsedEvent[];
    /** Flush any buffered frame (call at end of stream). */
    flush(): ParsedEvent[];
}
/** Parse one SSE frame (the lines between blank lines). */
export declare function parseFrame(frame: string): ParsedEvent | null;
/** Parse a JSON payload into a typed event. */
export declare function parsePayload(json: unknown): ParsedEvent;
