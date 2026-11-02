# Orchestral / Orchestral Swarm — Why It Doesn't Feel Like an Agent, and Where the Gaps Are

> Review performed against the working tree at `/home/admin/Swarm` on 2026-11-02.
> Nothing was modified except adding this report.

## 0. Executive summary

This project is **not an agent runtime with a security use case**. It is a **scripted security-scanner pipeline with an agent-themed dashboard**.

The "swarm" is a hard-coded three-step sequence (`recon → dast → triage`) that runs the same deterministic tool plan in each "agent". There is no planning, no dynamic delegation, no shared working memory, no conversation/session, no streaming reasoning, no steering, no context management, and no real verification loop. The LLM path exists but is a thin, single-provider `chat/completions` loop that is bypassed entirely when no key is set, and the default path silently blocks on HITL or returns fabricated/empty results.

Most of the named subsystems in the README are either a single class, a SQLite table, or a label in the UI. Several advertised commands and desktop actions are wired to endpoints or constructors that do not exist. The integration test suite passes because it only tests the parts that happen to work.

The result is the "uncanny valley" feeling: it has the vocabulary, dashboards, badges, and branding of an agent platform, but the behavior of a demo script.

### What actually works

To be fair, several pieces are real:

- The Rust core starts, uses SQLite WAL, serves `/health`, and persists agents/findings/HITL/blackboard rows.
- API-key authentication and the basic scope checks work for normal API keys.
- SSE and WebSocket telemetry are wired and emit agent/finding/HITL events.
- `KaliSandboxManager` really does run `nmap`/`curl` inside the Podman image when the image is present (verified: `uname -s` → `Linux`, `nmap` output).
- `ScopeGuard` blocks literal out-of-scope IPs and simple argument smuggling.
- The TUI snapshot renders a usable dashboard.
- The five recipes validate with `goose recipe validate`.
- `tests/test_terminal_mcp.py` passes 6/6 in this environment.

The problem is that these working pieces are not connected into an agent loop, and the broken/faked pieces are the ones a user interacts with first.

---

## 1. Why it doesn't feel like other agents

### 1.1 There is no real agent loop in the default path

- `AgentRunner.run()` checks `has_real_llm = bool(self.llm_api_key)` (`agent_runner.py:166`). With no key (the normal case), it runs a **hard-coded per-role tool list** (`agent_runner.py:242-288`), not a reasoning loop.
- The "reasoning loop" is a single `while` over `max_iterations` with no plan, no reflection, no scratchpad, no observation summarization, no stop condition other than "model returned no tool calls" (`agent_runner.py:182-240`).
- There is no conversation/session store. Each `AgentRunner` is constructed fresh with only `target` and `scope` (`coordinator.py:58-65`); nothing from prior turns or prior agents is passed in.
- There is no streaming, no token-by-token output, no cancellation, no retry/backoff, and no provider abstraction (`agent_runner.py:104-150`).

**Other agents feel like agents because you can watch them think, interrupt them, and continue a session. This one cannot do any of those things.**

### 1.2 The "swarm" is a fixed pipeline, not a swarm

- `CoordinatedSwarmCoordinator.run_engagement()` unconditionally launches `agent-recon-01`, then `agent-dast-01`, then `agent-triage-01`, in that order (`coordinator.py:56-135`).
- The "coordinator" does not decompose the objective, choose agents, assign tasks, or react to results. It is a function that calls three other functions.
- `Blackboard.assign_task()` exists (`blackboard.py:195-203`) but is **never called**. There is no task queue, no claiming, no dynamic scheduling.
- In coordinated mode there is no parallelism at all. The only parallel path is `IndependentWorkerPool`, which is a separate mode and is fake (see 1.4).
- The `ArchonSupervisor` can run threads (`supervisor.py`), but the coordinated path never uses it.

### 1.3 Agents do not share context

