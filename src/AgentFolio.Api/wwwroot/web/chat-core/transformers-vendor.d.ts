/**
 * Same-origin vendor re-export of @huggingface/transformers (browser-local-ml
 * skill: CDN dynamic imports die silently on iOS Safari behind content
 * blockers — everything must be served from the widget's own origin).
 * Built as a separate 'transformers-vendor' chunk (~1 MB, loaded only when
 * the user explicitly picks the browser model).
 */
export * from '@huggingface/transformers';
