#!/usr/bin/env bash
# AgentFolio Clean-Architecture gate — Atlas code-graph boundary enforcement.
# Usage: ./scripts/arch-gate.sh  (run from repo root; expects atlas CLI in PATH)
# Exit 0 = clean, 1 = violations found, 2 = tool failure.
#
# Rules:
#   R1 slice-isolation     : no cross-slice REFERENCES between any Slices/A and Slices/B (A≠B)
#   R2 agents-no-platform  : AgentFolio.Agents must not reference AgentFolio.Platform
#   R3 contracts-purity    : AgentFolio.Contracts must not reference Platform/Agents/Slices
#   R4 ef-encapsulation    : Slices/ or AgentFolio.Agents must not reference EntityFrameworkCore
#                            or Microsoft.Data.Sqlite (EF stays inside AgentFolio.Platform)
#
# NOTE on counting: GraphFusion's count(a) aggregation with a WHERE predicate on the
# target variable (b.*) is unreliable (returns bogus counts, cf. GF#1559 family).
# The gate therefore returns matching ROWS and counts them in bash — never aggregates.
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

VIOLATIONS=0

# rows "<cypher>" — print matching rows as "src -> dst" lines (JSON via --format json)
rows() {
  atlas graphfusion-query "$STORE" "$1" --format json 2>/dev/null \
    | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
if isinstance(d, list):
    for r in d:
        vals = list(r.values())
        print(" -> ".join(str(v) for v in vals))
'
}

# rule <name> <cypher-returning-rows>
rule() {
  local name="$1" out n
  out=$(rows "$2")
  n=$(printf '%s' "$out" | grep -c . || true)
  if [ "$n" -gt 0 ]; then
    echo "❌ $name: $n violation(s)"
    printf '%s\n' "$out" | head -20 | sed 's/^/    /'
    VIOLATIONS=$((VIOLATIONS+1))
  else
    echo "✅ $name"
  fi
}

# ---------- R1: slice isolation, ALL pairs A≠B ----------
# Iterate every slice directory pair so file_path A≠B is exact (no fragile Cypher
# string splitting inside GraphFusion). Each query is pair-scoped and cheap.
SLICES_DIR="$REPO_ROOT/src/Slices"
if [ -d "$SLICES_DIR" ]; then
  R1_OUT=""
  R1_N=0
  for A in "$SLICES_DIR"/*/; do
    for B in "$SLICES_DIR"/*/; do
      [ "$A" = "$B" ] && continue
      sa="src/Slices/$(basename "$A")"
      sb="src/Slices/$(basename "$B")"
      hits=$(rows "MATCH (a)-[:REFERENCES]->(b) WHERE a.file_path CONTAINS '$sa/' AND b.file_path CONTAINS '$sb/' RETURN a.file_path, b.qualified_name")
      if [ -n "$hits" ]; then
        R1_OUT+="$hits"$'\n'
        R1_N=$((R1_N + $(printf '%s' "$hits" | grep -c . || true)))
      fi
    done
  done
  if [ "$R1_N" -gt 0 ]; then
    echo "❌ slice-isolation: $R1_N violation(s)"
    printf '%s' "$R1_OUT" | head -20 | sed 's/^/    /'
    VIOLATIONS=$((VIOLATIONS+1))
  else
    echo "✅ slice-isolation (all slice pairs)"
  fi
else
  echo "✅ slice-isolation (no src/Slices directory)"
fi

# ---------- R2: Agents must not reference Platform internals ----------
rule "agents-no-platform" \
  "MATCH (a)-[:REFERENCES]->(b) WHERE a.file_path CONTAINS 'AgentFolio.Agents' AND b.file_path CONTAINS 'AgentFolio.Platform' RETURN a.file_path, b.qualified_name"

# ---------- R3: Contracts must not reference anything inward (pure kernel) ----------
rule "contracts-purity" \
  "MATCH (a)-[:REFERENCES]->(b) WHERE a.file_path CONTAINS 'AgentFolio.Contracts' AND (b.file_path CONTAINS 'AgentFolio.Platform' OR b.file_path CONTAINS 'AgentFolio.Agents' OR b.file_path CONTAINS 'Slices/') RETURN a.file_path, b.qualified_name"

# ---------- R4: EF encapsulation — EF/Sqlite only inside AgentFolio.Platform ----------
rule "ef-encapsulation" \
  "MATCH (a)-[:REFERENCES]->(b) WHERE (a.file_path CONTAINS 'Slices/' OR a.file_path CONTAINS 'AgentFolio.Agents') AND (b.qualified_name CONTAINS 'EntityFrameworkCore' OR b.file_path CONTAINS 'EntityFrameworkCore' OR b.qualified_name CONTAINS 'Microsoft.Data.Sqlite' OR b.file_path CONTAINS 'Microsoft.Data.Sqlite') RETURN a.file_path, b.qualified_name"

if [ "$VIOLATIONS" -gt 0 ]; then
  echo "arch-gate: $VIOLATIONS rule(s) violated"
  exit 1
fi
echo "arch-gate: all clean-architecture rules pass"
exit 0
