/**
 * Browser-tier (local LLM) support. The @huggingface/transformers module is
 * imported lazily via a runtime specifier that bundlers leave alone, so
 * nothing is downloaded at install/build/test time — only in a browser,
 * after WebGPU detection succeeded.
 */
export type LocalStatus = 'unavailable' | 'loading' | 'ready' | 'error';
/** True when this browser exposes WebGPU. */
export declare function hasWebGPU(nav?: {
    gpu?: unknown;
}): boolean;
export interface LocalChat {
    reply(message: string): Promise<string>;
    dispose(): void;
}
/** Lazily load transformers.js + the q4 LFM2-350M ONNX weights. */
export declare function loadLocalChat(onStatus?: (s: LocalStatus) => void): Promise<LocalChat>;
