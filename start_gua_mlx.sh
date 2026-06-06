#!/usr/bin/env bash
# Launch GUA_Blazor with mlx_vlm.server (Apple Silicon / MLX native inference).
# Tested: M4 16 GB, mlx-community/gemma-4-12B-it-4bit, mlx_vlm 0.6.1
#
# Usage:
#   ./start_gua_mlx.sh                        # uses defaults below
#   MLX_MODEL=mlx-community/gemma-4-12B-it-4bit ./start_gua_mlx.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Config (override via env) ──────────────────────────────────────────────
MLX_MODEL="${MLX_MODEL:-mlx-community/gemma-4-12B-it-4bit}"
MLX_MODEL_NAME="${MLX_MODEL_NAME:-gemma4-4bit-slim}"   # must contain "slim" for SlimAgent
MLX_PORT="${MLX_PORT:-8082}"
APP_PORT="${APP_PORT:-5168}"
HF_HOME="${HF_HOME:-/opt/models/mlx-cache}"
# max-kv-size: must be > prompt_tokens + max_tokens across all turns.
# GUA slim prompt ≈ 1922 tokens; each tool result adds ~100–300 tokens.
# 16384 gives room for ~15+ tool-call/result pairs before context overflow.
MAX_KV_SIZE="${MAX_KV_SIZE:-16384}"
# prefill-step-size: chunk size for memory-safe prefill (see docs/mlx-gemma4.md).
# 512 is safe on M4 16 GB; increase to 1024 if you have 32 GB.
PREFILL_STEP_SIZE="${PREFILL_STEP_SIZE:-512}"
# Path to the mlx_vlm venv (pip install mlx-vlm installs mlx_vlm.server here)
MLX_VENV="${MLX_VENV:-$SCRIPT_DIR/.venv-mlx}"
LOG_DIR="/tmp"

echo "=== GUA Blazor MLX Launcher ==="
echo "  MLX model:         $MLX_MODEL"
echo "  Model name (GUA):  $MLX_MODEL_NAME"
echo "  mlx_vlm port:      $MLX_PORT"
echo "  App port:          $APP_PORT"
echo "  max-kv-size:       $MAX_KV_SIZE"
echo "  prefill-step-size: $PREFILL_STEP_SIZE"
echo "  HF_HOME:           $HF_HOME"
echo ""

# ── 1. Kill existing instances ─────────────────────────────────────────────
pkill -9 -f "GUA_Blazor" 2>/dev/null || true
pkill -9 -f "dotnet.*run" 2>/dev/null || true
pkill -9 -f "mlx_vlm.server" 2>/dev/null || true
lsof -ti:$APP_PORT | xargs kill -9 2>/dev/null || true
lsof -ti:$MLX_PORT | xargs kill -9 2>/dev/null || true
sleep 2

# ── 2. Apply mlx_vlm prefill_step_size patch if needed ────────────────────
GEN_PY="$MLX_VENV/lib/python3.11/site-packages/mlx_vlm/server/generation.py"
if [[ -f "$GEN_PY" ]]; then
    if ! grep -q "prefill_step_size=get_prefill_step_size" "$GEN_PY"; then
        echo "Applying mlx_vlm BatchGenerator prefill_step_size patch..."
        # Wire get_prefill_step_size() into BatchGenerator so --prefill-step-size
        # is actually respected in the server's continuous-batching path.
        # See docs/mlx-gemma4.md for details.
        sed -i '' 's/greedy_sampling=args.temperature == 0,$/greedy_sampling=args.temperature == 0,\n                            prefill_step_size=get_prefill_step_size(),/' "$GEN_PY"
        echo "  Patch applied."
    else
        echo "mlx_vlm patch already applied."
    fi
else
    echo "WARNING: mlx_vlm not found at $GEN_PY"
    echo "  Install with: pip install mlx-vlm  (inside $MLX_VENV)"
    exit 1
fi

# ── 3. Start mlx_vlm.server ───────────────────────────────────────────────
echo "Starting mlx_vlm.server on port $MLX_PORT..."
HF_HOME="$HF_HOME" nohup "$MLX_VENV/bin/mlx_vlm.server" \
    --model "$MLX_MODEL" \
    --port "$MLX_PORT" \
    --host 127.0.0.1 \
    --max-kv-size "$MAX_KV_SIZE" \
    --prefill-step-size "$PREFILL_STEP_SIZE" \
    > "$LOG_DIR/mlx_server.log" 2>&1 &
MLX_PID=$!
echo "  PID $MLX_PID → $LOG_DIR/mlx_server.log"

# Wait for /v1/models to respond
echo -n "  Waiting (model load ~20s)"
for i in $(seq 1 120); do
    if curl -sf "http://127.0.0.1:$MLX_PORT/v1/models" >/dev/null 2>&1; then
        echo " ready"
        break
    fi
    echo -n "."
    sleep 3
done

# ── 4. Start GUA_Blazor ────────────────────────────────────────────────────
echo "Starting GUA_Blazor on port $APP_PORT..."
> "$LOG_DIR/gua_blazor.log"

# GUA_API_ENDPOINT must NOT have /v1/ — GUA appends the path itself.
# GUA_MODEL must contain "slim" to enable SlimAgentInstruction (~80 tokens)
# and the 13-tool slim toolset; the full 50-turn/full-tool path uses >2x tokens.
nohup env \
    GUA_API_ENDPOINT="http://localhost:$MLX_PORT/" \
    GUA_MODEL="$MLX_MODEL_NAME" \
    GUA_MAX_TOKENS="4096" \
    GUA_HEADLESS="true" \
    dotnet run --project "$SCRIPT_DIR" --urls "http://0.0.0.0:$APP_PORT" \
    > "$LOG_DIR/gua_blazor.log" 2>&1 &
APP_PID=$!
echo "  PID $APP_PID → $LOG_DIR/gua_blazor.log"

echo -n "  Waiting"
for i in $(seq 1 40); do
    HTTP=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$APP_PORT/" 2>/dev/null)
    if [[ "$HTTP" == "200" ]]; then
        echo " ready (HTTP $HTTP)"
        break
    fi
    echo -n "."
    sleep 3
done

echo ""
echo "GUA (MLX) is running:"
echo "  UI   → http://localhost:$APP_PORT/"
echo "  API  → http://localhost:$APP_PORT/api/agent"
echo ""
echo "Logs:"
echo "  tail -f $LOG_DIR/mlx_server.log"
echo "  tail -f $LOG_DIR/gua_blazor.log"
