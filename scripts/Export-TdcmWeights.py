"""최초 공식 PyTorch 가중치만 데이터로 옮긴다. C# 학습/추론에는 Python이 필요 없다."""
import argparse
import hashlib
import struct
from pathlib import Path
import torch

p = argparse.ArgumentParser()
p.add_argument('source', type=Path)
p.add_argument('output', type=Path)
a = p.parse_args()
if hashlib.sha256(a.source.read_bytes()).hexdigest() != '4bbd7178908332a7b036b2f3893d6a664c5fa144955b93a85f0a03524768dc02':
    raise ValueError('검증한 공식 가중치와 다릅니다.')
weights = torch.load(a.source, map_location='cpu', weights_only=True)
items = [(k,v) for k,v in weights.items() if not k.endswith('num_batches_tracked')]
a.output.parent.mkdir(parents=True, exist_ok=True)
with a.output.open('wb') as f:
    f.write(b'TDCM0001')
    f.write(struct.pack('<i', len(items)))
    for name, value in items:
        name = name.encode('utf-8')
        f.write(struct.pack('<i', len(name))); f.write(name)
        f.write(struct.pack('<i', value.ndim))
        for size in value.shape: f.write(struct.pack('<q', size))
        f.write(struct.pack('<i', value.numel()))
        f.write(value.detach().float().contiguous().numpy().astype('<f4').tobytes())
print(hashlib.sha256(a.output.read_bytes()).hexdigest())
