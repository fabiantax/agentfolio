#!/usr/bin/env bash
# AgentFolio Clean-Architecture gate — Atlas code-graph boundary enforcement.
# Usage: ./scripts/arch-gate.sh  (run from repo root; expects atlas CLI in PATH)
# Exit 0 = clean, 1 = violations found, 2 = tool failure.
set -uo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

command -v atlas >/dev/null 2>&1 || { echo "arch-gate: atlas CLI not found"; exit 2; }

# Store path is deterministic: ~/.atlas/graphfusion/<basename of repo root>/
STORE="$HOME/.atlas/graphfusion/$(basename "$REPO_ROOT")"
if [ ! -d "$STORE" ]; then
  echo "arch-gate: no atlas store at $STORE — running atlas scan ."
  atlas scan . >/dev/null 2>&1 || { echo "arch-gate: atlas scan failed"; exit 2; }
fi

Q() { atlas graphfusion-query "$STORE" "$1" --format json 2>/dev/null; }

VIOLATIONS=0
report() { # name, query, expected-zero-field
  local name="$1" query="$2"
  local out; out=$(Q "$query")
  local count; count=$(echo "$out" | python3 -c "import sys,json;d=json.load(sys.stdin);print(sum(int(r.get('count',0)) for r in d) if isinstance(d,list) else int(d.get('count',0)))" 2>/dev/null || echo "0")
  if [ "$count" != "0" ] && [ -n "$count" ]; then
    echo "❌ $name: $count violation(s)"
    echo "$out" | head -20
    VIOLATIONS=$((VIOLATIONS+1))
  else
    echo "✅ $name"
  fi
}

# R1: Slice→Slice references are banned (Team Topologies: stream-aligned slices don't see each other)
report "slice-isolation" \
  "MATCH (a)-[:REFERENCES]->(b) WHERE a.file_path CONTAINS 'Slices/Chat' AND b.file_path CONTAINS 'Slices/Stars' RETURN count(a) AS count"

# R2: Agents must not reference Platform internals (complicated-subsystem independence)
report "agents-no-platform" \
  "MATCH (a)-[:REFERENCES]->(b) WHERE a.file_path CONTAINS 'AgentFolio.Agents' AND b.file_path CONTAINS 'AgentFolio.Platform' RETURN count(a) AS count"

# R3: Contracts must not reference anything inward (pure kernel)
report "contracts-purity" \
  "MATCH (a)-[:REFERENCES]->(b) WHERE a.file_path CONTAINS 'AgentFolio.Contracts' AND (b.file_path CONTAINS 'AgentFolio.Platform' OR b.file_path CONTAINS 'AgentFolio.Agents' OR b.file_path CONTAINS 'Slices') RETURN count(a) AS count"

if [ "$VIOLATIONS" -gt 0 ]; then
  echo "arch-gate: $VIOLATIONS rule(s) violated"
  exit 1
fi
echo "arch-gate: all clean-architecture rules pass"
exit 0