- Recon results are written to the blackboard, but the DAST agent is constructed with only `target` and `scope` (`coordinator.py:91-98`). It never reads recon services/endpoints.
- The DAST agent does not receive the blackboard or prior observations. `AgentRunner.__init__` has no blackboard parameter (`agent_runner.py:24-42`).
- Triage reads candidates from the blackboard, but it confirms **every** candidate with a hard-coded CVSS of `5.5` (`coordinator.py:124-133`). It does not inspect evidence, deduplicate, verify, or score.
- The recipes say "retrieve discovered services from SynapseNexus using `nexus_query_state`" (`recipes/cerberus_dast/recipe.yaml`), but the runtime never gives the agent a tool to do that in the coordinated path.

**This is the biggest behavioral tell: three "specialists" that never talk to each other.**

### 1.4 Findings are fabricated or dropped

- `IndependentWorkerPool._execute_worker_task()` does not run any tool. It unconditionally submits a fake finding: "Hardcoded Secret Detected", `CRITICAL`, `9.1`, evidence `AKIAIOSFODNN7EXAMPLE` (`independent.py:36-48`). This is demo data presented as a real scan.
- In the deterministic `AgentRunner` path, findings are only extracted if `res.get("status") == "discovered"` (`agent_runner.py:271`). None of the native tools return that shape:
  - `NativeSecretScannerTool` returns `{"findings_count", "findings"}` (`secret_scanner.py:65-70`).
  - `NativePortScannerTool` returns `{"open_ports", ...}` (`port_scanner.py:52-58`).
  - `NativeHttpProbeTool` returns `{"missing_headers", "exposed_files"}` (`http_probe.py:83-89`).
  So the deterministic path discovers **zero** findings from native tools.
- Recon's permitted tools are `["kali_terminal", "native_secret_scanner"]` (`registry.py:61-62`), so the `native_port_scanner` and `native_http_probe` branches in `agent_runner.py:254-258` are unreachable. The coordinator then looks for `res.get("tool") == "native_port_scanner"` (`coordinator.py:71`) and never sees it.
- The secret scanner is called with `target="."` and no `directory_path` (`agent_runner.py:259-260`). Its `_run()` only scans when `content` or `directory_path` is provided (`secret_scanner.py:47-63`), so it returns `findings_count: 0`. The MCP `secret_scan` tool has the same problem (`mcp/server.py:118-121`).

### 1.5 Passive tools are misclassified as active and block for 120s

- `BaseSecurityTool.execute()` builds a `command_repr` that includes the tool name (`base.py:36`).
- `HITLInterceptor.evaluate_risk()` treats the substrings `"scan"` and `"probe"` as active (`hitl.py:59-61`).
- Therefore `native_secret_scanner` and `native_http_probe` are classified `ACTIVE`, even though they are local/passive. With `auto_approve_tier=PASSIVE` (`agent_runner.py:67-70`), they are **not** auto-approved and the agent enters a blocking poll loop (`hitl.py:85-116`).
- Reproduced: `orchestral agent --role athena_sast --target .` prints `[HITL GATE] ... native_secret_scanner ... Waiting for operator approval` and hangs.
- There are two different risk classifiers: `HITLInterceptor.evaluate_risk()` says `nmap` is `ACTIVE`; `KaliSandboxManager.classify_risk_tier()` says `nmap` is `PASSIVE` (`kali.py:132-138`). The test suite tests the second one (`test_terminal_mcp.py:83-84`) and never sees the first.

**From a user's perspective, the agent "freezes" on its first passive step.**

### 1.6 The LLM path is brittle and partly wrong

- Model selection has a Python precedence bug (`agent_runner.py:57-63`):

  ```python
  self.llm_model = (
      llm_model
      or os.environ.get("ORCHESTRAL_LLM_MODEL")
      or "gemini-2.5-flash"
      if "google" in self.llm_base_url or "gemini" in self.llm_base_url
      else "gpt-4o-mini"
  )
  ```

  This parses as `(llm_model or env or "gemini-2.5-flash") if google else "gpt-4o-mini"`. With the default OpenAI base URL, an explicitly passed `llm_model` or `ORCHESTRAL_LLM_MODEL` is **ignored** and the model is always `gpt-4o-mini`. Reproduced with `llm_model="my-explicit-model"` → `gpt-4o-mini`.
