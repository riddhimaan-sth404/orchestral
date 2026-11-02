# Orchestral: Autonomous Multi-Agent Security Swarm Platform

```
  ___  ____   ____ _   _ _____ ____ _____ ____     _     _     
 / _ \|  _ \ / ___| | | | ____/ ___|_   _|  _ \   / \   | |    
| | | | |_) | |   | |_| |  _| \___ \ | | | |_) | / _ \  | |    
| |_| |  _ <| |___|  _  | |___ ___) || | |  _ < / ___ \ | |___ 
 \___/|_| \_\\____|_| |_|_____|____/ |_| |_| \_/_/   \_\|_____|
                                                               
  Autonomous Multi-Agent Security Swarm Monitor & Defense Platform
  Citadel: Orchestral Citadel | Runtime: VigilEngine | Observatory: OrchestralLens
```

> **Cross-Platform AI Agent Swarm Monitor & Security Infrastructure**  
> Built for coordinated security research, automated vulnerability triage, and decoupled worker execution with scoped API-key and agent-token authentication, SQLite WAL persistence, SSE streaming, and deterministic guardrails.

---

## Status and honest limitations

- The runtime executes **real tools**. It never fabricates findings. When no LLM key is configured, each role runs a deterministic tool plan and reports only what the tools return.
- The LLM path is optional and uses any OpenAI-compatible `/chat/completions` endpoint. It is not a general autonomous planner; it is a bounded ReAct loop with a per-agent token budget.
- The coordinated pipeline is a fixed `recon → dast → triage` sequence, not free-form delegation. Agents do share a live blackboard, and triage verifies candidates against recorded evidence.
- The sandboxed terminal has its own network namespace. `nmap` inside the container cannot reach services bound to the host's loopback; use the native port scanner for local targets.
- CodeQL analysis requires the CodeQL CLI to be installed and on `PATH`.
- The audit ledger is append-only and hash-chained in SQLite. It is tamper-evident, not a distributed ledger.

---

## 1. Tactical Subsystems Architecture

| Tactical Subsystem | Language / Tech | Description |
|:---|:---|:---|
| **Orchestral Citadel** | Rust (Axum + SQLite WAL) | Control plane: persistent store, scoped keys, ephemeral agent tokens, event broker |
| **VigilEngine** | Python 3 | Agent execution engine: LLM ReAct loop or deterministic role plans |
| **OrchestralLens** | Python + Rich (SSE Live) | Split-pane terminal observatory with live SSE updates and HITL keybindings |
| **SynapseNexus** | Rust + Python | Shared SQLite-backed blackboard for services, endpoints, candidates, and confirmed findings |
| **IronGate** | Rust + Python | Approval gate for active/high-impact actions, with server-side token and scope checks |
| **PerimeterLock** | Python + Rust | IP/CIDR/domain boundary enforcement, including deep command-token inspection |
| **Tartarus** | Rootless Podman (`orchestral-tartarus`) | Hardened, capability-dropped (`--cap-drop=ALL`), PID-limited container isolation cell (Nmap, JtR, curl) |
| **ArchonSupervisor** | Python | Lifecycle manager: concurrency, cooperative cancellation, token budgets, runtime limits |
| **OrchestralArsenal** | Polyglot | Terminal gateway, CodeQL taint analyzer, secret scanner, native port/HTTP probes |
| **OrchestralChronicle** | SQLite WAL | Append-only, hash-chained audit ledger of actions, approvals, and discoveries |
| **Orchestral Desktop** | C# (.NET 8 + Avalonia UI) | Native operations console |

---

## 2. Operative Personas & Canonical Recipes

