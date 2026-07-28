#!/usr/bin/env bash
set -euo pipefail

solution_path="SimpleHttpListener.Rx.slnx"

# Docker creates named volumes as root. Make persistent tool state available to
# the non-root account required for Claude's bypass-permissions mode.
sudo chown -R "$(id -u):$(id -g)" "${CLAUDE_CONFIG_DIR}" "${CODEX_HOME}" "${COPILOT_HOME}"

skills_installer="/opt/ai-skill/scripts/install-macos.sh"
if [[ -f "${skills_installer}" ]]; then
	bash "${skills_installer}" --no-prompt
else
	echo "Shared skills installer is unavailable at ${skills_installer}; skipping skill installation." >&2
fi

npm install -g --allow-scripts=@anthropic-ai/claude-code @anthropic-ai/claude-code @openai/codex

claude_settings="${CLAUDE_CONFIG_DIR}/settings.json"
mkdir -p "${CLAUDE_CONFIG_DIR}" "${CODEX_HOME}"

# The JavaScript template literal is intentionally protected from shell expansion.
# shellcheck disable=SC2016
node -e '
const fs = require("fs");
const path = process.argv[1];
let settings = {};
try { settings = JSON.parse(fs.readFileSync(path, "utf8")); } catch (error) {
  if (error.code !== "ENOENT") throw error;
}
settings.defaultMode = "bypassPermissions";
fs.writeFileSync(path, `${JSON.stringify(settings, null, 2)}\n`);
' "${claude_settings}"

codex_config="${CODEX_HOME}/config.toml"
touch "${codex_config}"
sed -i -E '/^(approval_policy|sandbox_mode)[[:space:]]*=/d' "${codex_config}"
printf '\napproval_policy = "never"\nsandbox_mode = "danger-full-access"\n' >> "${codex_config}"

# Restore last so a feed or project failure cannot abort assistant configuration.
# A failure here still fails postCreateCommand, but the container stays usable.
if [[ -f "${solution_path}" ]]; then
	dotnet restore "${solution_path}"
else
	echo "${solution_path} has not been created yet; skipping restore."
fi
