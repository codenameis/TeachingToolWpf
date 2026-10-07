# 알고리즘 출처·라이선스 확인 — 2026-10-06

현재 런타임의 보정·Gaussian·사각 형태학·threshold는 이 프로젝트에서 직접 작성했다. OpenCV/HALCON 또는 논문 구현을 호출/복사하지 않는다. 아래 확인은 문헌 조사이며 구현 재현/성능 비교 완료가 아니다.

| 자료 | 원문·공식 구현 확인 | 코드/문서 라이선스와 적용 상태 |
|---|---|---|
| R1 Ben Weiss, Fast Isotropic Median Filtering, SIGGRAPH 2025 | [원문](https://arxiv.org/html/2505.22938v1), [저자 보충 CPU SIMD/CUDA 코드](https://github.com/google/fast-isotropic-median-filter) | 저장소 Apache-2.0 표시 확인. 논문의 공개 라이선스와 코드 라이선스는 별개. ordinal transform/omnigram 원형 percentile 후보로만 기록; 포팅·공식 코드 실행 없음 |
| R2 Louis Sugy, A Fast Parallel Median Filtering Algorithm Using Hierarchical Tiling, SIGGRAPH 2025 | [원문](https://arxiv.org/html/2507.19926v1), [DOI](https://doi.org/10.1145/3721238.3730709) | 원문 CC BY-NC-SA 4.0. 저자 공식 코드 저장소/코드 라이선스는 확인하지 못함. 코드 복사 없음. selection-network/data-aware 계층 타일링 후보. L40S/전송 제외 수치를 이 엔진에 적용하지 않음 |
| R3 Yuji Moroto, Nobuyuki Umetani, Constant Time Median Filter Using 2D Wavelet Matrix, SIGGRAPH Asia 2022 | [저자 페이지·원문 링크·errata](https://cgenglab.github.io/en/publication/sigga22_wmatrix_median/), [공식 코드](https://github.com/TumoiYorozu/WMatrixMedian), [LICENSE](https://github.com/TumoiYorozu/WMatrixMedian/blob/master/LICENSE) | 코드 MIT 확인. 논문/다른 보충 코드의 라이선스와 동일하다고 가정하지 않음. 구조 생성 포함 benchmark/다른 영상 재구축 필요. 커널 크기에 관한 질의 복잡도이지 영상 크기와 무관한 전체 시간 아님. 미구현 |
| R4 Kaiming He, Jian Sun, Xiaoou Tang, Guided Image Filtering, ECCV 2010 / TPAMI 2013; He, Sun, Fast Guided Filter 2015 | [저자 논문·MATLAB 코드 링크](https://people.csail.mit.edu/kaiming/eccv10/index.html), [Fast Guided 원문](https://arxiv.org/pdf/1505.00996) | 배포 코드의 상업적 이용 조건은 아직 별도 확인 필요. 코드 복사 없음. exact Guided와 명시적 downsample 근사 Fast Guided를 구별할 예정. 미구현 |
| R5 Otsuka 외, IA-CLAHE, NTIRE 2026 | [원문](https://arxiv.org/html/2604.16010v1) | 학습된 clip-limit 추정기이다. 이번 기본 런타임에서 제외. 일반 CLAHE 완료 후 독립 실험 후보이며 산업 결함 개선을 추론하지 않음 |

R1/R2 원문 알고리즘 서술과 R4 Fast Guided PDF를 읽었다. R3는 저자 설명·errata·공식 저장소를 확인했으며 원문 전체에 대한 재현 검토는 후속이다. 논문/코드별 라이선스를 다시 확인하고 이식 범위를 기록하기 전에는 최적화 코드로 가져오지 않는다.

기본 동작 참고: [OpenCV filtering](https://docs.opencv.org/4.13.0/d4/d86/group__imgproc__filter.html), [CLAHE](https://docs.opencv.org/4.13.0/d6/db6/classcv_1_1CLAHE.html), [HALCON lines_gauss](https://www.mvtec.com/doc/halcon/2405/en/lines_gauss.html), [Variation model](https://www.mvtec.com/doc/halcon/13/en/create_variation_model.html). 이들은 알고리즘/공개 기능 설명 참고일 뿐 런타임 의존성이 아니다. Gaussian 반경/sigma와 replicate 규칙은 이 엔진 계약으로 고정했다. HALCON 결과와 동등성을 검증하지 않았다.

## 실행 의존성

- CPU Core/Cpu/Pipeline: .NET 기본 라이브러리만 사용. WPF/Avalonia/Skia/GDI/System.Drawing/OpenCV 의존 없음.
- [ILGPU 1.5.3 안정 패키지](https://www.nuget.org/packages/ILGPU/1.5.3)를 GPU 프로젝트에만 고정. [NCSA License](https://github.com/m4rs-mt/ILGPU/blob/v1.5.3/LICENSE.txt), 배포 고지는 `licenses/`에 보관한다. CUDA 드라이버는 별도 시스템 요구이며 CPU 배포에는 필요 없다.
- [ILGPU kernel 제약](https://ilgpu.net/docs/03-advanced/02-kernels/), [buffer/view 소유권](https://ilgpu.net/docs/02-beginner/02-memorybuffers-and-arrayviews/), [CUDA best practices](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/)를 확인했다. 첫 구현은 자동 그룹 커널/단일 stream이며 shared-memory barrier/overlap 지원을 주장하지 않는다.
- 테스트 전용 Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3, VS runner 3.1.4. BenchmarkDotNet은 이번 실행기에 사용하지 않았으며 별도 동기 완료 측정기를 사용했다. 개발 도구의 버전은 csproj에 고정했다.
