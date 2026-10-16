# 세 매칭의 속도 개선·8K 전체 검색·방식별 설정

2026-10-16, `main`에서 로컬 수정·검증. [ADR-061](decisions/0061-full-image-matching-and-measured-optimization.md). **CPU125ms/CUDA80ms 버튼→표시 목표는 아직 미달이다.**

## 사용

Line2D/Sub-NCC/Directional Chamfer 중 선택하고 사각형 ROI를 학습한다. 기본 탭의 **선택 ROI 범위에서 찾기**를 끄면 전체 원본을 검색한다. 8192×8192(67,108,864픽셀)까지 처리하며 원본을4K로 자르거나 자동 축소하지 않는다. 피라미드 후보 탐색 이후 원본으로 위치/각도를 보정한다. 학습 ROI는 기존4~512px, 마스크 미지원이다.

**상세 설정 → 알고리즘별 설정 · 변경 후 다시 학습**에는 선택 방식의 항목만 표시한다. 공통 검색각/각도 간격/배율/피라미드/중복 반경/점수/후보 수/출력 상한은 기존 항목이며 방식별로 보관된다.

| 방식 | 파라미터 | 기본값 | 범위/의미 |
|---|---|---:|---|
| Line2D | 축소 / 원본 특징 수 |64 /128|각16~512, 선택 윤곽 특징 수|
| Sub-NCC | 축소 표본 수 |96|16~512짝수, 명암/경계 표본을 반씩 선택하고 중복 제외|
| Sub-NCC | 학습 최소 명암차 |8|1~255, 상수 영상 학습 거부 기준|
| Chamfer | 축소 / 원본 특징 수 |64 /128|각16~512|
| Chamfer | 축소 거리 단위 |5|0.6~8, 해당 축소 영상의 픽셀 단위|
| Chamfer | 원본 거리 상한 |1|0.6~4, 실제 단위는 clamp(짧은 변/16,0.6,상한)|
| Chamfer | 방향 한 칸 비용 |1.5|0.1~8, 방향 거리 전파 비용|

변경하면 재학습 표시와 검사 차단을 적용한다. 이전 값으로 되돌리면 기존 모델을 다시 사용할 수 있다. Sub-NCC 원본 내부 픽셀 전체 검증과 Chamfer 원본 양방향 윤곽 검증은 항상 유지한다. 저장된 모델/파라미터는 현재 세션에 한하며 로그에는 사용된 설정이 포함된다. 아래 비교는 모두 기존 기본값이다.

## 어디서 오래 걸렸는가

검사 단계에 Stopwatch를 추가하고 CPU sampled-thread trace를 확인했다. 제한 영역에서도 방향 맵 준비, 후보의 반복 자세 보정, NCC 보간/명암 누적이 주요 계산이었다. UI는 계산이 끝나도 낮은 우선순위 continuation에서 기다리는 경우가 있었다. WPF는 Dispatcher 작업 우선순위를 SynchronizationContext에 반영한다. [WPF 원문](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/DispatcherOperation.cs), [공식 스레딩 설명](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model).

전체8K를 처음 허용한 직접 구현은 원본 전체의16방향 맵과 조밀한 경계 배열을 만들었다. Chamfer 준비가 약26초를 차지했고 프로세스 최대 working set은 약15.9GiB였다. 전체 최상위 탐색은 보존하면서 원본/중간 특징 맵과 자세 보정용 경계를 후보 주변에서만 준비하도록 바꿨다. 로컬 맵 준비 비용은 실행되는 보정 단계 시간에 포함된다.

추가로 방향과 무관한 gradient 길이를 픽셀당 한 번만 계산하고, 독립 방향/후보를 최대4작업으로 나눴다. 방향 최소값 전파는 같은 float 순서의 SIMD다. NCC는 경계를 확인한 뒤 같은 double bilinear 식으로 읽어 중복 경계 검사만 제거했다. 결과 순서/외부 중심 판정/NMS의 순서는 보존한다.

## 정확도 수정

새8K 전체 데이터에서 Sub-NCC가4/8만 찾았다. 같은 물체라도 축소 영상의 정수 픽셀 위상이 달라지면 작은 원판의 명암 봉우리가2~3픽셀 옆으로 이동했다. 그 점수로 반복 보정하고 좁은 범위로 원본까지 따라가면서 정답을 벗어났다. 점수/후보 수를 낮추지 않고, 한 단계 큰 영상의 전체 템플릿으로 첫 후보 보정을 수행했다. 중복 제거를 모두 미루는 실험은 Kensa 비용을 키워 최종 코드에 남기지 않았다.

