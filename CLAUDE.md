# Inkswap

Offline manga translator: WPF + WebView2 shell (copied from SpliceBench) around `web/index.html`.

## How it fits together
- `web/index.html` is the whole app: pages, boxes, the side pane, image analysis (erase, colour,
  bubble fit), lettering and the prompts. It talks to the exe through `/api/*` on its own virtual host.
- `src/Inkswap/MainWindow.xaml.cs` `ServeApiAsync` answers `/api/status`, `/api/fonts`, `/api/start`
  and `/api/chat`; every other non-embedded request is blocked (offline app).
- `src/Inkswap/Engine.cs` downloads llama.cpp (Vulkan build, pinned `LlamaBuild`) and the Qwen3-VL
  GGUF + mmproj files (pinned URL, size and SHA-256), then runs `llama-server` on 127.0.0.1 with a
  free port, in a kill-on-close job. Its log is `Inkswap-Data\llama.log`.
- Each item has two rectangles: `box` = erase area (also what Re-read crops) and `tbox` = text box,
  fitted once by `ensureFit` and then only moved by the user. All erase areas are applied to one
  copy of the page (`cleanPage`), so overlapping areas can't paste original pixels back.
- Pipeline per page: one vision call finds boxes + text (`bbox_2d`, 0 to 1000 coordinates, JSON
  schema enforced), optionally one call per box re-reads a close crop, then one text call
  translates all bubbles together (JSON array of the same length).

## Recipes
- Self-check: `Inkswap.exe --smoke` (no model needed). `--smoke <file.js>` runs any script against
  the app instead and saves `smoke.txt` + `smoke.png` in the data folder; the window stays
  off-screen and unfocused. `window.inkswap` exposes the pipeline for such scripts.
- Real-model test: copy the Debug output to a scratch folder and junction only its
  `Inkswap-Data\models` and `Inkswap-Data\llama` to the project root's, so the model is shared but the
  web view profile isn't. Two copies on one `Inkswap-Data` hang (one WebView2 profile can't be shared),
  which matters whenever the owner has the released exe open.
- Updating llama.cpp or a model: change the pinned build/file, size and SHA-256 together (sizes
  and hashes from `gh release view` and the Hugging Face `api/models/.../tree/main` listing).
- Icon: `tools\make-icon.ps1` (must be saved as UTF-8 with BOM for Windows PowerShell).
- Version lives in the csproj `<Version>` and `const VERSION` in `web/index.html`; `publish.ps1` checks.
