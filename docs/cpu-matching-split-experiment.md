# 위치 보정 후 8·16분할 CPU 검사 실험

2026-10-16. 사용자가 요청한 «이미지 위치 보정 후 바로 8개 또는16개로 잘라 검사»를 독립 평가 도구에서 실행했다. 로컬 수정·검증이며 제품 WPF 기본 검색을 교체하지 않는다. **부품 사이로 절단할 수 있는 합성8K에서 Chamfer8분할은 약29%, Sub-NCC16분할은 약6% 개선됐다. Line2D는 느려졌다. CPU125ms는 미달이다.**

## 조건과 측정 범위

- 기준 장비는 Ryzen5 5600H/32GB/RTX3060Laptop6GB, Windows x64, .NET10 Release다. 분할 벤치마크는 CPU만 사용한다.
- 위치 보정이 이미 끝났다고 가정한 원본 Gray8 입력이다. 보정 알고리즘을 실행하거나 그 시간을 0ms로 검증한 것이 아니다.
- 8분할은4열×2행,16분할은4열×4행이다. 원본 전체를 덮고 사전 정답 위치로 탐색 영역을 줄이지 않는다.
- 같은 최소 점수0.75, 각도-30~30°/10°, 배율1, 피라미드3, 후보 묶음2048, 최종 출력256을 유지한다. 조각 안의 후보도 끝까지 검사한다.
- 전체 검사는 기존 단계별4작업 설정을 유지한다. 최종 분할 검사는 최대4조각을 동시에 실행하고 조각 내부의 후보 계산·맵 준비는 모두1작업으로 둔다. 후보의 공간별 실행 순서는 유지한다. 조각 수와 동시 실행 수는 다르다. 전체 검색의 지역 맵 준비는 기존 중첩 실행을 보존하므로 전 프로세스의 엄격한4스레드 상한을 뜻하지 않는다.
- 템플릿의 회전·배율 반경, 축소 단계의 거리 맵 전파와 정밀 보정 이동 여백을 포함해 겹쳐 자른다. crop 원점은 피라미드 위상을 보존하도록4픽셀 배수로 맞춘다.
- 경계 근처 중심은 양쪽 조각에서 검증하고 원본 좌표로 복귀한 뒤 기존 거리/점수 순서의 NMS로 중복을 합친다. 조각 또는 최종 결과의 상한 초과는 실패로 기록한다.
- 별도 `aligned` 실험은 모듈 사이 빈 공간으로 절단할 수 있다는 전제에서 겹침 여백을0으로 둔다. 각도/점수/후보 조건은 같다. 이 가정이 맞는 기본8K256핀과, 가정이 깨지는 회전 경계8개를 구별한다. 위치 보정만으로 임의의 부품이 분할선에 걸리지 않는다고 보장할 수 없다.
- 픽셀 crop, 피라미드/맵 준비, 후보 탐색, 위치·각도 보정, 원본 검증, 병합이 시간에 포함된다. PNG 읽기, 학습, 위치 보정 자체, WPF 버튼/표시는 제외한다. **버튼→표시125ms 수치가 아니다.** 이번 분할 실험은 CPU이며 CUDA 분할 성능은 측정하지 않는다.
- 모듈 조건은 첫 실행을 포함한3회이며, 전체→8→16 /8→16→전체 /16→전체→8 순서로 순환한다. 경계 회귀와8작업 추가 시험은 각1회다. 별도 강제 GC나 결과 재사용을 하지 않는다. 측정과 빌드/테스트를 병행하지 않는다. 열 상태·OS 백그라운드 부하를 고정한 장기 성능 보장은 아니다.

입력은 [전체 정확도 데이터](../samples/CpuMatching.Modules/README.md)의 Kensa1024×1536/48패키지와 합성8192×8192/256핀, 별도 `samples/CpuMatching.FullImage`의 회전8K/8패키지다. 마지막 입력은2048/4096/6144 분할 경계와 영상 모서리의 회전 대상을 포함한다. Kensa 기준은 독립 ECC 정합이며 물리 교정값이 아니다.8K는 합성 정답이며 실제 촬영8K 전수 검출로 표현하지 않는다.

## 재현

```powershell
dotnet build CpuMatching.slnx -c Release --artifacts-path artifacts/sidebar-verified -p:RestorePackagesPath=.packages -p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true --no-restore
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe split samples/CpuMatching.Modules artifacts/cpu-split-modules-final.json all 3 all 4
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe split samples/CpuMatching.FullImage artifacts/cpu-split-boundary-final.json all 1 all 4
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe split samples/CpuMatching.Modules artifacts/cpu-split-aligned-final.json 8k-full-modules 3 all 4 aligned
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe split samples/CpuMatching.FullImage artifacts/cpu-split-aligned-boundary.json all 1 all 4 aligned
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe split samples/CpuMatching.Modules artifacts/cpu-split-workers8.json 8k-full-modules 1 SubNcc 8 aligned
```

