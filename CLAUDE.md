# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CoLight (*coherent lighting for copresence*) is a master thesis project investigating real-time environment lighting estimation and avatar relighting in Mixed Reality. Built with **Unity 6000.3.9f1** (Unity 6.0.3) using the **Universal Render Pipeline (URP)**, targeting **Meta Quest 3** via OpenXR.

The core research goal: estimate real-world lighting from the headset and use it to relight virtual avatars so they visually match the real scene, improving the sense of copresence.

- **Language**: C# 9.0 (netstandard2.1)
- **Repository**: https://github.com/carlafernandez00/CoLight.git

## Architecture

### Rendering
Dual rendering pipeline configuration:
- `Assets/Settings/Mobile_RPAsset.asset` + `Mobile_Renderer.asset` — optimized for Quest
- `Assets/Settings/PC_RPAsset.asset` + `PC_Renderer.asset` — full-featured for desktop VR

### XR Stack
- **OpenXR 1.16.1** — cross-platform XR abstraction
- **Meta XR SDK Core 85.0.0** — Quest-specific features
- **XR Management 4.5.4** — loader/settings under `Assets/XR/`

### Input
Uses Unity's New Input System (1.18.0) with actions mapped for both gamepad and XR controllers: Move, Look, Attack, Interact (hold), Crouch, Jump, Sprint, Previous/Next.

### Key Packages
- `com.unity.render-pipelines.universal` (17.3.0)
- `com.unity.ai.navigation` (2.0.10) — AI pathfinding
- `com.unity.timeline` (1.8.10) — cinematic sequences
- `com.unity.visualscripting` (1.9.9)

### ML Integration (Planned)
The gitignore includes `*.pth` and `*.ckpt` for future PyTorch/checkpoint model integration, likely for AI-based environment lighting estimation (one of the two research pillars).

## Build & Run

Open the project in **Unity 6000.3.9f1**. Build targets:
- **Standalone** (macOS/Windows) for PC VR
- **Android** (min SDK 25) for Meta Quest

No CI/CD pipelines or custom build scripts are configured.

## Project Structure

```
Assets/
├── Prefabs/
│   ├── Avatar/              — avatar prefabs
│   └── Lighting/            — lighting rig prefabs
├── Materials/
│   ├── Avatar/              — avatar materials (URP Lit)
│   └── Environment/         — environment/scene materials
├── Models/
│   └── Avatar/              — character FBX from Mixamo
├── Textures/
│   └── Avatar/              — diffuse, normal, metallic maps
├── Scenes/
│   ├── Stage0_Scaffold.unity       — base MR scene (passthrough + avatar)
│   ├── Stage1_LightEstimation.unity — lighting estimation integration
│   └── Stage2_UserStudy.unity       — user study conditions
├── Scripts/
│   ├── Lighting/            — light estimation & relighting logic
│   ├── Avatar/              — avatar spawning & control
│   └── Utils/               — shared helpers
├── Settings/                — URP assets, renderer, volume profiles, input actions
├── UserStudy/               — questionnaires, data logging (later)
├── XR/                      — XR loader and settings configuration
└── Oculus/                  — Meta/Oculus SDK integration files
docs/                        — project documentation (overview, research context, objectives)
```
