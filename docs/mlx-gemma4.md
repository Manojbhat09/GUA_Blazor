# GUA on Apple Silicon — MLX Native Inference (Gemma 4 12B)

This guide covers running GUA with **`mlx_vlm.server`** on Apple Silicon instead of `llama-server`.
It documents a required patch to `mlx_vlm`, launch instructions, and a full benchmark comparison
against the GGUF variants of the same model.

**Tested configuration:** M4 16 GB · macOS 15 · mlx_vlm 0.6.1 · `mlx-community/gemma-4-12B-it-4bit`

---

## Why MLX instead of GGUF?

| | GGUF (llama-server) | MLX (mlx_vlm.server) |
|---|---|---|
| Backend | llama.cpp CPU/Metal | MLX (Apple unified memory, GPU-native) |
| Vision support | Requires separate mmproj | Native multimodal (same model handles text + images) |
| Quantization options | Many (Q4, Q5, Q6, Q8…) | 4-bit and 8-bit (community) |
| M4 16 GB fit | ✅ Q5/Q6 both work | ✅ 4-bit only (8-bit OOMs) |

---

## Installation

```bash
# Create a dedicated venv inside the GUA_Blazor directory
python3 -m venv .venv-mlx
source .venv-mlx/bin/activate
pip install mlx-vlm
```

### Required patch — `mlx_vlm` ≤ 0.6.1

`mlx_vlm.server` defines `get_prefill_step_size()` and accepts `--prefill-step-size` on the CLI,
but **never passes the value to `BatchGenerator`**. The result: every prompt, regardless of length,
is processed in one uninterrupted forward pass. For Gemma 4 12B on M4 16 GB, any prompt longer
than ~1000 tokens causes a Metal OOM crash.

**Apply the patch once after installing mlx_vlm:**

```diff
# mlx_vlm/server/generation.py  (~line 1068)

 batch_gen = BatchGenerator(
     self.model.language_model,
     self.processor,
     stop_tokens=self.stop_tokens,
     sampler=self._make_sampler(args),
     kv_bits=self.kv_bits,
     kv_group_size=self.kv_group_size,
     kv_quant_scheme=self.kv_quant_scheme,
     quantized_kv_start=self.quantized_kv_start,
     compute_logprobs=bool(args.logprobs),
     top_logprobs_k=self.top_logprobs_k if args.logprobs else 0,
     stream=generation_stream,
     apc_manager=self.apc_manager,
     draft_model=self.draft_model,
     draft_kind=self.draft_kind,
     draft_block_size=_get_draft_block_size_from_env(),
     greedy_sampling=args.temperature == 0,
+    prefill_step_size=get_prefill_step_size(),   # ← ADD THIS LINE
 )
```

**Why this works:** With `prefill_step_size=512`, a 1922-token GUA prompt is split into four
512-token chunks. Each chunk runs one forward pass, evaluates the KV cache state
(`mx.eval([c.state for c in prompt_cache])`), then calls `mx.clear_cache()` before the next chunk.
Peak activation memory drops from O(seq²) to O(chunk²) — enough to keep the model inside 16 GB.

The `start_gua_mlx.sh` script auto-applies this patch via `sed` if it isn't already present.

---

## Launch

```bash
# Quickstart — uses gemma-4-12B-it-4bit by default
./start_gua_mlx.sh

# Custom model or ports
MLX_MODEL=mlx-community/gemma-4-12B-it-4bit \
MLX_PORT=8082 \
APP_PORT=5168 \
./start_gua_mlx.sh
```

**Key environment variables:**

| Variable | Default | Notes |
|---|---|---|
| `MLX_MODEL` | `mlx-community/gemma-4-12B-it-4bit` | HF repo ID; downloaded on first run |
| `MLX_MODEL_NAME` | `gemma4-4bit-slim` | **Must contain `"slim"`** — activates SlimAgentInstruction (~80 tokens) and 13-tool toolset |
| `MLX_PORT` | `8082` | mlx_vlm.server port |
| `APP_PORT` | `5168` | GUA Blazor UI port |
| `MAX_KV_SIZE` | `16384` | KV cache budget (tokens). Must be > prompt + max_tokens across all turns. GUA's slim prompt starts at ~1922 tokens; each tool result adds ~100–300 tokens. |
| `PREFILL_STEP_SIZE` | `512` | Chunk size for prefill. 512 is safe on 16 GB; use 1024 on 32 GB for faster prefill. |
| `HF_HOME` | `/opt/models/mlx-cache` | Model cache directory (~11 GB needed for 4-bit) |

**GUA env vars set by the script:**

```bash
GUA_API_ENDPOINT="http://localhost:8082/"   # no /v1/ — GUA appends the path
GUA_MODEL="gemma4-4bit-slim"               # "slim" triggers SlimAgentInstruction
GUA_MAX_TOKENS="4096"                      # maps to max_tokens=2048 in API calls
```

