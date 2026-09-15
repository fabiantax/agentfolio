import '@agentfolio/chat-core';
import type { AgentFolioChatElement } from '@agentfolio/chat-core';

declare module 'react' {
  namespace JSX {
    interface IntrinsicElements {
      'agentfolio-chat': React.DetailedHTMLProps<React.HTMLAttributes<HTMLElement>, HTMLElement> & {
        'server-url'?: string;
        password?: string;
      };
    }
  }
}

declare global {
  // ensure the element class type is exported for tooling
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  type _ChatElement = AgentFolioChatElement;
}

function App() {
  return (
    <main style={{ maxWidth: '720px', margin: '0 auto', padding: '2rem 1rem' }}>
      <h1 style={{ fontSize: '1.4rem' }}>AgentFolio — React host</h1>
      <p style={{ color: '#555' }}>
        Demo page embedding the framework-agnostic <code>&lt;agentfolio-chat&gt;</code> custom
        element. Set a server URL and password to chat against the AgentFolio API; with WebGPU
        available it answers locally via LFM2-350M (q4, ONNX).
      </p>
      <agentfolio-chat server-url="https://localhost:5001" password="dev" />
    </main>
  );
}

export default App;
