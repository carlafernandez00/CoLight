# CoLight

*Coherent lighting for copresence*

## About

Mixed Reality applications suffer from visual inconsistencies between virtual content and real-world environments. In avatar-based scenarios, lighting mismatches reduce visual realism and harm the user's sense of copresence.

CoLight investigates how to bridge this gap by combining two technical pillars:

- **Environment lighting estimation** — inferring real-world illumination (direction, color, intensity, HDR environment maps) from headset sensors or images.
- **Avatar relighting** — rendering virtual humans with shading, shadows, and speculars that match the real scene in near real-time.

## Objectives

1. Implement a baseline using Unity lighting libraries and shaders for avatar rendering
2. Improve either light estimation (AI models) or avatar relighting/shading
3. Run user studies to measure impact on sense of copresence

## Tech Stack

- **Engine**: Unity 6000.3.9f1 (URP)
- **Target Device**: Meta Quest 3
- **XR**: OpenXR 1.16.1 + Meta XR SDK Core 85.0.0
- **Language**: C#

## Getting Started

### Prerequisites

- Unity 6000.3.9f1 (Unity Hub recommended)
- Meta Quest 3 headset (for deployment)

### Setup

1. Clone the repository:
   ```bash
   git clone https://github.com/carlafernandez00/CoLight.git
   ```
2. Open the project in Unity Hub
3. Let Unity resolve packages and import assets

### Build Targets

- **Android** (min SDK 25) — Meta Quest 3
- **Standalone** (macOS/Windows) — PC VR development

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
│   └── Avatar/              — character FBX
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
docs/                        — project documentation
```