- `GEMINI_API_KEY` is accepted as a bearer token for an OpenAI-compatible endpoint (`agent_runner.py:45-51`), but the base URL defaults to `api.openai.com`. Gemini's OpenAI-compatible endpoint is different.
- Cost is hard-coded at `$0.002 / 1K tokens` regardless of model (`agent_runner.py:145-146`), and token usage defaults to `250` when the provider omits it.
- On any HTTP error, `_call_llm` prints and returns `{}` (`agent_runner.py:148-150`); the loop breaks and the run is still marked `completed` (`agent_runner.py:289`). Failures look like success.
- The tool schema is generic for every tool: only `target` and `command` (`agent_runner.py:111-130`). Tools that need `directory_path` (`secret_scanner.py:47`) or `language` (`codeql.py:90`) cannot be driven correctly by the model.

### 1.7 No agent UX primitives

Missing compared to OpenCode/Goose/Claude Code-style agents:

- No chat transcript or message history UI.
- No session persistence/resume; no checkpoints; no undo.
- No streaming reasoning or tool-call timeline.
- No interrupt/steer/queue-while-running.
- No inline permission prompt; HITL is a separate blocking poll and a table row.
- No model/provider picker, no context-window management, no compaction.
- No plan/todo list, no subagent delegation, no skills, no repo indexing, no file references.
- No MCP client in the agent runtime (the MCP server exists, but the agent does not call it).
- No report generation; the coordinator returns a JSON dict, and the CLI tries to render it as Markdown (`bin/orchestral:74-78`).

The desktop app is a monitoring dashboard with tables, not an agent console. There is no place to talk to an agent, inspect its reasoning, or continue its work.

### 1.8 The "Goose reasoning runtime" is decorative

- `goose-upstream/` is a partial clone with **1,569 deleted files** (documentation, UI, `workflow_recipes`, `buzz`, etc.) and one modified skill doc renamed to "Orchestral" (`git -C goose-upstream status`).
- The only functional integration is a Goose MCP extension entry in `~/.config/goose/config.yaml` that launches `python3 -m orchestral_worker.mcp.server`.
- The Orchestral agent runtime never uses Goose: `AgentRunner` uses raw `urllib` to call an OpenAI-compatible endpoint. The `bin/goose` binary is only used by `test_06` to validate recipes.
- Recipes are valid Goose YAML, but they are not executed by Goose or by the Orchestral runtime. `profiles.py` only reads the `instructions` string.

So there are two separate systems: a Goose install with a partially working MCP server, and a Python "swarm" that ignores Goose.

---

## 2. Broken commands and dead paths (reproduced)

| Command | Result |
|---|---|
| `./bin/orchestral run --target 127.0.0.1 --allow 127.0.0.1/32` | `TypeError: CoordinatedSwarmCoordinator.__init__() got an unexpected keyword argument 'core_url'` |
| `./bin/orchestral mcp` | `ImportError: cannot import name 'main' from 'orchestral_worker.mcp.server'` |
| MCP `tools/call nexus_query_state` | `Execution error: 'OrchestralClient' object has no attribute 'get_blackboard_services'` |
| MCP `tools/call nexus_record_service` | `Execution error: 'OrchestralClient' object has no attribute 'post_blackboard_service'` |
| `./bin/orchestral agent --role athena_sast --target .` | Hangs on a HITL approval for the passive secret scanner |
| `./bin/orchestral archon --target 127.0.0.1` | No output within 40s; agents block on HITL and `wait_all()` has no monitor loop |
| `./bin/orchestral lens --snapshot` | Works (the TUI renders) |
| `python3 tests/test_terminal_mcp.py` | 6/6 pass, but none of the above paths are tested |

### CLI wiring bugs

- `cmd_run` passes `core_url`, `api_key`, `max_concurrency`, and `run_batch`, none of which exist on `CoordinatedSwarmCoordinator` / `IndependentWorkerPool` (`bin/orchestral:67-89`).
- `cmd_run` calls `coordinator.run_engagement(args.target)` without `research_objective` (`bin/orchestral:74`), which is required (`coordinator.py:35`).
- `cmd_run` renders the returned dict with `rich.markdown.Markdown` (`bin/orchestral:78`), which expects a string.
- `cmd_mcp` imports a non-existent `main` (`bin/orchestral:346`, `mcp/server.py:235`).
- Desktop dispatch uses `--role recon --target 127.0.0.1 --scope 127.0.0.1` (`MainWindow.axaml.cs:167`), but the CLI flag is `--allow`, not `--scope` (`bin/orchestral:455`).

