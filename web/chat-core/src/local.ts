/**
 * Browser-tier (local LLM) support. The @huggingface/transformers module is
 * imported lazily via a runtime specifier that bundlers leave alone, so
 * nothing is downloaded at install/build/test time — only in a browser,
 * after WebGPU detection succeeded.
 */

export type LocalStatus = 'unavailable' | 'loading' | 'ready' | 'error';

/** True when this browser exposes WebGPU. */
export function hasWebGPU(nav: { gpu?: unknown } = navigator as never): boolean {
  return typeof nav?.gpu === 'object' && nav.gpu !== null;
}

export interface LocalChat {
  reply(message: string): Promise<string>;
  dispose(): void;
}

type TextStreamerLike = { on_final_text: (cb: (t: string) => void) => void };
type Pipeline = (messages: Array<{ role: string; content: string }>, opts: unknown) => Promise<unknown>;

let pipePromise: Promise<Pipeline> | null = null;


/**
 * Same-origin URL of the vendor chunk. Derives from import.meta.url at runtime
 * (served as /web/chat-core/index.js) — NEVER a static specifier: Vite lib-mode
 * inlines `new URL('./x.js', import.meta.url)` into a base64 data: URL of the
 * SOURCE (verified in dist), which is unusable as a module path.
 */
function vendorUrl(): string {
  const here = import.meta.url; // .../web/chat-core/index.js
  return here.slice(0, here.lastIndexOf('/') + 1) + 'transformers-vendor.js';
}

/** Lazily load transformers.js + the q4 LFM2-350M ONNX weights. */
export async function loadLocalChat(onStatus?: (s: LocalStatus) => void): Promise<LocalChat> {
  if (!hasWebGPU()) throw new Error('WebGPU unavailable');
  if (!pipePromise) {
    onStatus?.('loading');
    pipePromise = (async () => {
      // Same-origin vendor chunk (browser-local-ml skill: never a CDN —
      // content blockers kill cross-origin dynamic imports before any request
      // leaves the device, and Safari reports only "Load failed").
      const mod = await import(vendorUrl());
      mod.env.allowLocalModels = false;
      mod.env.backends.onnx.wasm.wasmPaths = new URL('./dist/', import.meta.url).href;
      const pipe = await mod.pipeline(
        'text-generation',
        // LFM2.5-350M: newest-gen Liquid chat model; q4f16 = 255 MB download
        // (HF-verified 2026-10-09; smallest viable browser chat class — see
        // browser-local-ml skill: param count ≠ download size, gemma-270m is
        // heavier than this).
        'onnx-community/LFM2.5-350M-ONNX',
        { dtype: 'q4f16', device: 'webgpu' },
      ) as unknown as Pipeline;
      return pipe;
    })().catch((err) => {
      pipePromise = null;
      throw err;
    });
  }
  try {
    await pipePromise;
  } catch (err) {
    onStatus?.('error');
    throw err instanceof Error ? err : new Error(String(err));
  }
  onStatus?.('ready');
  const pipe = await pipePromise;
  return {
    async reply(message: string): Promise<string> {
      let final = '';
      const hf = await import(vendorUrl());
      const streamer = new hf.TextStreamer(
        (pipe as unknown as { tokenizer: unknown }).tokenizer as never,
        { skip_prompt: true, skip_special_tokens: true, on_final_text: (t: string) => { final += t; } },
      );
      
      await pipe(
        [{ role: 'user', content: message }],
        { max_new_tokens: 256, do_sample: false, streamer },
      );
      return final;
    },
    dispose(): void {
      pipePromise = null;
    },
  };
}
