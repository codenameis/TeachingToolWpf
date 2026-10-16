# 세 템플릿 매칭의 CPU+CUDA와 가독성 리팩토링

후속: 이 문서는 초기 CUDA 연결/동일성 기록이다. 현재의8K 전체 검색, 방식별 설정, 성능 개선과 Sub-NCC 정확도 수정은 [후속 검증](cpu-matching-performance.md)을 따른다.

2026-10-16, `main`의8c279f3에서 시작해 로컬 수정·검증했다. Line2D/Sub-NCC/Directional Chamfer에 실제 CUDA 후보 점수 계산을 추가했다. 알고리즘의 검색 순서와 점수·후보 예산은 보존했다. [결정](decisions/0059-cpu-matchers-cuda-and-readable-stages.md), [기초 알고리즘 읽기](cpu-matching-algorithm-guide.md).

## 사용

WPF에서 세 방식 중 하나를 선택하고 기존처럼 사각형 ROI를 학습한다. **상세 설정 → 후보 예산 · 실행 제어 → 템플릿 검사 실행 장치**에서 `CPU` 또는 `CPU+CUDA`를 선택한다. 장치 변경만으로 재학습할 필요는 없고 각 방식의 선택을 독립 보관한다. 기본은 CPU다.

CUDA는 모든 후보 위치의 점수를 병렬로 구한다. 피라미드/특징 준비·후보 선택·위치/각도 정밀화·Chamfer 양방향 원본 검증은 CPU다. 최상위 레벨이 원본인 작은 템플릿도 동일한 경로를 쓴다. **검색 점수나 후보 수를 낮추어 속도를 맞추지 않았다.**

로그에는 원본 읽기·검색·서비스·결과 게시·명령 완료·Present/fence 시간과 CUDA 장치명·초기화·전송·계산/대기·수신 시간이 나온다. GPU 계산/대기는 CPU 시계로 잰 제출/동기화 포함 시간이며 순수 CUDA event 시간으로 표현하지 않는다. 처음 CUDA를 선택하면 장치/커널 준비가 검색 시간에 포함된다. 작은 영역에서는 CPU가 더 빠를 수 있다. 장치 오류는 명시적으로 표시하며 자동 CPU 대체는 하지 않는다.