### Desktop wiring bugs

- `CitadelService.SendHitlDecisionAsync` posts to `/api/v1/hitl/decision` (`CitadelService.cs:85`), which does not exist. The core exposes `/api/v1/hitl/{id}/approve` and `/deny` (`api.rs:104-105`).
- `CitadelService.GetBlackboardSnapshotAsync` GETs `/api/v1/blackboard/snapshot` (`CitadelService.cs:98`), which does not exist. The core exposes `/api/v1/blackboard/summary` (`api.rs:111`).
- Desktop models do not match the API:
  - `FindingItem` expects `discovered_by`, `cvss_score`, `verified` (`SwarmModels.cs:24-26`); the API returns `agent_id`, `cvss`, `status` (`models.rs:56-68`).
  - `HitlItem` expects `request_id`, `action_type`, `command_summary`, `reasoning` (`SwarmModels.cs:31-37`); the API returns `id`, `tool_name`, `command_preview` (`models.rs:87-96`).
  - `BlackboardData` expects `services_count` etc. (`SwarmModels.cs:42-45`); the summary returns `total_services`, `total_endpoints`, `candidates`, `confirmed_vulnerabilities` (`api.rs:457-466`).
- The HITL approve/deny buttons bind `Tag="{Binding RequestId}"` (`MainWindow.axaml:439,446`), which is null because the JSON field is `id`. Clicks send a null request ID.
- `OnDispatchMissionClicked` ignores the user's instruction entirely and always launches the same recon command (`MainWindow.axaml.cs:151-167`).
- The hard-coded `/home/admin/Swarm/bin/orchestral` path (`MainWindow.axaml.cs:166,216`) breaks any non-dev install.

---

## 3. Security gaps (critical)

### 3.1 Agent tokens are not scoped at all

`validate_key` accepts any valid JWT and grants `agent`, `swarm:read`, and `swarm:write` regardless of the token's `target_scope` or `allowed_tools` (`state.rs:62-84`). The claims are never checked by any handler.

Reproduced against a temporary core:

```text
POST /api/v1/auth/agent-token  target_scope=127.0.0.1/32, allowed_tools=["kali_terminal"]
POST /api/v1/findings          target=evil.example.com, status=confirmed
→ HTTP 201, finding persisted
```

A token scoped to `127.0.0.1/32` can write a confirmed `CRITICAL` finding for `evil.example.com`. It can also read all findings, all agents, and the whole blackboard.

The `agent_tokens` table exists (`db.rs:129-137`) but is never written or read; `issue_agent_token` only signs a JWT (`state.rs:108-134`). There is no revocation, no `jti` tracking, and no target/tool enforcement.

### 3.2 The JWT signing secret has a hard-coded default

`state.rs:26-28` falls back to `"orchestral_ephemeral_jwt_secret_dev_32b_bytes!!"` if `ORCHESTRAL_JWT_SECRET` is unset. Anyone who knows the default can forge an agent token for any agent ID and target.

### 3.3 The admin API key is hard-coded in five places

- `bin/orchestral:423` (global `--api-key` default)
- `orchestral-worker/orchestral_worker/mcp/server.py:25`
- `orchestral-worker/orchestral_worker/tui.py:319`
- `orchestral-desktop/Services/CitadelService.cs:14`
- `tests/test_terminal_mcp.py:35`

`cmd_core` always sets `ORCHESTRAL_ADMIN_KEY` from `args.api_key` (`bin/orchestral:31-33`), and the default is the known test key. So a default `orchestral bastion` starts with a publicly known admin key. The key is also in `~/.config/goose/config.yaml` (redacted here).

### 3.4 API key hashes are exposed

`GET /api/v1/auth/keys` returns `Vec<ApiKeyRecord>` (`api.rs:162-169`), and `ApiKeyRecord` serializes `key_hash` (`models.rs:99-108`). An admin can retrieve SHA-256 hashes of every key. This is unnecessary and enables offline cracking.

### 3.5 The "immutable cryptographic ledger" is a plain table