All agent operative definitions are unified in [`recipes/`](file:///home/admin/Swarm/recipes/) as canonical YAML workflow recipes:

- **Archon Coordinator (`archon_coordinator`)**: Strategic attack surface decomposition & 4-phase coordination.
- **Sentinel Recon (`sentinel_recon`)**: Network port discovery, service mapping, HTTP banner probing via `nmap` and `curl`.
- **Cerberus DAST (`cerberus_dast`)**: Web application vulnerability analysis, security header audits, and dynamic fuzzing.
- **Athena SAST (`athena_sast`)**: Source code audit, hardcoded secret hunting, CodeQL semantic taint analysis.
- **Justiciar Triage (`justiciar_triage`)**: De-duplication, false positive elimination, and CVSS v3.1 scoring.

---

## 3. Directory Layout

```
/home/admin/Swarm/
├── bin/
│   ├── orchestral            # Primary unified tactical CLI driver
│   ├── orchestral                 # Tactical CLI driver alias
│   ├── orchestral-core            # Compiled Orchestral Citadel binary (Rust)
│   ├── orchestral-desktop/   # Compiled standalone Avalonia C# Desktop executable
│   └── goose                 # Reasoning runtime binary (v1.57.0)
├── orchestral-desktop/       # Native C# Avalonia Desktop GUI project
│   ├── Orchestral.Desktop.csproj
│   ├── App.axaml / App.axaml.cs
│   ├── MainWindow.axaml / MainWindow.axaml.cs
│   ├── Models/
│   └── Services/CitadelService.cs
├── recipes/                  # Canonical Operative Recipes (Single Source of Truth)
│   ├── archon_coordinator/   # Lead Swarm Coordinator recipe
│   ├── sentinel_recon/       # Attack surface recon recipe
│   ├── cerberus_dast/        # Dynamic vulnerability assessment recipe
│   ├── athena_sast/          # Static code audit & secret recipe
│   └── justiciar_triage/     # Triage & CVSS scoring recipe
├── tests/
│   ├── test_unit_guards.py       # Guardrail and tool unit tests (no core needed)
│   └── test_terminal_mcp.py      # End-to-end integration test suite
├── orchestral-core/               # Orchestral Citadel (Rust + SQLite WAL)
│   ├── Cargo.toml
│   └── src/
├── orchestral-worker/             # VigilEngine Security Swarm Runtime (Python)
│   ├── pyproject.toml
│   ├── README.md
│   └── orchestral_worker/
│       ├── mcp/server.py     # Model Context Protocol stdio server
│       ├── sandbox/kali.py   # Hardened Tartarus container manager
│       ├── guard/scope.py    # PerimeterLock boundary enforcer
│       ├── guard/hitl.py     # IronGate Human-in-the-Loop gateway
│       └── supervisor.py     # ArchonSupervisor lifecycle manager
└── orchestral-desktop/       # Native Avalonia UI (.NET 8 C#) Desktop Application
```

---

## 4. Quickstart: CLI & Terminal Tool Execution

The core requires an admin key. Export it once; the CLI, MCP server, desktop,
and worker all read the same variable.

```bash
# 0. Export an admin key (use a long random value outside local testing)
export ORCHESTRAL_API_KEY="asw_live_change_me_to_a_long_random_secret"

# 1. Start Orchestral Citadel (SQLite WAL persistence, bound to 127.0.0.1:8088)
./bin/orchestral bastion --port 8088

# 2. Run a coordinated engagement
./bin/orchestral run --target 127.0.0.1 --allow 127.0.0.1 --auto-approve active

# 3. Run a single operative
./bin/orchestral agent --role sentinel_recon --target 127.0.0.1 --allow 127.0.0.1

# 4. Execute a terminal tool directly inside the hardened Tartarus container
./bin/orchestral tartarus --cmd "nmap -sT -Pn -n -p 8088 127.0.0.1" --target 127.0.0.1

# 5. View operative personas / recipes and available tools
./bin/orchestral personas
./bin/orchestral recipes
./bin/orchestral arsenal

# 6. Launch the terminal observatory or the desktop console
./bin/orchestral lens --snapshot
./bin/orchestral gui

# 7. Run the test suites
python3 -m unittest tests.test_unit_guards -v
python3 -m unittest tests.test_terminal_mcp -v
```

The core defaults to loopback. Set `ORCHESTRAL_BIND_ADDR=0.0.0.0` only when it is
behind a firewall, and set `ORCHESTRAL_JWT_SECRET` to keep agent tokens valid across
restarts. Set `ORCHESTRAL_CORS_ORIGINS` to a comma-separated allowlist for browser
clients other than localhost.
