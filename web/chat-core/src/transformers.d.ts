/** Ambient declaration for the lazily-imported transformers.js module. */
declare module '@huggingface/transformers' {
  export const env: { allowLocalModels: boolean };
  export function pipeline(task: string, model: string, opts?: Record<string, unknown>): Promise<unknown>;
  export class TextStreamer {
    constructor(tokenizer: unknown, opts?: Record<string, unknown>);
  }
}
