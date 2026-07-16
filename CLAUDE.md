# Logging Standards
- Write log messages in plain language so users can immediately understand what happened.
- Keep log volume intentional: log key workflow steps at `info`, add deeper diagnostics at `debug`, and record failures with `error` plus useful context.
- Do not emit repetitive or duplicate log lines for the same event.
- Keep logs visually consistent and easy to scan, including clean formatting for timestamps and message structure.
