import "./style.css";
import { getConfig, setConfig } from "../../utils/config";

const $ = (id: string): HTMLInputElement =>
  document.getElementById(id) as HTMLInputElement;

async function load() {
  const cfg = await getConfig();
  $("serverUrl").value = cfg.serverUrl;
  $("ingestToken").value = cfg.ingestToken;
}

async function save(e: Event) {
  e.preventDefault();
  await setConfig({
    serverUrl: $("serverUrl").value.trim().replace(/\/+$/, ""),
    ingestToken: $("ingestToken").value.trim(),
  });
  const status = document.getElementById("status")!;
  status.textContent = "Saved ✓";
  setTimeout(() => (status.textContent = ""), 2000);
}

document.addEventListener("DOMContentLoaded", () => {
  void load();
  document.getElementById("config-form")?.addEventListener("submit", (e) => void save(e));
});
