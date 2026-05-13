#!/usr/bin/env python3
"""
Evaluate ONNX multi-label driving models on screenshot+label logs.

Expected label file format:
W,A,S,D,Space,timestamp
0,1,0,0,0,20260505_172055_281
...

Expected screenshot names:
Screenshot_YYYYMMDD_HHMMSS_mmm.png
"""

import argparse
import glob
import os
import re
from dataclasses import dataclass
from typing import List, Sequence, Tuple

import numpy as np
import onnxruntime as ort
from PIL import Image


MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)[:, None, None]
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)[:, None, None]
SCREENSHOT_RE = re.compile(r"Screenshot_(\d{8}_\d{6}_\d{3})\.png$")
KEYS = ("W", "A", "S", "D", "Space")


@dataclass
class EvalResult:
    model_path: str
    seq_len: int
    threshold: float
    exact_match: float
    macro_f1: float
    per_key_accuracy: Sequence[float]
    per_key_f1: Sequence[float]


def parse_bool_token(token: str) -> int:
    token = token.strip().lower()
    if token in ("1", "true", "t", "yes"):
        return 1
    return 0


def load_labels(path: str) -> List[Tuple[str, np.ndarray]]:
    rows = []
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        for i, line in enumerate(f):
            line = line.strip()
            if not line:
                continue
            parts = [p.strip() for p in line.split(",")]
            if len(parts) < 6:
                continue
            if i == 0 and parts[0].lower().startswith("w"):
                continue
            labels = np.array([parse_bool_token(x) for x in parts[:5]], dtype=np.int64)
            rows.append((parts[5], labels))
    return rows


def load_screenshot_index(folder: str) -> List[Tuple[str, str]]:
    items = []
    for fp in glob.glob(os.path.join(folder, "Screenshot_*.png")):
        m = SCREENSHOT_RE.search(os.path.basename(fp))
        if m:
            items.append((m.group(1), fp))
    return items


def preprocess_image(path: str) -> np.ndarray:
    image = Image.open(path).convert("RGB").resize((224, 224), Image.BILINEAR)
    arr = np.asarray(image, dtype=np.float32) / 255.0
    chw = np.transpose(arr, (2, 0, 1))
    return (chw - MEAN) / STD


def sigmoid(x: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-x))


def compute_metrics(y_true: np.ndarray, y_prob: np.ndarray, threshold: float):
    y_pred = (y_prob >= threshold).astype(np.int64)
    exact_match = float(np.mean(np.all(y_pred == y_true, axis=1)))
    per_key_accuracy = (y_pred == y_true).mean(axis=0)

    per_key_f1 = []
    for i in range(y_true.shape[1]):
        t = y_true[:, i]
        p = y_pred[:, i]
        tp = int(np.sum((t == 1) & (p == 1)))
        fp = int(np.sum((t == 0) & (p == 1)))
        fn = int(np.sum((t == 1) & (p == 0)))
        precision = tp / (tp + fp) if (tp + fp) else 0.0
        recall = tp / (tp + fn) if (tp + fn) else 0.0
        f1 = (2 * precision * recall / (precision + recall)) if (precision + recall) else 0.0
        per_key_f1.append(f1)

    macro_f1 = float(np.mean(per_key_f1))
    return exact_match, macro_f1, per_key_accuracy, per_key_f1


def evaluate_model(model_path: str, frames: np.ndarray, labels: np.ndarray) -> EvalResult:
    session = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    output_name = session.get_outputs()[0].name
    input_shape = session.get_inputs()[0].shape
    seq_len = int(input_shape[1]) if len(input_shape) == 5 else 1

    if frames.shape[0] < seq_len:
        raise ValueError(f"Not enough frames ({frames.shape[0]}) for seq_len={seq_len} in {model_path}")

    sample_count = frames.shape[0] - seq_len + 1
    y_true = labels[seq_len - 1 :]
    y_prob = np.empty((sample_count, 5), dtype=np.float32)

    for i in range(sample_count):
        x = frames[i : i + seq_len]
        x = x[0][None, ...] if seq_len == 1 else x[None, ...]
        y = session.run([output_name], {input_name: x})[0].reshape(-1, 5)[0]
        if y.min() < 0 or y.max() > 1:
            y = sigmoid(y)
        y_prob[i] = y

    best_result = None
    for threshold in np.linspace(0.1, 0.9, 33):
        exact_match, macro_f1, per_acc, per_f1 = compute_metrics(y_true, y_prob, float(threshold))
        score = 0.6 * macro_f1 + 0.4 * exact_match
        if best_result is None or score > best_result[0]:
            best_result = (
                score,
                EvalResult(
                    model_path=model_path,
                    seq_len=seq_len,
                    threshold=float(threshold),
                    exact_match=exact_match,
                    macro_f1=macro_f1,
                    per_key_accuracy=per_acc.tolist(),
                    per_key_f1=per_f1,
                ),
            )

    return best_result[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", required=True, help="Path to Input_Data.txt")
    parser.add_argument("--screenshots", required=True, help="Folder with Screenshot_*.png files")
    parser.add_argument("--models", nargs="+", required=True, help="One or more ONNX model paths")
    parser.add_argument(
        "--tail-frames",
        type=int,
        default=0,
        help="If >0, evaluate only the most recent N aligned frames",
    )
    parser.add_argument(
        "--stride",
        type=int,
        default=1,
        help="Evaluate every K-th aligned frame (for faster benchmarking)",
    )
    args = parser.parse_args()

    labels = sorted(load_labels(args.labels), key=lambda x: x[0])
    images = sorted(load_screenshot_index(args.screenshots), key=lambda x: x[0])
    n = min(len(labels), len(images))
    labels = labels[:n]
    images = images[:n]

    if args.tail_frames > 0 and args.tail_frames < n:
        labels = labels[-args.tail_frames :]
        images = images[-args.tail_frames :]
        n = len(labels)

    if args.stride > 1:
        labels = labels[:: args.stride]
        images = images[:: args.stride]
        n = len(labels)

    frames = np.empty((n, 3, 224, 224), dtype=np.float32)
    y = np.empty((n, 5), dtype=np.int64)
    for i in range(n):
        y[i] = labels[i][1]
        frames[i] = preprocess_image(images[i][1])

    print(f"Aligned samples: {n}")
    print(f"Label positive rate: {dict(zip(KEYS, np.mean(y, axis=0).round(4).tolist()))}")

    results = []
    for model in args.models:
        result = evaluate_model(model, frames, y)
        results.append(result)
        per_acc = {k: float(v) for k, v in zip(KEYS, np.round(result.per_key_accuracy, 4))}
        per_f1 = {k: float(v) for k, v in zip(KEYS, np.round(result.per_key_f1, 4))}
        print(f"\nModel: {model}")
        print(f"  seq_len: {result.seq_len}")
        print(f"  best_threshold: {result.threshold:.3f}")
        print(f"  exact_match: {result.exact_match:.4f}")
        print(f"  macro_f1: {result.macro_f1:.4f}")
        print(f"  per_key_accuracy: {per_acc}")
        print(f"  per_key_f1: {per_f1}")

    best = max(results, key=lambda r: 0.6 * r.macro_f1 + 0.4 * r.exact_match)
    print("\nBest model by score (0.6*macro_f1 + 0.4*exact_match):")
    print(f"  {best.model_path} (seq_len={best.seq_len}, threshold={best.threshold:.3f})")


if __name__ == "__main__":
    main()