`engagement_audit_log` is a normal SQLite table (`db.rs:139-147`) with `INSERT` (`db.rs:708-716`) and no hash chain, no signature, no triggers, no append-only enforcement. It is mutable by anyone with DB access. The README claim at `README.md:32` is false.

### 3.6 Scope enforcement is bypassable

- `ScopeGuard.validate_target()` only inspects the literal string (`scope.py:75-130`). It does **not** resolve DNS. A domain that resolves to `169.254.169.254` (cloud metadata) or an internal IP passes if the domain is allowlisted; a DNS rebinding attack also passes.
- `KaliSandboxManager._extract_and_validate_all_hosts()` tokenizes on whitespace and validates bare IPs/domains/URLs (`kali.py:140-178`). It misses:
  - `--target=host` style arguments,
  - targets generated by shell substitution (`curl http://$(printf 9.9.9.9)`),
  - targets inside `-iL` files,
  - `Host:` header SSRF,
  - IPv6, IDN, and trailing-dot variants.
- The command is executed with `bash -c` inside the container (`kali.py:238`), so shell expansion is available to the agent.
- `network_mode` is stored (`kali.py:50,58`) but never used in `run_cmd` (`kali.py:98-112`), so the sandbox cannot be switched to `none`/isolated networking.
- Local paths are always allowed (`scope.py:80-82`), and the secret scanner can be pointed at any host path via `directory_path` (if it were wired up).

### 3.7 The sandbox silently degrades to a fake success

If the container runtime or image is unavailable, `start_sandbox()` returns `container_unavailable` (`kali.py:124-130`), but `KaliTerminalTool._get_or_create_sandbox()` ignores the result (`kali_tool.py:30`) and `execute()` falls back to:

```python
"exit_code": 0,
"stdout": f"[AGENT SANDBOX] Executed '{command}' in isolated agent space for {target}",
"sandbox": "isolated_process_context",
```

(`kali.py:260-274`). The agent sees a successful execution even though nothing ran. For a security tool, this is worse than failing closed.

### 3.8 Container hardening is weaker than advertised

`run_cmd` uses `--cap-drop ALL` but then adds `NET_BIND_SERVICE` and `NET_RAW` (`kali.py:104-107`), does not use `--read-only`, `--user`, `--ipc=none`, `--ulimit`, seccomp/AppArmor profiles, or `--security-opt no-new-privileges` beyond the one flag. It does not enforce rootless Podman; it just prefers it if present (`kali.py:63-69`). The image is a floating tag (`localhost/orchestral-tartarus:latest`) with no digest pinning.

### 3.9 HITL can be bypassed or is unusable

- `orchestral-worker/main.py:73` constructs `HITLInterceptor` without a `client`, so active actions cannot be registered in the core and fall back to stdin or fail closed. There is no way for an operator to approve them from the TUI.
- Any agent token with `swarm:write` can create a HITL request (`api.rs:321-341`), but only an admin key can approve/deny (`api.rs:343-367`). The agent token itself cannot resolve its own request.
- The agent can submit a finding with `status: "confirmed"` directly (`api.rs:273-296`), bypassing triage and HITL entirely. Reproduced in 3.1.
- The risk classifier is keyword-based and inconsistent (1.5). `"write"` in a command triggers `HIGH_IMPACT` (`hitl.py:50-56`), so `curl -w` or `grep write` can block.

### 3.10 Core/API weaknesses

- CORS is `Any` (`main.rs:86-89`), no TLS, no rate limiting, no brute-force protection on API keys.
- SSE/WS auth is via `?api_key=` query parameter (`api.rs:86-88,513-520`), which leaks keys into logs, proxies, and browser history. No `Last-Event-ID` replay; events are lost on reconnect.
- `state.add_*` methods ignore DB errors (`let _ = ...`, e.g. `state.rs:57,148-166,207-213`), so the API can return `201 Created` even when persistence failed.
- DB read paths silently drop malformed rows (`rows.flatten()`, `if let Ok(...)` throughout `db.rs`), hiding corruption.
- No schema migrations; only `CREATE TABLE IF NOT EXISTS` (`db.rs:40-148`).
- `get_topology` fabricates a coordinator node and edges to every agent (`api.rs:218-260`); it is not derived from real topology.
- `ORCHESTRAL_ADMIN_KEY=""` would register an empty admin key because `Some("")` is accepted (`main.rs:38-55`).