작업 수 뒤의 선택 인자는 `overlap`(기본) 또는 `aligned`다. `all` 대신 입력 이름 또는 `SubNcc` 등 방식 이름을 지정할 수 있다. 결과 JSON에 모든 자세/점수, 입력 해시, 옵션, 각 조각의 범위/시간/후보/평가 수, 읽은 총 픽셀 수, 할당량과 CPU 시간을 남긴다. 할당량은 누적 managed allocation이며 동시 점유 메모리가 아니다. 조각별 시간은 병렬로 겹치므로 합계를 전체 벽시계 시간으로 해석하지 않는다. 정확도는 위치≤1px·각도≤1°·배율 오차≤0.03, 누락/추가/잘림 없음으로 검사한다. 전체 검색 결과와의 비교도 같은 허용 오차이며 bit-exact 동등성을 의미하지 않는다.

## 결과

### 겹침 분할

다음 표는 초기 비교다. 후보 계산은 조각 내부1작업이지만 Line2D/Chamfer의 방향 맵 준비에는 기존4작업 중첩이 남아 있었다. 따라서 이를 같은 총4작업 비교로 표현하지 않는다. 이 사실은 CPU 시간/경과 시간이4를 넘는 계측으로 발견했고, 최종 코드는 맵 준비에도 같은 옵션을 적용한다. 아래 원시 기록을 폐기하거나 최종 값으로 바꾸지 않는다.

Kensa/8K ×3방식×전체/8분할/16분할×3회 =54회 모두 누락·추가 검출·후보/출력 잘림 없이 정확도 기준을 통과했다. 반복 결과의 모든 자세/점수는 동일하다. 전체 검색과 분할의 비교는 위치/각도 허용 오차 기준이다.

| 입력 | 방식 | 전체 중앙값/최대(ms) |8분할 중앙값/최대(ms)|16분할 중앙값/최대(ms)|
|---|---|---:|---:|---:|
|Kensa48개|Line2D|5495.6 /5977.9|16773.8 /16798.5|25939.9 /26095.9|
|Kensa48개|Sub-NCC|2422.9 /2529.8|9531.6 /12643.0|14416.3 /14695.9|
|Kensa48개|Chamfer|3755.6 /4983.0|10643.8 /11941.2|16437.1 /16449.2|
|8K256개|Line2D|1987.3 /1996.9|2074.8 /2267.1|3018.0 /3076.1|
|8K256개|Sub-NCC|23051.4 /26532.7|30883.3 /32645.0|32459.3 /33765.3|
|8K256개|Chamfer|27617.6 /30449.9|30812.4 /31622.0|32722.6 /33245.7|

겹침 분할은 이 조건에서 빨라지지 않았다. 읽은 픽셀 수는8K에서1.35/1.56배, 작은 Kensa에서3.50/5.36배다.8K Sub-NCC 후보는1984→2406→2770개, Chamfer는4937→6300→7190개로 늘었다. 겹침 영역의 같은 대상을 최종 병합 전에 다시 정밀 검사하는 비용을 포함한다. CPU 캐시 잠금 대기 시간 자체는 별도 프로파일링하지 않았으므로 해당 비중을 단정하지 않는다.

Kensa의 최대 위치/각도 오차는 Line2D·Chamfer0.3212px/0.5905°, Sub-NCC0.2368px/0.3447°다. 기본8K는 Line2D·Chamfer0.2030px/0.0220°, Sub-NCC0.0743px/0°다. 측정한 CLI 입력/학습 파일의 자체 전체 검색과 비교하며, 이전 WPF 버튼 측정값을 이 표의 기준으로 사용하지 않는다.

최초 Sub-NCC 탐색 실험은 전체21.89초,8분할26.95초,16분할31.67초였다. 이후 분할 내부에도 기존 공간별 후보 순서를 유지한 위 최종 결과와 섞지 않는다.

### 모듈 사이 절단과 경계 회귀

최종4작업 분할은 맵 준비도 조각 내부1작업으로 실행한다. 최소 점수·각도 검색 조건과256개 검출/위치·각도 정확도를 유지했다. Line2D/Chamfer는 맵 준비 제어 후3회씩 다시 측정한 `cpu-split-controlled-line/chamfer.json`, Sub-NCC는 해당 준비 병렬화가 없어 기존 `cpu-split-aligned-final.json`의3회를 사용한다.

| 방식 | 전체 중앙값/최대(ms) |8분할 중앙값/최대(ms)|16분할 중앙값/최대(ms)|판단|
|---|---:|---:|---:|---|
|Line2D|1918.5 /2573.1|2446.7 /2449.4|2690.0 /2749.1|분할 시 느려짐|
|Sub-NCC|22013.7 /22299.0|20859.6 /20909.3|20698.3 /20712.6|16분할 약6% 개선|
|Chamfer|26083.4 /26986.4|18432.2 /18757.3|24197.4 /24203.5|8분할 약29% 개선|

