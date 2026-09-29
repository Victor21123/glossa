#!/usr/bin/env bash
# Runs the glossa-cli eval with one card model and one translator model on llama-server, records VRAM.
# Usage: tools/eval_models.sh NAME CARD_GGUF CARD_ALIAS TR_GGUF TR_ALIAS [EXTRA_CARD_ARGS...]
# CARD_NGL (env): set to "" to let "--fit on" place a large MoE model (it refuses when -ngl is given).
# TR_NGL (env): the same for the translator.
# TR_ARGS (env) adds llama-server flags for the translator, e.g. "--fit on" for a large MoE model.
# EVAL_ARGS (env) adds glossa-cli eval flags, e.g. "--dict-hint off".
# TR_GGUF may be "same" to use the card model for context translation (single-model mode).
set -u
name=$1; card=$2; card_alias=$3; tr=$4; tr_alias=$5; shift 5
# LLAMA_SERVER (env): another llama-server.exe; by default the official build Glossa downloads.
export CUDA_CACHE_PATH='D:\GlossaData\cuda-cache'
S=${LLAMA_SERVER:-/d/GlossaData/llama.cpp/b11243-cuda/llama-server.exe}
CLI=/d/projects/glossa/src/Glossa.Cli/bin/Debug/net8.0/glossa-cli.exe
OUT=/d/GlossaData/test/eval_results
mkdir -p "$OUT"
common="--ctx-size 4096 --flash-attn on --jinja --parallel 1 --host 127.0.0.1 --no-webui"

wait_up() { for i in $(seq 1 240); do [ "$(curl -s --noproxy '*' -o /dev/null -w "%{http_code}" "http://127.0.0.1:$1/health")" = "200" ] && return; sleep 1; done; }
t0=$(date +%s)
pids=()
"$S" --model "$card" --alias "$card_alias" ${CARD_NGL---n-gpu-layers 99} --port 8091 $common "$@" > "$OUT/$name.card.log" 2>&1 &
pids+=($!)
wait_up 8091
# The translator starts second, so "--fit on" sees the memory the card model already holds.
if [ "$tr" != "same" ]; then
  "$S" --model "$tr" --alias "$tr_alias" ${TR_NGL---n-gpu-layers 99} --port 8092 $common ${TR_ARGS:-} > "$OUT/$name.tr.log" 2>&1 &
  pids+=($!)
  wait_up 8092
fi
echo "$name: servers up after $(( $(date +%s) - t0 )) s; VRAM $(nvidia-smi --query-gpu=memory.used --format=csv,noheader)"
if [ "$tr" = "same" ]; then trspec="http://127.0.0.1:8091/v1|$card_alias"; else trspec="http://127.0.0.1:8092/v1|$tr_alias"; fi
"$CLI" eval /d/GlossaData/test/eval_cases.json "$OUT/$name.json" --card "http://127.0.0.1:8091/v1|$card_alias" --tr "$trspec" ${EVAL_ARGS:-} > "$OUT/$name.txt" 2>&1
echo "$name: VRAM at end $(nvidia-smi --query-gpu=memory.used --format=csv,noheader)"
# Only this run's servers: the installed Glossa may have its own llama-server loaded.
for p in "${pids[@]}"; do
  w=$(cat /proc/$p/winpid 2>/dev/null)
  if [ -n "$w" ]; then taskkill //PID "$w" //F > /dev/null 2>&1; else kill -9 "$p" 2>/dev/null; fi
done
sleep 2
