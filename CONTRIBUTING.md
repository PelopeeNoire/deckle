# Contributing

WhispUI is a **personal project** developed primarily as a learning exercise
and for the maintainer's own daily use. The codebase is public so others can
read, fork, learn from, or borrow ideas — not because external contributions
are actively solicited.

## Scope

External contributions are evaluated **case-by-case**. There is no
contribution roadmap, no commitment to review on any timeline, and no
guarantee that any given pull request will be accepted, even if it is
technically sound.

## Before opening a pull request

Open an **issue first**. Describe what you want to change and why. If the
change aligns with the direction of the project, the maintainer will let
you know whether a PR is welcome. Drive-by PRs without a prior issue may
be closed without review.

## Bug reports

Bug reports are welcome and useful. When opening a bug:

- Describe what you did and what you expected.
- Attach the relevant logs from `%LOCALAPPDATA%\WhispUI\logs\` (typically
  `app.jsonl` and `latency.jsonl`). Redact paths or content you consider
  private — these logs are local-only by design.
- Mention your Windows build, GPU vendor (Vulkan backend), and the model
  you were using.

## Security issues

For anything security-sensitive, follow the procedure in
[SECURITY.md](SECURITY.md) — do **not** open a public issue.

## License

By contributing, you agree your contribution is licensed under the same
[MIT license](LICENSE) as the rest of the project.
