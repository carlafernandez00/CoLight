# Overview

CoLight — *coherent lighting for copresence*

# Problem Statement

Mixed Reality applications traditionally suffer from visual inconsistencies between virtual content and real-world environments. This issue is especially evident in avatar-based scenarios, where the mismatch reduces potential rendering enhancements that incorporate real-world illumination, affecting both visual realism and users' sense of copresence. This master thesis investigates how to bridge this gap.

# Research Gap

Two technical pillars need to be addressed:

- **Environment lighting estimation:** How to infer real-world illumination (direction, color, intensity, possibly HDR environment maps) from sensors or images for MR.
- **Avatar / human illumination:** How to render a virtual human so that shading, shadows, and speculars match the real scene, in near real-time. Also that the avatar can be *relit* when the environment changes

No existing work combines both pillars in a unified MR pipeline.

# Objectives

The system targets the **Meta Quest 3** headset, estimating real-world lighting directly from it and using that lighting to relight/render avatars inside **Unity MR**.

- [ ]  Implement a simple baseline: Unity lighting libraries + shader for avatar rendering
- [ ]  Decide on improvement direction:
    - Option A: Improve light estimation (AI models or other methods)
    - Option B: Improve avatar relighting/shading
- [ ]  Run user studies to measure impact on sense of copresence

[Timeline](https://www.notion.so/Timeline-3100f1db1f9e8052af9aec0793519c70?pvs=21)