---

## 4. Agent-runtime gaps

- **No planning/decomposition.** The coordinator's "decomposition" is three hard-coded calls.
- **No task queue or claiming.** `assign_task` is dead code (`blackboard.py:195-203`).
- **No memory.** No conversation history, no observations store, no repo index, no RAG, no context compaction.
- **No reflection/self-correction.** A tool error is printed and ignored (`agent_runner.py:286-287`); an LLM error breaks the loop and marks success.
- **No deduplication or false-positive handling.** Triage confirms every candidate with CVSS 5.5 (`coordinator.py:126-133`).
- **No CVSS v3.1 calculation.** The score is a literal.
- **No CVE lookup.**
- **No report generation.** The coordinator returns JSON; the CLI expects Markdown.
- **No prompt-injection defense.** Tool output is appended as `role: "tool"` (`agent_runner.py:236-240`) with no delimiting or instruction hierarchy. Scanning hostile pages is exactly where indirect prompt injection is likely.
- **No token/cost enforcement in the loop.** `AgentSupervisor.token_budget_per_agent` is checked only in `monitor_and_enforce_limits()` (`supervisor.py:127-146`), which is never called. The runner never checks `tokens_used` against a budget.
- **No cancellation.** `kill_agent` calls `future.cancel()` (`supervisor.py:104-125`), which cannot stop a running Python thread. `ManagedAgent.is_alive` is just a flag; the runner never checks it.
- **No graceful shutdown/cleanup.** Sandboxes are not stopped by the supervisor or CLI; only the test calls `stop_sandbox()` (`test_terminal_mcp.py:62`).
- **Thread safety.** A shared `ToolRegistry` is passed to concurrent agents (`supervisor.py:52,77-85`); `KaliTerminalTool._sandboxes` is a plain dict (`kali_tool.py:21-32`) with no lock.

---

## 5. Data / architecture gaps

- **Blackboard cross-engagement contamination.** Triage calls `blackboard.get_candidates()` with no target filter (`coordinator.py:124`; `blackboard.py:184-193`), so it can confirm candidates from previous engagements. `list_blackboard_candidates` has no filter (`db.rs:631-654`).
- **No finding dedup.** Every run inserts new rows; `findings` has no unique constraint on `(target, title, agent_id)`.
- **No confidence/evidence model.** Findings carry a free-text `evidence` and a `status`, but no verification record, no reviewer, no chain of custody.
- **No relations.** Candidates, confirmed findings, services, endpoints, and findings are separate tables with no foreign keys or joins.
- **No pagination/filtering.** `list_findings` returns everything; `list_audit_logs` has an unbounded `limit` (`api.rs:476`).
- **Single SQLite connection behind a `Mutex`** (`db.rs:14-16`); every request serializes on one lock. Fine for a demo, not for a "distributed multi-process intelligence fabric".
- **No event sourcing.** SSE events are ephemeral; no replay, no durable event log.
- **No migration/versioning.** Schema changes require manual DB edits.

---

## 6. Packaging, repo, and test gaps

- **Root is not a git repo.** `/home/admin/Swarm` has no `.git`; `orchestral-core/.git` exists but has **zero commits** (`git -C orchestral-core log` → "does not have any commits yet"). There is no history, no branching, no review workflow.
- **No root `.gitignore`.** `orchestral-core/target` is 1.7 GB, `goose-upstream` is 2.0 GB, `orchestral-desktop/bin` is 318 MB, `bin/` is 1.1 GB. The working tree is dominated by build artifacts and a vendored 926 MB `goose` binary.
- **`goose-upstream` is a damaged clone.** 1,569 deleted files, one modified skill doc. It is not a usable upstream checkout and it is not documented as trimmed.
- **`orchestral-worker/pyproject.toml`**:
  - `readme = "README.md"` but `orchestral-worker/README.md` does not exist.
  - Declares `requests`, `websockets`, `pydantic`, `aiohttp` — none are used by the code.
  - Imports `yaml` (`profiles.py:2`) but does not declare `PyYAML`.
  - No `[project.scripts]` entry points.
  - No package data for `recipes/` or `sandbox/Containerfile`.
  - `profiles.py` computes `RECIPES_DIR` relative to the source tree (`profiles.py:6-7`); an installed package will not find the recipes.
  - No `__init__.py` in `orchestral_worker/`, `mcp/`, `sandbox/`, `tools/`; relies on PEP 420 namespace packages. It works from the source tree but is fragile for packaging.
  - `orchestral-worker.egg-info/` is checked in and stale.
