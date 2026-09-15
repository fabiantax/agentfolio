import '@agentfolio/chat-core';

import { Component, CUSTOM_ELEMENTS_SCHEMA } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

@Component({
  selector: 'app-root',
  standalone: true,
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  template: `
    <main>
      <h1>AgentFolio &mdash; Angular host</h1>
      <p>
        Minimal Angular 20 host embedding the framework-agnostic
        <code>&lt;agentfolio-chat&gt;</code> custom element. With WebGPU available it answers
        locally via LFM2-350M (q4, ONNX); otherwise it falls back to the server SSE tier.
      </p>
      <agentfolio-chat server-url="https://localhost:5001" password="dev"></agentfolio-chat>
    </main>
  `,
  styles: [`
    main { max-width: 720px; margin: 0 auto; padding: 2rem 1rem; font-family: system-ui, sans-serif; }
    h1 { font-size: 1.4rem; }
    p { color: #555; }
  `],
})
export class AppComponent {}

bootstrapApplication(AppComponent).catch((err) => console.error(err));
