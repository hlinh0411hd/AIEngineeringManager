# Qwen3.5 4B evaluation — 2026-11-01

Detected installed model: `qwen3.5:4b`, Q4_K_M, 4.2B parameters, approximately 3.3 GB on disk. `/api/show` reports completion/vision/tools/thinking capabilities and thinking controls `[false,true]`, default true. No download was performed.

The initial diagnostic used the existing adapter defaults. Server logs showed a 262,144-token context and requests reaching the 30-second deadline. That baseline was stopped; incomplete baseline results are not used for qualification.

The adapter now checks `/api/show` and requests `think=false` only when the installed model advertises false as a supported value. It explicitly uses `num_ctx=4096`, retains temperature zero, a 1,024-token output budget, per-example 30-second deadline, JSON output and `keep_alive=0`. Prompts above the local character limit are rejected for shortening or allowed cloud fallback rather than silently relying on a huge context. No benchmark answers or passing threshold were changed.

The same six exact-answer prompts were then executed through the updated C# provider and evaluator:

| Category | Passed | Accuracy | Total elapsed | Timeouts / errors | Default qualification |
|---|---:|---:|---:|---:|---|
| Classification | 3/3 | 100% | 10,015 ms | 0 / 0 | Pass |
| Extraction | 2/3 | 66.7% | 8,671 ms | 0 / 0 | Fail |

Timing includes model loading/unloading. No RAM/VRAM peak, sustained performance, content quality or coding ability was measured. A three-example category is only a preliminary qualification, not evidence of broad reliability. Coding and general agent/tool control are not granted by this suite.

Debug build succeeded with zero warnings/errors. All 28 executable checks passed, including the updated mock API assertions that thinking is disabled only using discovered capability and context is bounded to 4K. The real completed benchmark used only Ollama loopback; no cloud inference or paid API was used. `ollama ps` showed no loaded model after completion.

Results are also saved to the manager's evaluations table without enabling providers, granting write access or changing model permissions. In the app, use History & checkpoints -> View local evaluation results. Restart via Run.bat to use the updated adapter. Detect models and explicitly enable Ollama if desired; only qualified, allowed task types can route to it.

The native Codex Windows sandbox fix remains provider-specific. Other CLI providers use tool-free proposals and reviewed manager writes; their live authenticated integrations have not all been verified and are not claimed to be universally free of permission/authentication issues.

Reference: [Ollama generation API](https://docs.ollama.com/api/generate) and [thinking capability discovery](https://docs.ollama.com/capabilities/thinking).