기존 Line2D/Chamfer 결과·평가 수 보존과 Sub-NCC 수정 후 정확도, CPU/CUDA 결과 동등성을 별도로 검사한다. [위상 회귀 데이터](../samples/CpuMatching.PhaseValidation/cases.json)는 전체8K와 같은4픽셀 정렬의 작은 재현8개이며, 전체 범위 시험을 대신하지 않는다.

## 측정 조건과 결과

Ryzen5 5600H/32GB/RTX3060Laptop6GB, Release. 최소 점수0.75, 후보2048, 출력256, 검색각/배율/피라미드는 각 고정 JSON 및 WPF 진단 조건 그대로다. WPF는 실제 버튼의 Automation Invoke부터 원본 읽기·매칭·결과 게시·DX12 Present/fence·다음 WPF Rendering까지 관찰한다. 창은 화면 밖에 놓이며 물리 입력 메시지 대기/모니터 scan-out은 측정하지 않는다. 앱 준비/학습은 검사 시간 밖이고, 첫 CUDA 초기화는 검사 시간 안이다.

### 버튼→표시: 실제8K 전체 검색

8192×8192 전체, 템플릿89×52, 알려진 변환8개. 각 방식/장치3회 모두8/8, 오검출0, 후보/출력 잘림 없음. 최종 최대 위치 오차 0.1118px, 각도 오차 0.3147°다.

| 방식 | 장치 | 개선 전 중앙값(ms) | 개선 후 중앙값(ms) | 첫 실행(ms) | 최대(ms) | 시간 합격 |
|---|---|---:|---:|---:|---:|---|
|Line2D|CPU|4575.2|1956.7|1997.3|1997.3|0/3|
|Line2D|CPU+CUDA|4506.5|1050.9|2190.0|2190.0|0/3|
|Sub-NCC|CPU|4512.2|4065.6|6902.4|6902.4|0/3|
|Sub-NCC|CPU+CUDA|967.2|924.8|1275.7|1275.7|0/3|
|Chamfer|CPU|29382.4|5441.8|5441.8|5593.2|0/3|
|Chamfer|CPU+CUDA|27554.4|3914.5|3986.0|3986.0|0/3|

Sub-NCC 개선 전은4/8누락이 있던 버전이다. 최종은8/8이며 같은 정확도 결과의 순수 속도 비교로 표현하지 않는다. CPU125ms/CUDA80ms는18회 모두 미달이다.

500ms 간격 표본의 프로세스 최대 working set: **15.88→2.58GiB**. 순간 최대/VRAM/전체 시스템 메모리 상한을 의미하지 않는다.

### 버튼→표시: Kensa4개 부품 제한 영역

| 방식 | CPU 개선 전→후 중앙값(ms) | CPU+CUDA 개선 전→후 중앙값(ms) |
|---|---:|---:|
|Line2D|244.7→124.9|231.8→113.8|
|Sub-NCC|308.7→281.6|247.8→172.3|
|Chamfer|697.4→218.3|290.9→171.4|

중앙값만으로125/80ms를 합격 처리하지 않았다. Line2D CPU도3회 중1회가125ms를 넘었다. 첫 CUDA 초기화와 표시 대기, 조건별 변동은 [전체 표시 CSV](cpu-speed-display-summary.csv)에 보존했다.

### CLI 검색 준비 포함·UI 제외

동일21조건×3방식×2장치126건의 입력 해시·옵션이 기존 기준과 같다. 완전 정답20조건120건은 모두 합격이고, 원본18V6건은 알려진 대상만 확인했다. 새 위상 회귀8조건48건도 모두 합격이다. CPU/CUDA87조합은 결과·점수·평가 수·잘림 플래그가 정확히 같다.

Line2D42건/Chamfer42건은 최적화 전후 결과·평가 수까지 정확히 같다. Sub-NCC는 정확도 수정으로42건 중36건의 자세 또는 평가 수가 달라졌으며 모든 기존 정답 기준을 통과했다. [알고리즘 단계/전후 CSV](cpu-speed-algorithm-summary.csv)는 UI 포함 시간과 구별한다.

### 자동/실기 검증