- **`orchestral-core`** has no README, no tests, no CI, no migrations, and no `ORCHESTRAL_JWT_SECRET` documentation.
- **Tests are shallow and environment-coupled.** `tests/test_terminal_mcp.py` has 6 tests; it requires a live core on `127.0.0.1:8088` and the `orchestral-tartarus` image. It does not test:
  - `orchestral run` / `archon` / `mcp` CLI,
  - the `nexus_*` MCP tools,
  - `AgentRunner` / coordinator / supervisor,
  - the desktop endpoints/models,
  - token scope enforcement,
  - the LLM path.
  It also leaks the MCP subprocess on failure (`ResourceWarning` in the run) and invokes the 926 MB `goose` binary five times.
- **No linter/formatter/type-check config** for Python or C#; no Rust CI.
- **README overclaims.** `README.md:23-33` describes "distributed multi-process intelligence fabric", "zero-trust synchronous blocking gateway", "immutable append-only cryptographic ledger", "hardened capability-dropped container", "sub-ms SSE streaming", etc. Most are partially implemented at best.

---

## 7. Smaller correctness bugs

- `main.py` / `bin/orchestral` scope parser drops bare IPs:

  ```python
  scope_domains = [s for s in allow.split(",") if not ("/" in s or s.replace(".", "").isdigit())]
  scope_cidrs = [s for s in allow.split(",") if "/" in s]
  ```

  `--allow 127.0.0.1` yields empty domains and empty CIDRs; `ScopeGuard` then rejects `127.0.0.1` with "No CIDR ranges configured". (`main.py:64-65`, `bin/orchestral:57-58`; reproduced.)
- `ScopeGuard` private-IP auto-detection uses substring checks (`"10." in c`), so `210.0.0.0/8` incorrectly enables private IPs (`scope.py:42-45`; reproduced).
- `tui.py` uses `Optional` at `tui.py:80` but imports only `Dict, Any, List` at `tui.py:9`. On Python 3.10–3.13 (the declared minimum is `>=3.10`), this raises `NameError` at import because annotations are evaluated eagerly. It only works on Python 3.14+ (PEP 649).
- The TUI footer advertises `[Q] [C] [I] [A] [D] [SPACE]` (`tui.py:258-277`), but `run_live()` never reads input (`tui.py:305-313`). There is no key handling.
- The TUI's SSE cache is largely unused: `render_snapshot()` polls the REST API every refresh (`tui.py:279-301`), and `_handle_live_event` ignores `blackboard.updated` and `system.log` (`tui.py:80-94`).
- `NativeSecretScannerTool`'s "AWS Secret Key" pattern is any 40-character alphanumeric string (`secret_scanner.py:16`). With `directory_path="."` it produced hundreds of false positives across `goose-upstream`, `target/`, `obj/`, and `.json` files (reproduced).
- `CodeQLTool` uses `subprocess.run` without a timeout (`codeql.py:131,149`), writes to `/tmp/codeql_db_` for `target="."` (empty name), and assumes `{language}-{query_suite}.qls` is resolvable (`codeql.py:143`).
- MCP `codeql_analyze` passes `language=None` when the argument is omitted (`mcp/server.py:125-126`), overriding the default and causing `None.lower()` (`codeql.py:111`).
- MCP tools can write to stdout, corrupting the JSON-RPC stream. `HITLInterceptor` prints to stdout (`hitl.py:80-83,106,115,118,133`), as does `CodeQLTool` (`codeql.py:83`). The MCP server writes JSON to the same stdout (`server.py:231`).
- MCP protocol coverage is minimal: no `ping`, `resources/*`, `prompts/*`, `logging/setLevel`, `notifications/cancelled`; unknown notifications get an error response with `id: null` (`server.py:219-229`).
- `IndependentWorkerPool.run_batch_tasks` uses `max_workers=min(len(targets), 5)` (`independent.py:68`); an empty target list gives `max_workers=0` and `ValueError`.
- `AgentRunner._update_status` always reports `"mode": "coordinated"` (`agent_runner.py:94`), even in independent/supervised modes.
- Coordinator telemetry is hard-coded: coordinator `tokens_used: 600`, `cost_usd: 0.003` (`coordinator.py:51-52`), then `+1000` / `+0.005` at the end (`coordinator.py:144-145`).
- `NativeHttpProbeTool` defaults to `https://` when no scheme is given (`http_probe.py:35`), so probing a plain HTTP host on port 8088 over TLS fails.
- `NativePortScannerTool` grabs a "banner" for port 443 over plain TCP (`port_scanner.py:26-30`), which is not a TLS handshake.
- `AgentRunner` uses `urllib` with a 30s timeout and no retry (`agent_runner.py:140-143`); a transient 429 aborts the run.
- `orchestral-core` `main.rs` slices the admin key with `&admin_key[..min(14,len)]` (`main.rs:46`), which panics on a non-ASCII key.
- `orchestral-core` `ApiKeyRecord` serialization exposes `key_hash` (3.4).
- `orchestral-core` `AuthError::InsufficientPermissions` is never constructed (`auth.rs:30-34`); missing scopes return `InvalidKey` (401) instead of 403.

