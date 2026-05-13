# Explainable Vehicle Trajectory Prediction

Unity project for explainable vehicle trajectory prediction in a city-driving scene. The project combines player driving, AI-controlled vehicles, ONNX inference, traffic-light behavior, obstacle avoidance, and a real-time explainability HUD for inspecting vehicle decisions.

## Highlights

- Unity 6 project built with Editor `6000.3.8f1`.
- Main scene: `Assets/Scenes/City_night.unity`.
- ONNX-based vehicle-control inference through Unity Inference Engine.
- Player and AI vehicle controllers with wheel-collider physics, path following, red-light handling, obstacle avoidance, and recovery behavior.
- Explainable AI overlay for player telemetry, traffic risk, braking, steering, and nearby-object context.
- Data capture workflow for collecting driving input labels and screenshots.
- Python evaluation utility for benchmarking ONNX models against captured screenshot/label data.

## Project Structure

```text
Assets/
  AI_model/                         ONNX models and generated capture location
  Materials/                        Project materials and physics materials
  Prefabs/                          Player, AI car, and collider prefabs
  Scenes/                           Main Unity scenes
  Scripts/                          Vehicle control, inference, traffic, HUD, and data capture scripts
  ARCADE - FREE Racing Car/         Vehicle art assets
  Versatile Studio Assets/          City environment assets
Packages/
  manifest.json                     Unity package dependencies
ProjectSettings/
  ProjectVersion.txt                Unity editor version
tools/
  evaluate_onnx_models.py           Offline ONNX model evaluator
```

## Requirements

- Unity Editor `6000.3.8f1` or a compatible Unity 6 editor.
- Git LFS for large Unity/model assets.
- Python 3.9+ only if you want to run the ONNX evaluation script.
- Python packages for evaluation: `numpy`, `pillow`, and `onnxruntime`.

## Setup

```bash
git lfs install
git clone https://github.com/kirangautham-82899/Explainable-Vehicle-Trajectory-Prediction.git
cd Explainable-Vehicle-Trajectory-Prediction
git lfs pull
```

Open the project folder in Unity Hub with Unity `6000.3.8f1`, then load `Assets/Scenes/City_night.unity`.

## Core Scripts

- `Assets/Scripts/onnxcontroller.cs` runs ONNX inference for AI vehicle control, captures camera frames, follows paths, and handles traffic/obstacle responses.
- `Assets/Scripts/CarController.cs` handles player wheel-collider driving and traffic-light braking.
- `Assets/Scripts/AICarConroller.cs` provides non-ONNX AI path following, obstacle avoidance, and recovery behavior.
- `Assets/Scripts/TrafficLightManager.cs` spawns and coordinates traffic lights and assigns relevant signals to vehicles.
- `Assets/Scripts/PlayerExplainableMonitor.cs` renders the explainability overlay for player telemetry and risk context.
- `Assets/Scripts/DataCollection_1.cs` captures screenshots and input labels for training/evaluation data.

## AI Models and Data

Tracked ONNX models live in `Assets/AI_model/`:

- `game_ai_model.onnx`
- `game_cnn.onnx`
- `game_sequence_cnn.onnx`

Generated training captures are intentionally ignored by Git:

- `Assets/AI_model/Screenshots/`
- `Assets/AI_model/Input_Data.txt`

Those files are produced locally by `DataCollection_1.cs` during play mode and can become very large, so they are not committed to the repository.

## Evaluate Models

After collecting labels and screenshots locally, run:

```bash
python3 tools/evaluate_onnx_models.py \
  --labels Assets/AI_model/Input_Data.txt \
  --screenshots Assets/AI_model/Screenshots \
  --models Assets/AI_model/game_ai_model.onnx Assets/AI_model/game_sequence_cnn.onnx
```

The script reports exact-match accuracy, macro F1, per-key accuracy, per-key F1, and the best threshold for each model.

## Version Control Notes

This repository uses a Unity-specific `.gitignore` to keep generated folders such as `Library/`, `Temp/`, `Logs/`, and `UserSettings/` out of source control. Large model and Unity binary assets are tracked with Git LFS through `.gitattributes`.