- PASS: CPU9 + CUDA63 + WPF/검사 통합299 = **371개 테스트**. 큰 이미지의 순차 scorer 계약/중심 callback,256개 원본8K 타일 읽기, 방식별 설정 독립/재학습/입력 오류, 비동기 명령 문맥, 사용자 옵션 CPU/CUDA 동등성을 포함한다.
- PASS: 실제 WPF7조건126회 + 전체8K18회 = **144회** 정확도·표시 확인. 장치 선택과 알고리즘 옵션 변경→재학습 차단→원래 값 복원 바인딩을 실제 컨트롤에서 확인했다. CPU/CUDA 및 반복 결과 동일.
- PASS: Release 빌드 경고0/오류0, 이번 변경 파일의 공백/스타일/명명 검사. UI 문맥 테스트는 수정 전 실패를 확인한 뒤 통과했다.
- FAIL: 전체 `Format-Code.ps1 -Verify`는 이번 범위 밖 기존54개 파일의 공백/줄바꿈 문제로 실패했다. 해당 파일은 재포맷하지 않았다.
- FAIL: CPU125ms/CUDA80ms 버튼→표시의 전 조건 충족. 현장 전수 정답, 다른PC/장시간, 실제 물리 클릭→scan-out은 NOT_RUN.

원시 JSON/비교 스크립트/입력·소스 해시는 [검증 ZIP](data/cpu-speed-validation.zip), 입력 이미지는 [테스트 데이터 ZIP](data/cpu-full8k-test-data.zip)에 있다.

## 재현

기존 `artifacts/sidebar-verified` 출력과 하드링크를 재사용했다. 새 대형 publish 사본은 만들지 않았다.

```powershell
dotnet build TeachingToolWpf.slnx -c Release --artifacts-path artifacts/sidebar-verified -p:RestorePackagesPath=.packages -p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true --no-restore
artifacts/sidebar-verified/bin/TeachingToolWpf/release/TeachingToolWpf.exe --cpu-full8k-comparison
artifacts/sidebar-verified/bin/TeachingToolWpf/release/TeachingToolWpf.exe --cpu-cuda-comparison
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe run samples/CpuMatching.Accuracy artifacts/cpu-speed-final-kensa.json all 3 compare
```

전체8K의 정답은 [cases.json](../samples/CpuMatching.FullImage/cases.json), 이미지는 [8k-full-eight.png](../samples/CpuMatching.FullImage/8k-full-eight.png)다. 원본 Kensa 패키지를 알려진 위치/각도로 변환해 만들었다. 생성 코드는 `FullImageDataset`이며 CLI `full-image-data <새 폴더> kensa.png`, `phase-data <새 폴더> samples/CpuMatching.FullImage`로 재현한다. 기존21조건 CLI는10회 워밍업 후3회 측정하며 첫 실행도 보관한다. 병렬 실행의 할당량은 이제 프로세스 전체 managed allocation이므로 이전 호출 스레드 전용 수치와 직접 비교하지 않는다.

## 설계 검토와 남은 범위

| 원칙 | 적용 |
|---|---|
| SRP / Information Expert | 알고리즘별 점수와 옵션 검증은 해당 프로젝트, 전체 검색은Core, 원본 읽기는서비스, UI 상태는VM, 장치 수명은CUDA 세션 |
| OCP / Polymorphism | 기존 점수/가속기 계약으로 CPU와CUDA를 선택하고 기본 순차 계약은 유지 |
| LSP | 병렬 안전을 명시한 레벨만 나누며 IRowScore의 순차 호출 계약 유지 |
| ISP | 옵션은 알고리즘별 record, CPU 알고리즘이 WPF/CUDA 구현을 참조하지 않음 |
| DIP / Protected Variations | 서비스에 선택형 가속기를 주입하고 불가/실패를 숨기지 않음 |
| Creator | App이 서비스/장치 수명, matcher가 자신의 옵션/레벨, 검색이 검사 수명 캐시 생성 |
| Controller | VM이 학습/검사 상태, 알고리즘은 UI 상태를 알지 않음 |
| Low Coupling / High Cohesion | 이미지 표시/ROI 계층/외부 모델 구조 변경 없음 |
| Pure Fabrication / Indirection | 필요한 LocalScoreLevel만 기존 점수 계약을 감싸고 별도 범용 캐시 프레임워크는 추가하지 않음 |
| Clean Code / DRY / YAGNI | 준비→전체 후보→보정→원본 검증의 순서, 이름 있는 옵션, 고정된 작은 캐시 정책. 점수식/조건을 숨긴 빠른 프리셋 없음 |

작은 영역은 GPU 준비·전송 때문에 CPU보다 느릴 수 있다. 최대 후보/결과 초과는 계속 불완전 경고다. 합성8K8개 성공은 생산 이미지의 모든 부품/가림/밀집 후보·모든 템플릿 크기에 대한 보장이 아니다. 8K 전체의125/80ms, 실제 모니터까지의 클릭 지연, 장시간·다른PC는 별도 미달/미검증이다. 기존 PoseMatch 경로와 다른 채팅의 파일 로딩/이상 감지 작업은 유지했다. 커밋·푸시 없음.
