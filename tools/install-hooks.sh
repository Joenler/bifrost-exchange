#!/usr/bin/env bash
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
HOOK="$ROOT/.git/hooks/pre-commit"
cat > "$HOOK" <<'EOF'
#!/usr/bin/env bash
exec "$(git rev-parse --show-toplevel)/tools/scrub-gsd.sh"
EOF
chmod +x "$HOOK"
echo "Installed pre-commit hook -> tools/scrub-gsd.sh"
