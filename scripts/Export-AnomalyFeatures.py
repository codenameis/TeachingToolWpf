"""Export the fixed ImageNet feature extractor; runtime/training is C# only."""
import hashlib
import json
from pathlib import Path
import sys
import numpy as np
import onnxruntime as ort
import torch
import torch.nn.functional as F
from torchvision.models import resnet18, ResNet18_Weights


class Features(torch.nn.Module):
    def __init__(self):
        super().__init__()
        network = resnet18(weights=ResNet18_Weights.IMAGENET1K_V1)
        self.stem = torch.nn.Sequential(network.conv1, network.bn1, network.relu,
                                        network.maxpool, network.layer1)
        self.layer2 = network.layer2
        self.layer3 = network.layer3
        self.register_buffer('indices', torch.arange(0, 384, 4))

    def forward(self, image):
        second = self.layer2(self.stem(image))
        third = self.layer3(second)
        second = F.avg_pool2d(second, 3, 1, 1)
        third = F.interpolate(F.avg_pool2d(third, 3, 1, 1), (28, 28), mode='bilinear', align_corners=False)
        features = torch.cat((second, third), 1).index_select(1, self.indices)
        return features.permute(0, 2, 3, 1).reshape(784, 96)


target = Path(sys.argv[1])
target.mkdir(parents=True, exist_ok=True)
torch.manual_seed(17)
torch.set_num_threads(2)
torch.hub.set_dir(str(Path('artifacts') / 'anomaly-model-cache'))
model = Features().eval()
example = torch.randn(1, 3, 224, 224)
path = target / 'resnet18-patches.onnx'
torch.onnx.export(model, example, path, input_names=['image'], output_names=['patches'],
                  opset_version=17, dynamo=False)
with torch.no_grad():
    expected = model(example).numpy()
actual = ort.InferenceSession(str(path), providers=['CPUExecutionProvider']).run(None, {'image': example.numpy()})[0]
error = float(np.max(np.abs(expected - actual)))
assert error < 0.0001, error
example.numpy().astype('<f4').tofile(target / 'golden-input.bin')
expected.astype('<f4').tofile(target / 'golden-output.bin')
report = {'backbone': 'torchvision ResNet18 IMAGENET1K_V1', 'torch': torch.__version__,
          'shape': [784, 96], 'input': 'RGB 224x224 / ImageNet mean and std',
          'maxAbsoluteError': error, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
          'scope': 'Fixed feature extractor; normal memory bank is trained separately in C#.'}
(target / 'conversion.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
