# Project rules for Claude

# Responses — deliver as markdown files
- Deliver all substantive responses (reports, audits, reviews, analyses, summaries) as
  markdown (.md) files, not just chat text. Write the file, then surface it to me.
- Keep the chat reply itself short: a one-or-two-line pointer to the file and its verdict.
- Reuse a sensible location (e.g. docs/) and a stable filename when updating an existing report.

# Secrets — never hardcode, never commit
- NEVER write an API key, token, password, or broker/Rithmic/AMP credential as a literal value in any
  source file. No exceptions, not even "temporarily" or in an example.
- Secrets come from environment variables or a gitignored local config file, read at runtime. If code
  needs a key, reference the env var (e.g. read from the environment), never the value.
- If I paste a real key into chat, do NOT write it into a file — tell me to put it in an env var /
  gitignored config instead.
- Never commit, or instruct committing, a file that contains a real secret. If a secrets file is
  needed, add it to .gitignore first.
- If you spot a hardcoded secret anywhere in the code, STOP and flag it — do not leave it and do not
  silently "fix" it by moving it somewhere still committed.