독립 라이브러리는 [CpuMatching.Cuda 사용 예](../src/CpuMatching.Cuda/README.md)를 따른다. 세 알고리즘은 Core만 참조하고 CUDA 의존성이 없다. CUDA 프로젝트도 WPF·VisionAlgorithm·ONNX·TorchSharp를 참조하지 않는다. 기존 ILGPU1.5.3을 사용한다. 정밀도를 강제 FP32로 바꾸지 않고 원래의 float/double을 보존한다([ILGPU 공식 수학 모드 설명](https://ilgpu.net/docs/03-advanced/04-math-functions/)).

## 바뀐 알고리즘 코드

- `PyramidSearch.Find`는 준비 → 후보 점수 → 피라미드 보정 → 원본 검증을 읽는 순서로 호출한다.
- `DirectionalSample`/`DirectionalPoseScore`는 Line2D와 Chamfer의 동일한 표본 회전/보간 점수를 공유한다.
- Sub-NCC의 표본 선택, Chamfer의 공간/방향 거리 전파와 점수 변환, 경계 보정의 가까운 대응 탐색을 이름 있는 메서드로 나눴다.
- CPU SIMD와 캐시 상한은 유지했다. 전체 위치를 빠짐없이 평가하는 격자는 평가 횟수를 맵 길이만큼 한 번 합산한다. 픽셀마다 `ref` 카운터에 쓰는 비용을 만들지 않는다.
- GPU 자원 수명은 Session, 전송/실행은 Backend, 수식은 Kernels에 둔다. 알고리즘 코드에 장치 API를 섞지 않는다.

행 단위 호출/인라인 강제 실험은 유지하지 않았다. 초기 비교에는 .NET tiered JIT 전환이 반복 시간에 섞였다. 최종 CLI는 첫 실행을 `FirstMs`, 추가10회 준비를 `WarmupMs`, 이후 반복을 `WarmMs`로 모두 보존한다. 첫 실행/JIT를 검사→표시 목표에서 제외하지 않는다.

## 정확도와 시간의 범위

정확도 기준은 누락/오검출0, 위치≤1px·각도≤1°·배율 오차≤0.03, 후보/출력 잘림 없음이다. 기존21조건의 동일 이미지 SHA256·옵션으로 비교한다. 20조건은 완전 정답이며 원본18V 한 조건은 알려진 대상만 확인했다. Kensa 수작업 중심은 약±1px 오차가 있다. 합성 회전/잡음/가림/배율 시험을 현장 전수 정확도로 표현하지 않는다.

8K 시험은8192×8192 파일 안의512×512 검색 영역이다. 마스크 없는 사각형 학습4~512px, 여백 포함2Mpx 검색 예산은 유지한다. **8K 전체 탐색과 CPU125ms/CUDA80ms 검사→표시 목표는 아직 달성하지 못했다.**

장비: Ryzen5 5600H, RTX3060 Laptop GPU6GB, NVIDIA581.83, Windows x64, .NET10.0.13 Release. 표시도 같은 GPU의 DX12다. 사용자의 다른 프로그램을 종료하거나 전원 설정을 바꾸지 않았다. 반복 시간의 작은 차이와 큰 UI 지연 변동을 모두 기록한다.

<!-- RESULTS -->
## 검증 결과

- PASS: 21조건×3방식, CPU 리팩토링 전후와 CPU/CUDA의 검출 목록·좌표·각도·배율·점수·평가 횟수·잘림 플래그가 모두 일치했다. 입력 hash와 옵션도 일치했다.
- PASS: 완전 정답20조건은 누락/오검출과 자세 기준을 통과했다. 원본18V는 알려진 한 대상만 통과했다.

### CPU 리팩토링 전후

아래는 각 프로세스의 첫 실행과 추가10회 준비를 별도로 기록한 뒤10회 측정한 중앙값(ms)이다. 원래 CPU 소스와 최종 소스의 서로 다른 DLL hash를 원시 자료에 보존했다.

|입력|방식|이전 CPU|정리 후 CPU|변화|
|---|---|---:|---:|---:|
|Kensa4개|Line2D|118.4|120.1|+1.5%|
|Kensa4개|SubNcc|129.7|130.5|+0.6%|
|Kensa4개|DirectionalChamfer|226.1|225.5|-0.3%|
|Kensa18V|Line2D|20.5|20.1|-2.1%|
|Kensa18V|SubNcc|57.7|59.8|+3.8%|
|Kensa18V|DirectionalChamfer|23.5|23.0|-2.1%|
|8K 안512²|Line2D|60.0|60.8|+1.2%|
|8K 안512²|SubNcc|435.3|437.4|+0.5%|
|8K 안512²|DirectionalChamfer|435.6|437.2|+0.4%|

대표9조합의 차이는 약−3~+4% 범위였다. 작은 수 ms 차이까지 성능 향상으로 주장하지 않는다. 초기 준비가 섞인 측정과 분리했으며, 다른 입력/PC의 무회귀를 보장하는 결과는 아니다.

### CPU와 CPU+CUDA

동일한 조건에서 준비10회 후5회 중앙값(ms). CUDA 열에는 검사별 전송·수신을 포함한다. PNG 읽기/UI는 제외한다.

|입력|방식|CPU|CPU+CUDA|
|---|---|---:|---:|
|Kensa4개|Line2D|119.9|124.9|
|Kensa4개|SubNcc|132.6|120.5|
|Kensa4개|DirectionalChamfer|225.8|227.1|
|Kensa18V|Line2D|23.3|9.6|
|Kensa18V|SubNcc|77.3|18.7|
|Kensa18V|DirectionalChamfer|24.2|16.0|
|1024² 합성|Line2D|122.1|139.2|
|1024² 합성|SubNcc|203.0|22.6|
|1024² 합성|DirectionalChamfer|1043.6|1141.3|
|8K 안512²|Line2D|58.1|83.7|
|8K 안512²|SubNcc|451.9|165.4|
|8K 안512²|DirectionalChamfer|437.1|361.7|

첫 CUDA 장치/커널 준비는 이 측정에서 최대 약547ms였다. `FirstMs`에 포함되어 있다. 특히 Sub-NCC의 넓은 탐색은 개선되지만 작은 영역의 Line2D 등은 전송/호출 비용으로 느려질 수 있다. CUDA가 항상 빠르다고 표현하지 않는다.

### WPF 명령→표시

7조건×3방식×CPU/CUDA×3회=126회에서 검출 조건과 표시 완료 확인을 통과했다. 실제 상세 설정의 장치 선택 바인딩도 확인했다. 아래는 반복1~2의 평균(ms)이며 첫 실행과 모든 개별값은 원시 자료에 있다.

|입력|방식|CPU 명령→표시|CPU+CUDA 명령→표시|
|---|---|---:|---:|
|Kensa4개|Line2D|264.3|395.3|
|Kensa4개|SubNcc|380.7|271.3|
|Kensa4개|DirectionalChamfer|1111.7|355.1|
|8K 안512²|Line2D|1162.7|494.7|
|8K 안512²|SubNcc|819.4|268.5|
|8K 안512²|DirectionalChamfer|602.5|919.3|

원본 읽기·검색·게시·표시 대기를 합친 값이다. GUI의 큰 변동을 검색 함수 속도와 혼동하지 않는다. CPU125ms/CUDA80ms 목표는 FAIL이다.

[전체 원시 JSON·비교·바이너리 hash ZIP](data/cpu-cuda-validation.zip), [전체 CPU/CUDA 요약 CSV](cpu-cuda-summary.csv).
<!-- /RESULTS -->

## 빌드·회귀·리뷰

- PASS: `TeachingToolWpf.slnx` 및 독립 평가 CLI Release 빌드, 경고0·오류0. 출력은 `artifacts/sidebar-verified`를 재사용했다.
- PASS: CPU8개, 실제 CUDA를 포함한 VisionCompute60개, WPF 검사 관련176개 회귀 테스트. CPU에서 GPU 생성하지 않기, 장치 미연결 오류, 방식별 장치 설정, 원본 좌표/회전 학습, 출력/후보 상한, 취소/독점 대여/지연 해제를 확인했다.
- PASS: CPU/CUDA 분기를 임시로 반전하면 `CpuDoesNotInitializeCudaAndUnavailableCudaDoesNotSilentlyFallBack`가 실패했다. 원본 복구 후 해당 통합12개가 다시 통과했다.
- PASS: 변경한 C# 파일의 공백·명명·스타일 검사와 변경분 검토. CUDA 전송 중 취소하면 업로드 캐시를 무효화해 다음 호출에서 일부만 바뀐 맵을 재사용하지 않게 했다. 이 오류 경로 보완은 정상 검색 측정 이후에 추가했으며 정상 계산 경로는 동일하다.
- FAIL: 저장소 전체 `scripts/Format-Code.ps1 -Verify`는 기존 다른55개 파일의 줄바꿈/공백 위반 때문에 실패했다. 요청과 무관한 파일은 일괄 정리하지 않았다.
- NOT_RUN: 다른 CPU/GPU·NVIDIA가 없는 PC의 실제 실행, 장시간 장치 오류/드라이버 리셋, 물리 클릭→모니터 표시, 현장 전수 정답과8K 전체 탐색. CUDA 없는 서비스 경계의 명시적 오류는 자동 테스트로 확인했다.

## 재현

기존 검증 출력 폴더를 재사용하며 별도 전체 배포 폴더를 만들지 않는다. 리팩토링 전 비교는 HEAD의 CPU 소스만 작은 참조 빌드로 만들고, 양쪽 CLI에 같은10회 준비 측정을 적용했다. Git 이력을 변경하지 않았다.

```powershell
dotnet build tools/CpuMatching.Evaluation/CpuMatching.Evaluation.csproj -c Release --artifacts-path artifacts/sidebar-verified -p:RestorePackagesPath=.packages -p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true
$cli = 'artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe'
& $cli run samples/CpuMatching.Accuracy artifacts/cpu-cuda-final-kensa.json all 5 compare
& $cli run samples/CpuMatching.Data artifacts/cpu-cuda-final-synthetic.json all 5 compare
& $cli run samples/CpuMatching.AccuracyValidation artifacts/cpu-cuda-final-validation.json all 5 compare
```

마지막 인자는 `cpu`/`cuda`/`compare`다. `compare`는 최종 검출 목록(좌표·각도·배율·점수)·평가 횟수·잘림 플래그가 CPU와 다르면 실패 종료한다. OpenCV는 기존 CPU 비교다. 실행 장치/회전 탐색 조건이 다른 OpenCV 수치를 공정한 동등 조건 비교로 표현하지 않는다.

WPF 재현은 기존 실행 파일의 `--cpu-cuda-comparison`이다. 숨김 시험 창에서 실제 선택 바인딩·학습/검색 명령·원본 reader·게시·DX12 Present/fence·다음 WPF Rendering을 확인한다. **물리 입력 메시지 대기와 모니터 scan-out은 포함하지 않는다.** CLI Find는 PNG 디코드/UI를 제외하고, 검색 준비·GPU 대여/해제·전송은 포함한다. WPF 시험의8K 배율은1 고정이고 CLI 추가 검증은0.75~1.25이므로 두 검색 시간을 직접 비교하지 않는다.

## 설계 원칙 검토

|원칙|이번 변경의 확인|
|---|---|
|SRP|검색 순서, 점수 계산, 장치 수명, 앱 연결을 각각 맡는다.|
|OCP|Core의 선택형 가속 계약을 추가하고 기존 PoseMatch/논문 형상 공급자를 바꾸지 않는다.|
|LSP|CPU/CUDA가 같은 표본·입력 한도·최종 검증을 사용하며 결과/평가 수·취소/오류를 검사한다.|
|ISP|점수 맵 두 연산만 있는 검색 대여 계약이다. CPU 점수 구현에는 GPU 자원 관리 메서드를 강제하지 않는다.|
|DIP|Core는 중립 계약만 알고 App/CLI가 선택형 CUDA를 주입한다.|
|Information Expert|알고리즘이 표본/정밀도, CUDA가 장치/버퍼를 소유한다.|
|Creator|호스트가 Session, Session이 Backend/Lease, Backend가 GPU 버퍼를 만든다.|
|Controller|기존 VM 명령과 검사 서비스가 장치 선택/실행을 조율한다.|
|Low Coupling|새 CUDA 프로젝트는 Core/ILGPU만 참조한다. Canvas·ONNX 의존성을 추가하지 않는다.|
|High Cohesion|공통 탐색 단계와 각 점수식을 각각 가까이 둔다.|
|Polymorphism|같은 검색이 CPU 점수/선택형 GPU 전체 점수를 호출한다.|
|Pure Fabrication|장치 세션은 알고리즘과 무관한 자원 수명을 격리한다.|
|Indirection|App의 대여 함수가 검사 서비스와 실제 CUDA 생성을 연결한다.|
|Protected Variations|CPU 기본값, 실패 명시, 단일 대여, 지연 해제, 방식별 설정/로그를 보존한다.|

원칙 예외 승인을 요구하는 변경은 없다. 기존 AnomalyMonitor 작업은 보존했고 커밋·푸시는 하지 않았다.
