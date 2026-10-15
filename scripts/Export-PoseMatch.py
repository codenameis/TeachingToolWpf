"""공식 MIT 가중치의 C# 배포용 변환. 격리된 변환 환경에서만 실행한다."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import types
import subprocess

import numpy as np
import onnx
import onnxruntime as ort
import torch
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument("upstream", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
revision = subprocess.check_output(["git", "-C", str(args.upstream), "rev-parse", "HEAD"], text=True).strip()
if revision != "a6adc3a74fcf38ddbe4beb49e4bc76be07f848a2":
    raise ValueError("검증된 공식 revision이 아닙니다.")
subprocess.run(["git", "-C", str(args.upstream), "diff", "--exit-code", "HEAD", "--", "model.py", "dict/model.pth"], check=True)
args.output.mkdir(parents=True, exist_ok=True)
# 원 저장소 utils의 학습/시각화 의존성을 가져오지 않는다. 모든 값은 strict load로 대체한다.
utilities = types.ModuleType("utils")
utilities.weight_init = lambda module: None
sys.modules["utils"] = utilities
spec = importlib.util.spec_from_file_location("official_model", args.upstream / "model.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
torch.set_num_threads(4)
model = module.Model(None).eval()
weights = args.upstream / "dict/model.pth"
model.load_state_dict(torch.load(weights, map_location="cpu", weights_only=True), strict=True)
names = ["score", "sign", "cos", "scale_x", "scale_y"]
origin = torch.zeros(1, 3, 224, 224)
template = torch.zeros(1, 3, 36, 36)
path = args.output / "posematch-tdcm.onnx"
axes = {"origin": {2: "height", 3: "width"}}
axes.update({name: {2: "height", 3: "width"} for name in names})
with torch.no_grad():
    torch.onnx.export(model, (origin, template), str(path), input_names=["origin", "template"],
                      output_names=names, dynamic_axes=axes, opset_version=17, dynamo=False)
unoptimized_hash = hashlib.sha256(path.read_bytes()).hexdigest()
# 다섯 head의 같은 입력을 공유하는 FP32 convolution을 결합한다.
# 출력 채널 순서/가중치/활성화는 그대로이며 전체 영상 탐색을 줄이지 않는다.
graph = onnx.load(path)
head_names = ["conv_score", "conv_sign", "conv_cos", "conv_scale_x", "conv_scale_y"]
initializers = {item.name: onnx.numpy_helper.to_array(item) for item in graph.graph.initializer}
for stage in ["0", "2"]:
    for kind in ["weight", "bias"]:
        joined = np.concatenate([initializers[f"{head}.{stage}.{kind}"] for head in head_names], axis=0)
        graph.graph.initializer.append(onnx.numpy_helper.from_array(joined, f"fused.{stage}.{kind}"))
graph.graph.initializer.append(onnx.numpy_helper.from_array(np.ones(5, dtype=np.int64), "head_sizes"))
nodes = []
inserted = False
for node in graph.graph.node:
    if any(node.name.startswith("/" + head + "/") for head in head_names) and node.op_type in ["Conv", "Relu"]:
        if not inserted:
            nodes.extend([
                onnx.helper.make_node("Conv", ["/upsample_1/DepthToSpace_output_0", "fused.0.weight", "fused.0.bias"], ["fused_0"], pads=[1, 1, 1, 1]),
                onnx.helper.make_node("Relu", ["fused_0"], ["fused_relu"]),
                onnx.helper.make_node("Conv", ["fused_relu", "fused.2.weight", "fused.2.bias"], ["fused_2"], group=5),
                onnx.helper.make_node("Split", ["fused_2", "head_sizes"], [f"/{head}/{head}.2/Conv_output_0" for head in head_names], axis=1)
            ])
            inserted = True
        continue
    nodes.append(node)
del graph.graph.node[:]
graph.graph.node.extend(nodes)
used = {value for node in nodes for value in node.input}
kept = [item for item in graph.graph.initializer if item.name in used]
del graph.graph.initializer[:]
graph.graph.initializer.extend(kept)
onnx.checker.check_model(graph)
onnx.save(graph, path)
options = ort.SessionOptions()
options.intra_op_num_threads = 4
session = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
errors = []
generator = np.random.default_rng(42)
for height, width in [(224, 224), (256, 320), (512, 512)]:
    image = generator.normal(size=(1, 3, height, width)).astype(np.float32)
    patch = generator.normal(size=(1, 3, 36, 36)).astype(np.float32)
    with torch.no_grad():
        reference = model(torch.from_numpy(image), torch.from_numpy(patch))
    actual = session.run(None, {"origin": image, "template": patch})
    for name, expected, output in zip(names, reference, actual):
        error = float(np.max(np.abs(expected.numpy() - output)))
        errors.append({"size": [width, height], "head": name, "maxAbsoluteError": error})
        np.testing.assert_allclose(output, expected.numpy(), rtol=2e-4, atol=2e-4)
report = {"upstreamRevision": revision,
          "optimization": "FP32 five-head convolution fusion; unchanged weights and full spatial search",
          "unoptimizedOnnxSha256": unoptimized_hash,
          "weightsSha256": hashlib.sha256(weights.read_bytes()).hexdigest(),
          "onnxSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
          "torch": torch.__version__, "onnxruntime": ort.__version__, "comparisons": errors}
(args.output / "conversion.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
fixture = Path("tests/PoseMatch.Tdcm.Tests/Fixtures")
fixture.mkdir(parents=True, exist_ok=True)
origin_bgr = np.asarray(Image.open(args.upstream / "res/image.png").convert("RGB"))[:, :, ::-1].copy()
origin_bgr = np.pad(origin_bgr, ((0, -origin_bgr.shape[0] % 4), (0, -origin_bgr.shape[1] % 4), (0, 0)))
template_bgr = np.asarray(Image.open(args.upstream / "res/template.png").convert("RGB"))[:, :, ::-1].copy()
def normalize(pixels, width, height):
    sh, sw, _ = pixels.shape
    xx = np.clip((np.arange(width) + .5) * sw / width - .5, 0, sw - 1)
    yy = np.clip((np.arange(height) + .5) * sh / height - .5, 0, sh - 1)
    x0, y0 = xx.astype(int), yy.astype(int)
    x1, y1 = np.minimum(x0 + 1, sw - 1), np.minimum(y0 + 1, sh - 1)
    fx, fy = (xx - x0).astype(np.float32)[None, :, None], (yy - y0).astype(np.float32)[:, None, None]
    value = (pixels[y0[:, None], x0].astype(np.float32) * (1 - fx) + pixels[y0[:, None], x1] * fx) * (1 - fy) + (pixels[y1[:, None], x0].astype(np.float32) * (1 - fx) + pixels[y1[:, None], x1] * fx) * fy
    value = (value / 255 - np.array([.485, .456, .406], dtype=np.float32)) / np.array([.229, .224, .225], dtype=np.float32)
    return value.transpose(2, 0, 1)[None].copy()
height, width, _ = origin_bgr.shape
th, tw, _ = template_bgr.shape
with torch.no_grad():
    heads = model(torch.from_numpy(normalize(origin_bgr, width, height)), torch.from_numpy(normalize(template_bgr, 36, 36)))
peak = int(heads[0].flatten().argmax())
probes = sorted(set([peak] + list(range(0, height * width, 503))))
golden = {"width": width, "height": height, "templateWidth": tw, "templateHeight": th, "peak": peak,
          "probes": [{"index": i, "values": [float(head.flatten()[i]) for head in heads]} for i in probes]}
(fixture / "image.bgr").write_bytes(origin_bgr.tobytes())
(fixture / "template.bgr").write_bytes(template_bgr.tobytes())
(fixture / "golden.json").write_text(json.dumps(golden, indent=2), encoding="utf-8")
print("Official fixture:", width, height, tw, th, "peak", peak % width, peak // width)