---

## 8. Recommended fix order

If the goal is to make this feel like a real agent platform, fix in this order:

1. **Stop the bleeding (security):**
   - Remove hard-coded admin key/JWT secret from all code and config; require env/secret store.
   - Enforce `target_scope` and `allowed_tools` on every write/read; persist `jti` in `agent_tokens`; add revocation.
   - Reject `status: "confirmed"` from agent tokens; only triage/operator can confirm.
   - Remove `key_hash` from API responses.
   - Fix the scope parser and add DNS-resolution/IP-pinning checks.
2. **Make the happy path run:**
   - Fix `cmd_run`, `cmd_mcp`, `IndependentWorkerPool` constructor/method names, `Markdown(dict)`, and desktop endpoints/models.
   - Make `secret_scan` pass `directory_path`, and make the MCP `nexus_*` tools call existing client methods.
   - Make passive tools auto-approve; unify the two risk classifiers.
3. **Make it agentic:**
   - Add a real `Session`/`Conversation` store and a streaming loop.
   - Give the coordinator a planner that emits tasks; give agents the blackboard and prior observations.
   - Add task claiming, parallel execution, cancellation, and token budgets enforced in the loop.
   - Replace the hard-coded triage with evidence-based verification and CVSS calculation.
4. **Make it observable:**
   - Persist tool calls/reasoning events, not just findings.
   - Add a chat/timeline UI, interrupt/steer, and HITL inline approval.
   - Replace polling with real SSE consumption and replay.
5. **Clean the repo:**
   - Root git init, `.gitignore`, remove build artifacts and the 926 MB binary from the tree (use a release download or LFS).
   - Repair or remove `goose-upstream`; document whether Goose is actually part of the architecture.
   - Add packaging metadata, PyYAML, entry points, package data, migrations, and CI.
6. **Test the real paths:**
   - Add tests for `orchestral run`, `mcp` CLI, `nexus_*` MCP tools, token scope, desktop endpoints, coordinator/triage, and the LLM loop with a mock server.

---

## 9. One-paragraph diagnosis

The project has the **surface area** of an agent platform — named subsystems, personas, recipes, a TUI, a desktop app, a Rust control plane, MCP, HITL, a sandbox — but the **behavioral core** is missing. The agents are functions with role strings; the swarm is a fixed sequence; the blackboard is a write-only log; the findings are fabricated or dropped; the LLM path is optional and brittle; the "scoped" tokens are not scoped; and several advertised commands and UI actions call things that do not exist. That is why it doesn't feel like other agents: other agents have a loop, memory, tools that actually run, visible reasoning, and a way for the human to steer. This project has the vocabulary but not the loop.
