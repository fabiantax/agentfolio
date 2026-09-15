<script setup lang="ts">
import '@agentfolio/chat-core';
</script>

<template>
  <main>
    <h1>AgentFolio — Vue host</h1>
    <p>
      Demo page embedding the framework-agnostic <code>&lt;agentfolio-chat&gt;</code> custom element.
      Set a server URL and password to chat against the AgentFolio API; with WebGPU available it
      answers locally via LFM2-350M (q4, ONNX).
    </p>
    <!-- eslint-disable-next-line vue/no-deprecated-slot-attribute -->
    <agentfolio-chat
      server-url="https://localhost:5001"
      password="dev"
    />
  </main>
</template>

<style scoped>
main { max-width: 720px; margin: 0 auto; padding: 2rem 1rem; }
h1 { font-size: 1.4rem; }
p { color: #555; }
</style>