---

## Why `max-kv-size=16384` matters

GUA's SlimAgent prompt is ~1922 tokens on first turn. With `max-kv-size=4096` and
`max_tokens=2048`, the budget check (`prompt_tokens + max_tokens ≤ MAX_KV_SIZE`)
passes on turn 1 (1922 + 2048 = 3970 < 4096), but after the first tool result is
appended (~100–300 tokens), turn 2's prompt exceeds 4096 → **400 error → task stuck
after 1–2 tool calls**.

Setting `max-kv-size=16384` allows ~12 tool-call/result pairs before overflow, which
is enough for all benchmark tasks (max observed: 20 browser_use calls on H1).

---

## Model selection for M4 16 GB

| Model | Size | M4 16 GB | Notes |
|---|---|---|---|
| `mlx-community/gemma-4-12B-it-4bit` | ~11 GB | ✅ Works | **Recommended** |
| `agentmish/gemma-4-12B-it-mlx-8bit` | ~12 GB | ❌ OOM | Weights + OS + overhead > 16 GB |
| `osmapi/osmGemma-4-12B-uncensored-mixed-4.2bpw-mlx` | ~6.6 GB | ❌ Broken | Tool-call tokens never generated; loops on `<\|channel>thought` |

---

## Benchmark: Gemma 4 12B — H1–H8 task suite

Tasks run via [GUA_Blazor](../) autonomous agent on the same M4 16 GB machine.
Each task is scored: **✅ COMPLETE** (stop_loop called with result), **⚠️ PARTIAL** (tool calls made but no stop_loop), **❌ FAIL** (no tool calls or server crash).

| Task | Baseline | GGUF Q5_K_XL | GGUF Q6_K | osmapi-4.2bpw | **MLX 4-bit** |
|---|:---:|:---:|:---:|:---:|:---:|
| H1: Multi-hop Wikipedia | ⚠️ | ⚠️ | ⚠️ | ❌ | ⚠️ |
| H2: HN scores extraction | ⚠️ | ✅ | ✅ | ❌ | ✅ |
| H3: Code debug loop | ⚠️ | ✅ | ✅ | ❌ | ✅ |
| H4: Multi-file project | ✅ | ✅ | ✅ | ❌ | ✅ |
| H5: GitHub trending (browser) | ⚠️ | ✅ | ✅ | ❌ | ✅ |
| H6: Conditional shell logic | ✅ | ✅ | ✅ | ❌ | ✅ |
| H7: httpbin form fill | ⚠️ | ✅ | ⚠️ | ❌ | ✅ |
| H8: reCAPTCHA (vision_detect) | — | ❌ | ⚠️ | ❌ | ✅ |
| **Score** | 2✅ 5⚠️ | 6✅ 1⚠️ 1❌ | 5✅ 3⚠️ | 0✅ 8❌ | **7✅ 1⚠️** |

The MLX 4-bit model scores **7/8** — the highest of all variants tested, including GGUF. H8 (vision task)
is the standout: GGUF Q5 fails it outright, GGUF Q6 gets partial; MLX completes it because
`mlx_vlm` handles vision natively without a separate mmproj.

H1 (multi-hop Wikipedia) stays ⚠️ across all models — the model exhausts the 20-turn agent limit
without converging, consistent with the difficulty of multi-hop retrieval at this parameter scale.

### Tool call sequence for completed tasks (MLX 4-bit)

```
H2  browser_use → browser_use → stop_loop
H3  create_file → run_command → read_terminal_output → edit_file → run_command → read_terminal_output → stop_loop
H4  create_file → run_command → read_terminal_output → create_file → run_command → read_terminal_output → stop_loop
H5  browser_use → browser_use → stop_loop
H6  run_command → read_terminal_output → run_command → read_terminal_output → stop_loop
H7  browser_use × 6 → stop_loop
H8  browser_use × 6 → stop_loop
```

---

## Troubleshooting

**`[METAL] Insufficient Memory` on startup or first request**
The model weights exceed available unified memory. Use the 4-bit model. 8-bit requires >14 GB headroom.

**`400 Bad Request` after the first tool call**
`max-kv-size` is too small. Set `MAX_KV_SIZE=16384` (or higher).

**`ValueError: Missing N parameters`**
The model cache is corrupt (XET-protocol download was interrupted). Delete the cache and re-download:
```bash
rm -rf /opt/models/mlx-cache/hub/models--mlx-community--gemma-4-12B-it-4bit
```

**Model name and SlimAgent**
`GUA_MODEL` must contain the string `"slim"` to activate `SlimAgentInstruction`. Without it, GUA uses
the full ~500-token system prompt and all 20+ tools, pushing the first-turn prompt well above 2000 tokens.

**`GUA_API_ENDPOINT` format**
Must be `http://localhost:8082/` — **no `/v1/` suffix**. GUA appends `/v1/chat/completions` internally.
