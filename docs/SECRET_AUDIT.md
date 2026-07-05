# Secret Audit — `christopherjliby-prog/IOF`

**Date:** 2026-07-05
**Scope:** read-only audit of tracked files + full git history. Nothing modified, deleted, rewritten, or pushed.

## Verdict: **CLEAN**

No API keys or secrets in tracked files or git history. Nothing to revoke.

---

## What I scanned and what it returned

### 1. Tracked files (14 total) — `git ls-files`
- No `.env`, no `*.key/.pem/.pfx/.p12`, no `secret`/`credential`/`config` files. Nothing secret-named.
- `git grep` for `sk-ant-` / `sk-…` key patterns → **no matches**.
- `git grep` for `api_key|apikey|secret|token|password|passwd|bearer|authorization = "value"` → **no assignments with real values**.
- Only textual mention: a code comment `// … no Windows-login` (benign).

### 2. Full history (4 commits) — `git log -p --all`
Commits: `8ab681e`, `15c8e72`, `a0ed767`, `76b8925`
- Key patterns (`sk-ant-`, `sk-…`) → **no matches**.
- Secret-variable assignments with real-looking values → **no matches**.
- Files ever added: same 14. **Files deleted in history: none** — so no secret hides in a since-removed file.

### 3. `.gitignore` status
- **No `.gitignore` exists.** Not an exposure by itself, but nothing prevents a future `.env` from being committed.

### 4. False positives run down (all benign — none are secrets)

| Match | File / location | Real? |
|---|---|---|
| `password/login/host/token` grep hits | `TradePhantoms_IOF_v2.cs` history | **Placeholder/none** — long identifiers (`lastItfBarIndexProcessedForTrend`), text "tokenize", `Environment.MachineName`, `host = ""` |
| `http://localhost:8000/api/...` | bridge URLs in `TradePhantoms_IOF_v2.cs` | **Not a secret** — local loopback endpoint, no credentials, no auth header |
| `MaxConnectionsPerServer = 64` | HTTP client config | Not a credential |
| `discord.com/api/webhooks/` | `AlertsHelper.cs:391` | **Not a URL** — a `url.IndexOf(...)` substring check to detect webhook type |
| `WebhookUrl` | `AlertsHelper.cs:81` | **Empty default** (`= ""`), user-supplied at runtime |
| `Authorization` / `Bearer` | — | **No matches anywhere** |

### 5. Remote exposure
- `origin` → `http://local_proxy@127.0.0.1:41729/git/christopherjliby-prog/IOF` (agent's GitHub proxy; GitHub repo is `christopherjliby-prog/IOF`).
- Public vs private visibility could not be confirmed through the proxied remote. **It does not change the verdict** — with zero secrets in files or history, there is nothing exposed regardless of visibility.

---

## Bottom line
- **No real API keys, tokens, passwords, or broker/Rithmic/AMP credentials** are committed — not in current files, not in history.
- **NEEDS-REVIEW: none.** Every pattern match resolved to a benign false positive.
- **EXPOSED / COMMITTED-BUT-PRIVATE: none.**

## Non-secret cleanup notes (report only — nothing changed)
1. `backtest/__pycache__/eval_sim.cpython-311.pyc` — a Python build artifact was committed (from an earlier `py_compile`). Harmless; candidate for `git rm --cached` + gitignore.
2. Consider adding a `.gitignore` (`.env`, `__pycache__/`, `*.pyc`, secrets files) to prevent an accidental future commit.

## Commands run (for reproducibility)
```
git ls-files
git grep -nE 'sk-ant-[A-Za-z0-9_-]{10,}|sk-[A-Za-z0-9]{20,}' -- <tracked>
git grep -niE '(api[_-]?key|apikey|secret|token|password|passwd|bearer|authorization)\s*[:=]\s*["'][^"']+'
git log -p --all | grep -nE 'sk-ant-…|sk-…'
git log -p --all | grep -niE '^\+.*(api_key|secret|token|password|bearer|rithmic|amp_login)\s*[:=]\s*["'][^"']{6,}'
git log --all --diff-filter=D --name-only        # deleted files
git log --all --diff-filter=A --name-only         # ever-added files
git remote -v
```
