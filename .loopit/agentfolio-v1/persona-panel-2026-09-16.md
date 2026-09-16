
## CTO-cofounder (scales hiring)

1. FIRST IMPRESSION
Right wedge — cited answers over personal data is exactly what clients ask for. But as shipped it's a chat box that answers "nothing found": empty tenant plus no server key means my two minutes prove nothing. An empty showcase is worse than none.

2. WHAT CONVINCES ME
- SQLite vec/FTS hybrid retrieval: pragmatic, zero-infra; no vector-DB cargo cult
- Browser-local AI tier = a real privacy story for confidential RFPs
- Doc upload inside the demo makes me sell it to myself
- SSE streaming + citations shows product sense, not just API plumbing

3. WHAT DOUBTS OR GAPS I HAVE
- Both headline tiers are dark; chips promise, page can't deliver
- Citations are bare links — can't verify a claim at a glance; no snippets = trust theater
- Zero eval evidence. Retrieval demos die on wrong-citation rate and I have no number
- Where does my uploaded doc actually go? Tenancy/auth unaddressed
- Agent Framework: taste or résumé-driven?

4. WHAT'S MISSING
- Synced real stars/posts before I open it
- One configured server model so the badge is honest
- Preview snippets on citations
- One-line eval note ("N questions, X% citation-exact")

5. ONE THING TO FIX FIRST
Sync real data and plug in a server key. Then it's a shortlist candidate; today I'd close the tab after the first "nothing found."


## Engineering Manager (agency side)

HTTP 400: no keys found for provider: mac-local and model: glm-5.3-flash


## Procurement lead (non-technical)

(TIMED OUT)


## Skeptical senior architect (peer review)

HTTP 400: no keys found for provider: mac-local and model: glm-5.3-flash


## Startup founder (budget, speed)

1. FIRST IMPRESSION
Clean, fast, professional — the streaming answers with citations and a paperclip that lets me upload my own RFP is exactly the MVP feature I'm shopping for. I'd stay on the page... for about ninety seconds.

2. WHAT CONVINCES ME
- It's deployed and interactive, not a slide deck — I typed and it streamed.
- Doc upload works without signup; I can test with my own document today.
- Honest capability chips (browser vs server tier) instead of vaporware.

3. WHAT DOUBTS OR GAPS I HAVE
- The two headline features (GitHub stars, LinkedIn saves) return "nothing found" — I'm demoing an empty showcase. That's the product failing in front of me.
- Only the browser-local AI works; the server tier has no API key. So I can't evaluate the thing I'd actually be paying to run.
- Citations are bare links — I can't verify an answer at a glance, and investors will click.
- Nothing shows cost, latency, or what happens when AI is wrong.

4. WHAT'S MISSING
- Synced real data in the showcase tenant.
- A working server-AI path with a live API key.
- One citation with a visible source snippet.
- A 60-second Loom of a real use case.

5. ONE THING TO FIX FIRST
Sync the owner's real stars/posts tonight. An empty flagship demo costs you the client before your code gets judged.


## Engineering Manager (agency side) (round 2)

Unknown provider 'custom:zai'. Check 'hermes model' for available providers, or 
run 'hermes doctor' to diagnose config issues.


## Procurement lead (non-technical) (round 2)

Unknown provider 'custom:zai'. Check 'hermes model' for available providers, or 
run 'hermes doctor' to diagnose config issues.


## Skeptical senior architect (peer review) (round 2)

Unknown provider 'custom:zai'. Check 'hermes model' for available providers, or 
run 'hermes doctor' to diagnose config issues.


## Engineering Manager (agency side) (round 2)

1. FIRST IMPRESSION
Right stack — .NET 10, Agent Framework, SQLite vec/FTS hybrid is literally the pattern I bill clients for. But the demo is empty: a chat box that can't answer anything real is a claim, and I hire against claims.

2. WHAT CONVINCES ME
- Hybrid vec/FTS + SSE streaming: production instincts, not tutorial code
- Tier badge shows cost/latency awareness
- Doc-upload (JD/RFP) Q&A is exactly the client use case
- Openly labels what's broken — rare and good

