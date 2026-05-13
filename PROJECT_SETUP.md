<div align="center">

# Project Setup Guide

### Complete setup instructions for running, validating, and maintaining the Unity vehicle trajectory prediction project.

[![Unity](https://img.shields.io/badge/Unity-6000.3.8f1-000000?style=for-the-badge&logo=unity&logoColor=white)](https://unity.com/)
[![Git LFS](https://img.shields.io/badge/Git%20LFS-Required-F64935?style=for-the-badge&logo=gitlfs&logoColor=white)](https://git-lfs.com/)
[![Python](https://img.shields.io/badge/Python-Optional%20Evaluation-3776AB?style=for-the-badge&logo=python&logoColor=white)](https://www.python.org/)

</div>

---

## Table of Contents

- [Setup Goal](#setup-goal)
- [Prerequisites](#prerequisites)
- [Fresh Clone Setup](#fresh-clone-setup)
- [Open in Unity](#open-in-unity)
- [First Run Checklist](#first-run-checklist)
- [Controls](#controls)
- [Optional Python Evaluation Setup](#optional-python-evaluation-setup)
- [Data Capture Setup](#data-capture-setup)
- [Git LFS Verification](#git-lfs-verification)
- [Common Issues and Fixes](#common-issues-and-fixes)
- [Development Workflow](#development-workflow)
- [What Should Not Be Committed](#what-should-not-be-committed)

## Setup Goal

This guide helps you get the project running from a clean clone with:

- Unity project files loaded correctly.
- Git LFS assets downloaded as real binary files.
- ONNX models available in `Assets/AI_model/`.
- The main scene opened and playable.
- Optional Python tooling ready for model evaluation.

## Prerequisites

| Tool | Required | Recommended Version | Purpose |
| --- | --- | --- | --- |
| Unity Hub | Yes | Latest stable | Opens and manages the Unity project. |
| Unity Editor | Yes | `6000.3.8f1` | Matches the project version. |
| Git | Yes | Latest stable | Clones and versions the project. |
| Git LFS | Yes | Latest stable | Downloads large model and Unity asset files. |
| Python | Optional | `3.9+` | Runs the offline ONNX evaluation script. |

## Fresh Clone Setup

Install Git LFS once on your machine:

```bash
git lfs install
```

Clone the repository:

```bash
git clone https://github.com/kirangautham-82899/Explainable-Vehicle-Trajectory-Prediction.git
cd Explainable-Vehicle-Trajectory-Prediction
```

Download large LFS assets:

```bash
git lfs pull
```

Confirm the repository is clean:

```bash
git status
```

Expected result:

```text
On branch main
Your branch is up to date with 'origin/main'.
nothing to commit, working tree clean
```

## Open in Unity

1. Open Unity Hub.
2. Select **Add** or **Open**.
3. Choose the cloned project folder:

```text
Explainable-Vehicle-Trajectory-Prediction/
```

4. Open it with Unity `6000.3.8f1` or a compatible Unity 6 editor.
5. Wait for Unity to import packages and rebuild the `Library/` folder.
6. Open the main scene:

```text
Assets/Scenes/City_night.unity
```

The first Unity import can take several minutes because the project includes city assets, vehicle meshes, lightmaps, ONNX models, and Unity package dependencies.

## First Run Checklist

Before pressing Play, check:

| Check | Expected |
| --- | --- |
| Main scene loaded | `Assets/Scenes/City_night.unity` is open. |
| Console | No missing script errors. |
| ONNX models | Model files exist in `Assets/AI_model/`. |
| Packages | Unity package restore has completed. |
| Scene objects | Player car, AI vehicles, camera, paths, and traffic systems are present. |

Press Play after the checklist is clean.

## Controls

| Input | Action |
| --- | --- |
| `W` / Up Arrow | Accelerate |
| `S` / Down Arrow | Reverse or brake input |
| `A` / Left Arrow | Steer left |
| `D` / Right Arrow | Steer right |
| `Space` | Brake |
| `M` | Toggle explainability monitor |

## Optional Python Evaluation Setup

The Unity project can run without Python. Python is only needed for offline ONNX evaluation.

Create a virtual environment:

```bash
python3 -m venv .venv
```

Activate it on macOS or Linux:

```bash
source .venv/bin/activate
```

Install dependencies:

```bash
python3 -m pip install --upgrade pip
python3 -m pip install numpy pillow onnxruntime
```

Run the evaluator after collecting screenshots and labels:

```bash
python3 tools/evaluate_onnx_models.py \
  --labels Assets/AI_model/Input_Data.txt \
  --screenshots Assets/AI_model/Screenshots \
  --models Assets/AI_model/game_ai_model.onnx Assets/AI_model/game_sequence_cnn.onnx
```

Optional faster evaluation:

```bash
python3 tools/evaluate_onnx_models.py \
  --labels Assets/AI_model/Input_Data.txt \
  --screenshots Assets/AI_model/Screenshots \
  --models Assets/AI_model/game_ai_model.onnx Assets/AI_model/game_sequence_cnn.onnx \
  --tail-frames 1000 \
  --stride 2
```

## Data Capture Setup

The data capture workflow writes local training/evaluation data into:

```text
Assets/AI_model/Input_Data.txt
Assets/AI_model/Screenshots/
```

These files are intentionally ignored by Git because capture sessions can become very large.

Recommended capture workflow:

1. Open the main scene.
2. Enable or attach the data collection component only when you want to record.
3. Press Play.
4. Drive through the scene.
5. Stop Play mode when enough samples are collected.
6. Run the Python evaluator if needed.

Do not commit generated screenshots or `Input_Data.txt`.

## Git LFS Verification

Large assets should be tracked by Git LFS. Check the tracked LFS files:

```bash
git lfs ls-files
```

Important LFS-backed assets include:

```text
Assets/AI_model/*.onnx
Assets/**/*.fbx
Assets/**/*.exr
Assets/**/LightingData.asset
```

If a model file looks tiny or contains text like `version https://git-lfs.github.com/spec/v1`, run:

```bash
git lfs pull
```

## Common Issues and Fixes

| Issue | Likely Cause | Fix |
| --- | --- | --- |
| ONNX model is missing or tiny | LFS files were not downloaded | Run `git lfs pull`. |
| Unity opens with many import tasks | First import after clone | Wait for import to finish. |
| Missing package errors | Unity package restore not finished | Reopen Unity or let Package Manager resolve dependencies. |
| Missing script references | Project opened with incompatible Unity version or incomplete import | Use Unity `6000.3.8f1`, then reimport. |
| Generated files appear in Git | Local cache or capture output | Confirm `.gitignore` is active and avoid staging generated paths. |
| Python evaluator cannot import packages | Dependencies not installed | Install `numpy`, `pillow`, and `onnxruntime`. |
| No screenshots for evaluation | Data capture has not been run | Run play mode with data capture enabled. |

## Development Workflow

Use this flow for clean project changes:

```bash
git pull
git status
```

Make Unity or script changes, then inspect the changed files:

```bash
git status
git diff --stat
```

Stage only intentional source changes:

```bash
git add README.md PROJECT_SETUP.md
```

For Unity work, stage project files deliberately, for example:

```bash
git add Assets/Scripts Packages ProjectSettings Assets/Scenes
```

Commit and push:

```bash
git commit -m "Describe the change"
git push
```

## What Should Not Be Committed

The following are generated locally and should stay out of Git:

```text
Library/
Temp/
Obj/
Build/
Builds/
Logs/
UserSettings/
.DS_Store
Assets/AI_model/Input_Data.txt
Assets/AI_model/Screenshots/
```

## Final Setup Validation

A healthy setup should satisfy:

- `git status` is clean after clone and LFS pull.
- Unity opens the project without missing script errors.
- `Assets/Scenes/City_night.unity` loads successfully.
- ONNX models are present in `Assets/AI_model/`.
- Pressing Play starts the driving simulation.
- The explainability monitor can be toggled with `M`.

Once these pass, the project is ready for development, testing, model evaluation, and data collection.
