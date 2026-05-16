"""
IOF Autonomous Agent — main entry point.

Runs on a schedule (GitHub Actions cron). Every cycle:
  1. Reads all files from the IOF_System Google Drive folder
  2. Sends full context to Claude API (claude-sonnet-4-6)
  3. Parses <FILE_UPDATE> blocks from Claude's response
  4. Writes updated/new files back to Drive

Required env vars:
  ANTHROPIC_API_KEY
  GOOGLE_SERVICE_ACCOUNT_JSON   (service account with Drive Editor access)
  GOOGLE_DELEGATED_EMAIL        (optional — folder owner email to impersonate)
"""

import os
import re
import sys
from datetime import datetime, timezone

import anthropic

from drive_client import DriveClient

# ── Constants ─────────────────────────────────────────────────────────────────

QUANTOWER_PATH = r"C:\Quantower\Settings\Scripts\Indicators\IOF"

PRIORITY_FILES = [
    "Handoff_Notes.md",
    "BRANDON_CLAUDE_RESPONSE.md",
    "MESSAGE_TO_BRANDON_CLAUDE.md",
]

SYSTEM_PROMPT = f"""You are Christopher's Claude — the autonomous IOF trading system agent running \
on a scheduled loop.

ROLE: Keep the IOF system moving forward without human intervention.

CORE FACTS (never change these):
- Trader: Christopher (Nfifty4)
- Platform: Quantower + Visual Studio  — NO TradingView, ever
- Quantower scripts path: {QUANTOWER_PATH}   ← always use this exact path
- Prop: Lucid Trading 5×$50k | $3k target | $2k DD | 90/10
- Instruments: MNQ, MES, MGC, CL, GC
- Strategy: IOF — IBI zones, ex-UBS methodology
- Indicator: TradePhantoms IOF v2 (current: v2.1.3-tp-entries)
- Brandon's Claude is running 52k+ bot simulations optimizing parameter space

YOUR JOB EACH CYCLE:
1. Read Handoff_Notes.md first — full context lives there
2. Check BRANDON_CLAUDE_RESPONSE.md — if new content, act on it immediately
3. Advance any open work items (code, docs, analysis, settings)
4. Update IOF_Changelog.md with what you did
5. Update Handoff_Notes.md Last Updated if significant work was done
6. Write MESSAGE_TO_BRANDON_CLAUDE.md with any new requests/responses for his Claude

OUTPUT FORMAT — to write or update a file, use this exact block (you can use multiple):
<FILE_UPDATE>
<FILENAME>exact_filename_including_extension</FILENAME>
<CONTENT>
full file content goes here — always write the complete file, not a diff
</CONTENT>
</FILE_UPDATE>

If no action is needed (nothing new, no pending work), output exactly:
<NO_ACTION>reason</NO_ACTION>

RULES:
- Never move SL further away (trading rules apply to code logic too — don't break interfaces)
- Always complete file content — never truncate
- Always correct Quantower path: {QUANTOWER_PATH}
- Changelog entry format: ## YYYY-MM-DD | Christopher's Claude | [what] | [why]
"""


# ── File update parser ─────────────────────────────────────────────────────────

UPDATE_PATTERN = re.compile(
    r"<FILE_UPDATE>\s*<FILENAME>(.*?)</FILENAME>\s*<CONTENT>(.*?)</CONTENT>\s*</FILE_UPDATE>",
    re.DOTALL,
)


def parse_updates(text: str) -> list[tuple[str, str]]:
    return [(m.group(1).strip(), m.group(2).strip()) for m in UPDATE_PATTERN.finditer(text)]


# ── Context builder ────────────────────────────────────────────────────────────

def build_context(file_contents: dict[str, str]) -> str:
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    parts = [f"**Cycle time:** {now}\n"]

    # Priority files first
    for fname in PRIORITY_FILES:
        if fname in file_contents:
            parts.append(f"=== {fname} ===\n{file_contents[fname]}\n")

    # Remaining files
    for fname, content in file_contents.items():
        if fname not in PRIORITY_FILES:
            parts.append(f"=== {fname} ===\n{content}\n")

    parts.append(
        "\n---\nReview everything above and take autonomous action. "
        "Output <FILE_UPDATE> blocks for every file you want to create or update. "
        "Output <NO_ACTION>reason</NO_ACTION> if nothing needs doing."
    )
    return "\n".join(parts)


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> int:
    print(f"[iof-agent] Starting cycle — {datetime.now(timezone.utc).isoformat()}")

    drive = DriveClient()
    claude = anthropic.Anthropic(api_key=os.environ["ANTHROPIC_API_KEY"])

    # 1. Read Drive
    print("[iof-agent] Reading Drive folder...")
    file_contents = drive.read_all()
    print(f"[iof-agent] {len(file_contents)} files loaded")

    # 2. Call Claude
    print("[iof-agent] Calling Claude API...")
    response = claude.messages.create(
        model="claude-sonnet-4-6",
        max_tokens=8096,
        system=SYSTEM_PROMPT,
        messages=[{"role": "user", "content": build_context(file_contents)}],
    )
    output = response.content[0].text
    print(f"[iof-agent] Response: {len(output)} chars | stop={response.stop_reason}")

    # 3. Parse updates
    updates = parse_updates(output)
    print(f"[iof-agent] {len(updates)} file update(s) to write")

    if not updates:
        no_action = re.search(r"<NO_ACTION>(.*?)</NO_ACTION>", output, re.DOTALL)
        if no_action:
            print(f"[iof-agent] No action: {no_action.group(1).strip()}")
        else:
            print("[iof-agent] WARNING — no updates and no <NO_ACTION> tag")
            print(output[:800])
        return 0

    # 4. Write to Drive
    for filename, content in updates:
        drive.write_file(filename, content)

    print(f"[iof-agent] Cycle complete — {len(updates)} file(s) written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