3. DOUBTS / GAPS
- Zero synced data: "every answer cites its source" is currently unfalsifiable — there's nothing to cite
- Browser-local AI only: is this real server-side orchestration or a WASM demo?
- Bare-link citations aren't citations; snippets are table stakes for RAG work
- No k8s anywhere — that's the job requirement
- No tests, CI badge, or repo link visible
- LinkedIn saves: where's the PII/redaction thinking?

4. MISSING
- His real stars synced, answering real questions
- Repo link: tests, pipeline, Dockerfile/k8s manifests
- One working server-AI session (BYO key acceptable)
- Architecture sketch + cost per query

5. ONE THING TO FIX FIRST
Sync his actual GitHub stars so one real question returns a snippet-cited answer. Until then I'd close the tab — the stack alone buys exactly one revisit.


## Procurement lead (non-technical) (round 2)

HTTP 400: no keys found for provider: mac-local and model: glm-5.3-flash


## Skeptical senior architect (peer review) (round 2)

1. FIRST IMPRESSION
Clean dev-tool aesthetic and rare honest disclosure of gaps — that buys 5 minutes. But the two headline chips (GITHUB STARS, LINKEDIN SAVES) demo nothing: the product's reason to exist is switched off. I'm evaluating a shell.

2. WHAT CONVINCES ME
- Citation-first design is the right hard problem to pick
- SQLite vec + FTS = pragmatic hybrid search, no infra circus
- SSE streaming + working doc-upload Q&A = real plumbing, end-to-end
- Admits limits instead of faking a demo

3. DOUBTS / GAPS
- Chips advertise features the live tenant can't show — that's vaporware dressing
- Server-AI tier (what a client deploys) doesn't run without a key
- "Citations" as bare links: no snippet, no verification — defeats the stated promise
- Where's the repo, test counts, CI? Zero evidence offered
- Microsoft Agent Framework: load-bearing or resume keyword?
- LinkedIn saves sync = API walled garden; how, legally?

4. MISSING BEFORE SHORTLISTING
- Public repo: tests, CI, commit history
- Populated tenant with real synced data
- Snippet-backed citations
- Latency/cost numbers

5. FIX FIRST
Sync real stars/saves into the showcase tenant — or delete those chips.

Verdict: tab stays open only until I check for a repo link. None visible — I'd close it, then search GitHub for you.


## SYNTHESIS (4/5 personas: CTO, Founder, EM, Architect)

CRITICAL — 4/4 unanimous:
1. EMPTY SHOWCASE = the product failing in front of the buyer. "Chips advertise features
   the live tenant can't show — vaporware dressing." Fix: sync REAL stars (needs
   GITHUB_TOKEN) or remove the chips. Every persona: "sync data first."
2. NO REPO LINK / no verifiable evidence (tests, CI, commits). Architect: "tab stays
   open only until I check for a repo link. None visible — I'd close it, then search
   GitHub for you." Fix: prominent footer/header link to the Gitea repo + CI badge.
3. BARE-LINK CITATIONS defeat the product's own promise ("every answer cites" =
   unfalsifiable without snippets). Fix: snippet previews on citations.

MODERATE — 2-3/4:
4. Server-AI tier dark (no NIM key): "can't evaluate the thing I'd pay to run" (Founder+CTO).
   BYO-key acceptable to EM.
5. No k8s evidence visible (EM: "that's the job requirement").
6. No eval/quality numbers ("wrong-citation rate" — CTO), no cost/latency (Architect+EM).
7. LinkedIn PII/redaction story missing (EM+Architect).

WHAT WORKS (consensus — do not touch):
+ Citation-first product framing ("the right hard problem")
+ SQLite vec/FTS ("no vector-DB cargo cult", "pattern I bill clients for")
+ Browser-local AI tier = privacy story for confidential RFPs
+ Doc upload = "I can sell it to myself"
+ Honest disclosure of gaps ("rare and good")
+ Dark dev-tool aesthetic lands

Hardest single quotes:
- "An empty showcase is worse than none." (CTO)
- "I hire against claims." (EM)
- "I'd close it, then search GitHub for you." (Architect)