초기 중첩 맵 준비 버전의 Chamfer8분할 중앙값은12583.5ms였으나, 여러 조각이 각각 내부4작업을 사용한 값이다. 최종 표의18432.2ms와 같은 실행 예산으로 표현하지 않는다. 맵 준비를 순차화하기 전후의 모든 결과 숫자·후보·평가 횟수는 정확히 같았다. 변경은 계산량을 줄인 것이 아니라 실행 병렬도 제어다.

Sub-NCC를8작업으로 실행한 추가1회에서는 전체15517.1ms,8분할14232.1ms,16분할14289.3ms였다. 모두256개·위치0.0743px·각도0°·잘림 없음이다.4작업보다 큰 개선은 동시 작업 수 증가에서 왔으며, 같은8작업끼리 비교한 분할 이득은 약8%다. 이1회 값을3회 중앙값 또는 안정적인 상한으로 표현하지 않는다.

**같은 검색 파라미터와 같은 전역 가설은 구별한다.** 여백 없는 절단에서는 조각 경계를 영상 끝으로 취급한다. Line2D 후보는3420→3408개, Chamfer는4937→4840/4832개로 달라진다. Sub-NCC 후보1984개는 같지만 평가 횟수는31347928→31344488/31342768회다. 점수·후보 묶음 상한을 낮추지 않았어도, 전역 후보/평가가 정확히 같은 범용 최적화라고 주장할 수 없다. 보정된 고정 배치에서 부품 사이를 자를 수 있다는 전제를 시험한 것이다.

분할 경계에 회전 부품을 둔 별도8K에서는 세 방식 모두:

- 전체 검색과 **겹침 분할은8/8검출**·위치/각도 기준 PASS다. 최대 오차는 Line2D/Chamfer0.0388px/0.3068°, Sub-NCC0.1119px/0.15°다.
- **여백 없는8·16분할은4/8검출로 FAIL**이다. 검출된4개의 자세가 맞는 사실을 전체 성공으로 표시하지 않는다. 출력/후보 잘림은 아니며 잘린 대상의 영상 문맥이 사라진 결과다.
- Line2D/Chamfer는 최종 준비 제어 후에도 양성/실패 조건을 다시 검사했고 이전 결과·후보·평가 수와 정확히 같다.

제품 기본 경로를 일괄 분할로 바꾸지 않았다. 부품이 경계에 걸리지 않는 배치에서 선택적으로 쓰는 실험과 임의 이미지의 전체 검출을 구별한다. 모든 측정은125ms를 넘으며, 위치 보정과 화면 표시까지 더하면 목표를 충족하지 못한다. CUDA 분할 벤치마크는 NOT_RUN이고 기존 GPU 경로는 유지한다.

## 검증과 증거

- CPU26 + CUDA66 = **92개 테스트 PASS**. 새 CPU 작업 수1/4의 세 방식 결과·점수·후보·평가 횟수 동등성과 유효 범위를 포함한다.
- `CpuMatching.slnx` Release 빌드 경고0/오류0. 변경8개 C# 파일의 공백·스타일·명명 검사 PASS.
- `scripts/Format-Code.ps1 -Verify`는 기존 범위 밖54개 파일의 줄바꿈 등 오류로 FAIL이다. 관련 없는 파일을 일괄 수정하지 않았다.
- 분석 스크립트가 전체 면적을 덮는 분할 core, crop 범위/4px 위상, 반복 결과 동등성, 고정 점수/각도/배율, 누락/잘림 및 맵 준비 전후 동등성을 검증했다.
- 원시 JSON/로그·재현 스크립트·소스/입력/실행 DLL 해시는 [검증 ZIP](data/cpu-split-validation.zip)에 있다. 입력 PNG는 기존 샘플을 사용하며 중복 복사하지 않았다. 초기 실험과 최종 준비 제어 실험의 출처를 manifest에서 구별한다.

## 변경 경계

분할/병합과 측정은 `tools/CpuMatching.Evaluation`에 둔다. 공통 검색은 CPU 작업 수만 명시적으로 받을 수 있게 하고 기존 기본값4를 유지한다. 새 이미지 라이브러리·GPU 세션·UI 설정을 추가하지 않는다. 테스트에서 CPU 작업 수1/4의 원본 회귀 픽셀 결과·점수·후보·평가 횟수 동등성과 유효 범위를 확인한다.

분할 계획/병합은 `TiledInspection`, 입력·정답 비교·시간 기록은 `SplitBenchmark`, 실제 점수 계산은 기존 matcher 책임이다(SRP/Information Expert). 기존 `ITemplateMatcher`를 사용하고 앱·WPF를 참조하지 않는다(DIP/Low Coupling). 새 인터페이스·관리자·캐시 계층 없이 작은 실행 옵션만 추가했다(KISS/YAGNI). 조각마다 독립 matcher와 지역 맵을 사용하고 입력 픽셀은 불변으로 공유한다. 실패·취소·잘림을 정상 정확도 통과로 처리하지 않는다.
