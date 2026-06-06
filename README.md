# 🤖 GUA (General Usage Agent)

GUA is a powerful, autonomous AI assistant built on **Blazor Server (.NET 10)**. It serves as a multi-modal orchestrator capable of managing file systems, executing terminal commands, browsing the web, and processing media (TTS/Transcription) through a unified agentic interface.

[![Changelog](https://img.shields.io/badge/updates-changelog-blue)](changelog.md)

---

## 📑 Table of Contents
1. [🚀 Features](#-features)
2. [🧠 Model Optimization](#-model-optimization)
3. [📦 Installation Guide](#-installation-guide)
    - [Playwright Setup](#1-playwright-browser-automation)
    - [llama.cpp Setup](#2-llamacpp-inference-server)
    - [whisper.cpp Setup](#3-whispercpp-transcription-server)
    - [mlx_vlm Setup (Apple Silicon)](#4-mlx_vlm-apple-silicon--mlx-native)
    - [Kokoro TTS Setup](#5-kokoro-tts-python)
4. [⚠️ Limitations & Known Issues](#️-limitations--known-issues)
5. [🛠️ Tools Reference](#️-tools-reference)
6. [📁 Project Structure](#-project-structure)

---

## 🚀 Features

- **Autonomous Agent Mode**: Multi-turn reasoning loops (up to 50 turns) using tool-calling to solve complex tasks.
- **Persistent Terminal**: Real-time shell execution with session persistence across messages.
- **Deep Web Research**: Google Search and Playwright-powered browser automation.
- **Media Suite**: 
  - Voice cloning/Synthesis via Kokoro.
  - Video-to-audio extraction and burning subtitles with FFmpeg.
  - High-accuracy transcription via Whisper.
- **Repository Ingestion**: Analyze entire GitHub repositories in one go using the `GitIngest` tool.
- **Modern UI**: Dark-themed Blazor interface with markdown support, collapsible tool-execution logs, and **resizable sidebar**.
- **Control Flow**: Stop generation at any time and a dedicated `ask_user` tool for agent clarifications.
- **Visual Feedback**: Real-time toolcall animations for better transparency.

---

## 🧠 Model Optimization

GUA is specifically tuned for high-parameter local models and state-of-the-art APIs:

- **Primary Optimization**: **Qwen3.5-35B-A3B** (Optimized for reasoning and tool-calling accuracy).
- **Secondary Support**: Fully compatible with **DeepSeek-V3/R1** via API.
- **Local Inference**: Designed to work seamlessly with `llama-server.exe` providing the OpenAI-compatible endpoint.

---

## 📦 Installation Guide

### 1. Playwright (Browser Automation)
Used for the `browser_use` tool to navigate real websites.
```bash
# Install the Playwright CLI tool
dotnet tool install --global Microsoft.Playwright.CLI

# Build the project to generate the executable
dotnet build

# Install the required browser (Chromium)
# Note: Check your bin path (net10.0)
pwsh bin/Debug/net10.0/playwright.ps1 install chromium
```

### 2. llama.cpp (Inference Server)
GUA requires an OpenAI-compatible API endpoint.
1.  **Download**: [llama.cpp Releases](https://github.com/ggerganov/llama.cpp/releases).
2.  **Model**: Download the GGUF for `Qwen3.5-35B-A3B`.
3.  **Run**:
    ```bash
    llama-server.exe -m path/to/Qwen3.5-35B-A3B.gguf --port 8080 -c 262144
    ```

### 3. whisper.cpp (Transcription Server)
1.  **Download**: [whisper.cpp Releases](https://github.com/ggerganov/whisper.cpp).
2.  **Model**: Use `ggml-large-v3.bin`.
3.  **Run**:
    ```bash
    whisper-server.exe -m models/ggml-large-v3.bin --port 8081
    ```

### 4. mlx_vlm (Apple Silicon — MLX Native)

Run GUA with native MLX inference instead of llama.cpp — no GGUF needed.
Tested on M4 16 GB with `mlx-community/gemma-4-12B-it-4bit` (scores **7/8** on the H1–H8 benchmark).

```bash
# Install mlx_vlm into a dedicated venv
python3 -m venv .venv-mlx
source .venv-mlx/bin/activate
pip install mlx-vlm

# Launch (downloads model on first run, ~11 GB)
./start_gua_mlx.sh
```

> **Required:** a one-line patch to `mlx_vlm/server/generation.py` — `start_gua_mlx.sh` applies it automatically.
> See [`docs/mlx-gemma4.md`](docs/mlx-gemma4.md) for the patch, all config options, and the full benchmark comparison against GGUF.

---

### 5. Kokoro TTS (Python)
The text-to-speech service requires the Kokoro Python implementation.
1.  Navigate to your Kokoro folder.
2.  Setup: `python -m venv venv` and `pip install flask kokoro soundfile`.
3.  Run the server on port **8082**.

---

## ⚠️ Limitations & Known Issues

### Current Limitations
- **No Persistence**: There is no database or LocalStorage implementation. Refreshing the browser will wipe the current chat history and session state.
- **No Multi-user**: The application is designed for local, single-user use. Session sandboxing is internal but not authenticated.
- **UI Desyncs**: The UI may occasionally flicker or desync during high-speed agent streaming or complex parallel tool calls.

### Known Technical Issues
- **Zombie Terminal Processes**: If the application crashes or is force-closed during a command execution, the underlying `cmd.exe` or `bash` process may remain running in the background and must be closed manually via Task Manager.
- **Playwright Cleanup**: The Chromium window opened by Playwright often stays open after the main program ends. These windows must be closed manually.

---

## 🛠️ Tools Reference

| Category | Tools |
| :--- | :--- |
| **Filesystem** | `create_file`, `read_file`, `edit_file`, `delete_file`, `list_directory`, `zip_directory`, `unzip_file`, `create_pdf` |
| **Terminal** | `run_command`, `read_terminal_output` |
| **Web** | `web_search`, `scrape_url`, `browser_use`, `git_ingest`, `vision_detect` |
| **Control** | `stop_loop`, `ask_user` |
| **Media** | `extract_audio`, `transcribe_audio`, `burn_subtitles`, `text_to_speech`, `merge_audio` |

---

## 📁 Project Structure

```text
GUA_Blazor/
├── Components/       # UI Layer (Pages, Layouts, Modals)
├── Helper/           # SessionSandbox (Path security logic)
├── Models/           # Data objects and System Instructions
├── Service/          # AIService (Orchestrator) and Media Wrappers
├── Tools/            # AI Tool Definitions
│   ├── Filesystem/   # File IO
│   ├── Terminal/     # Shell Execution
│   ├── Web/          # Playwright & Scrapers
│   └── TTS/          # Audio/Video processing
├── wwwroot/          # Styles and Client-side JS
└── run_all_servers.bat # Batch script for local dependencies
```

---

**Built with ❤️ using .NET 10 and Blazor Server.**