# MRAvatarIllumination

Real-time environment lighting estimation for mixed reality avatars on **Meta Quest 3**.

## About

In mixed reality, virtual content is often lit differently from the real room around it. For avatars, this mismatch breaks visual realism and weakens the user's sense of copresence.

This project estimates the real-world illumination from the headset's sensors at runtime and uses it to light virtual avatars so they look like they belong in the scene.

## How it works

```
Passthrough camera (color) ─┐
                            ├─► Environment map reconstruction ─► Equirectangular panorama
Environment Depth API ──────┘     (accumulated while scanning)      (color + depth maps)
                                                                           │
                                                                           ▼
                                                    L2 Spherical Harmonics projection
                                                    · per-probe parallax correction
                                                    · luminance importance sampling
                                                                           │
                                                                           ▼
                                                    Runtime light probe update ─► Avatar shading (URP)
```

1. **Environment map reconstruction.** Color frames from the passthrough camera and depth frames from the Environment Depth API are projected into two aligned equirectangular panoramas (color and depth maps). Coverage accumulates over time as the user looks around the room.
2. **Spherical Harmonics projection.** A compute shader projects the environment map to L2 SH (9 RGB coefficients). Each light probe gets its own parallax-corrected projection, which gives spatially varying lighting from a single capture.
3. **Importance sampling.** Instead of integrating every texel, directions are sampled according to a piecewise-constant 2D luminance distribution (PBRT-style CDF inversion).
4. **Runtime probe update.** The SH coefficients are written directly into Unity's light probes and ambient probe, because DynamicGI is not available on Quest 3.

## Tech stack

| | |
|---|---|
| Engine | Unity 6000.3.9f1 (URP 17.3) |
| Target device | Meta Quest 3 |
| XR | OpenXR 1.16.1, Unity Meta OpenXR 2.2.0 |
| Meta SDKs | Meta XR Core SDK 85.0.0, MR Utility Kit 85.0.0 |
| Language | C#, HLSL (compute shaders) |

## Getting started

### Prerequisites

- Unity 6000.3.9f1 (Unity Hub recommended)
- Meta Quest 3 with developer mode enabled
- Space Setup completed on the headset (required for the MRUK room mesh)

### Setup

1. Clone the repository:
```bash
   git clone https://github.com/carlafernandez00/MRAvatarIllumination.git
```
2. Open the project in Unity Hub and let Unity resolve the packages.
3. Switch the build target to **Android** and select the Quest build profile in `Assets/Settings/Build Profiles`.
4. Build and run on the headset. Camera and scene permissions are requested automatically on first launch.

## Scenes

| Scene | Purpose |
|---|---|
| `EnvironmentLightEstimation` | Main pipeline: environment map reconstruction + SH lighting |

## Project structure

```
Assets/
├── Scenes/          — main, debug and test scenes
├── Scripts/         — lighting estimation, reconstruction and debug components
├── Resources/       — compute shaders and custom shaders
├── Materials/       — avatar and environment materials
├── Models/          — avatar models
├── Textures/        — avatar textures and test HDRIs
├── Debug/           — profiling outputs and SH / importance sampling validation data
├── Settings/        — URP assets, volume profiles, build profiles
├── Plugins/Android/ — Android manifest
└── XR/              — XR loader and OpenXR settings
```


## Author

Carla Fernández Albert. MSc in Computer Graphics and Virtual Reality (MIRI-CGVR), Universitat Politècnica de Catalunya.