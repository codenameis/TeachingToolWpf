# 형상 검사 속도와 소수점 입력 검증

2026-10-04 후속 작업. 기준 장비는 Ryzen 5 5600H / RAM 32 GB / RTX 3060 Laptop 6 GB / Windows x64다. [ADR-024](decisions/0024-matching-throughput-and-decimal-input.md)의 변경을 검증한다.

## 입력과 방법

원본 8192×8192 및 24576×8192 procedural PCB. 핀 ROI 중심 (759.518, 546.178), 크기 105.827×129.459, 회전 ±180°/10° 간격, 최소 점수 .72, 추가 윤곽 감점 1. 최대 결과 1000, 원본 후보 2048(8K)/8192(24K), 검증 64, 거친 검증 256. 합성 핀 256/768개를 각각 원본 위치 오차 1 px 이내로 일대일 검증한다. 24K는 폭 24576, 높이 8192인 이 입력을 뜻하며 모든 24K 이미지/FPS를 대표하지 않는다.

각 크기·장치별 워밍업 1회, 측정 3회. 학습은 제외하고 매회 reader/CUDA 생성·원본 읽기·검색·자원 해제가 포함된 호출 전체를 측정한다. 전원/열 상태는 별도 통제하지 않았다. 절대 시간과 편차를 함께 기록한다. 합성 소스는 원본 타일 생성 비용도 읽기 시간에 포함하므로 실제 카메라/파일 성능으로 표현하지 않는다.

재현 명령:

```powershell
dotnet run --project benchmarks/TeachingToolWpf.Matching.Benchmarks -c Release -- artifacts/inspection-speed.json inspection-speed 4
```

## 변경 전후 실측

호출 전체 시간 중앙값(괄호는 3회 최소~최대), 단위 ms:

| 입력 | 모드 | 변경 전 CPU1 | 최종 CPU1 | 최종 CPU4 |
|---|---|---:|---:|---:|
| 8192×8192 | CPU | 4253.36 (4195.36~5653.94) | 3130.67 (3130.00~3137.08) | 2149.90 (2148.18~2149.96) |
| 8192×8192 | CUDA 전처리+CPU | 4729.65 (4677.29~4914.26) | 3250.20 (3216.31~3339.76) | 2338.15 (2325.48~2361.39) |
| 24576×8192 | CPU | 16455.00 (14736.43~17021.66) | 9562.48 (9475.93~10131.77) | 6532.94 (6424.32~6697.13) |
| 24576×8192 | CUDA 전처리+CPU | 16970.67 (15593.35~17208.97) | 9872.95 (9658.43~9925.57) | 7190.75 (7120.86~7455.10) |

CPU 1을 그대로 두어도 전체 시간이 8K 26.4%, 24K 41.9% 줄었다. 실제 후보/원본 정밀화에 CPU 4를 적용한 새 앱 기본은 변경 전 대비 각각 49.5%, 60.3% 줄었다. CUDA도 전체 경로 개선으로 빨라졌지만 최종 CPU 4보다 8K 8.8%, 24K 10.1% 느리므로 CPU 기본을 유지한다. GPU가 빨라졌다는 표현은 기존 CUDA 경로 대비 전체 시간 감소이며, CPU 대비 GPU 이득을 뜻하지 않는다.

원본 level-0 타일 읽기는 8K 768→512회, 24K 2982→1536회다. 축소 캐시로 거친 검색 중 재읽기를 없애고, 원본 refinement의 공간 순서로 다시 읽은 타일을 재사용한다. 원본 캐시 16 MiB에 축소 캐시 4 MiB를 추가했으며 그 숫자는 캐시가 소유한 대여 버퍼에만 적용한다. 전체 프로세스·gradient 작업 배열·native/GPU 메모리 상한이 아니다.

각 stage의 측정 12회 모두 정답 256/768개·오차 1 px·미잘림을 통과했다. 최종 CPU 1/CPU 4/CUDA의 전체 match records(반환 순서, pose, candidate/final/direction score, support, quadrants, clutter, refinement)가 변경 전과 정확히 같았다. 워밍업을 포함한 각 실행의 CPU/CUDA 동등성도 벤치마크에서 검사했다. 학습 ROI 하나를 사용했으며 검출 수를 높이기 위해 점수·검증 예산을 완화하지 않았다.

요약 데이터는 [비교표 JSON](data/matching-throughput-summary-20261004.json), 원시 반복은 아래 artifacts에 보관한다. 병렬 전처리·후보·정밀화 진단 합은 중첩된 작업 시간이며 위의 전체 시간에 더하거나 전체 시간 대비 점유율로 해석하지 않는다.

## 재현 증거

- 변경 전: `artifacts/inspection-speed-before.json`, `.log`. 원본 읽기 768/2982회. 모든 반복 256/768개, 예산 잘림 없음, CPU/CUDA record 정확 일치.
- 1차(변환 복사 제거·공간 읽기·고정 배율 중복 계산 제거), CPU 1: `artifacts/inspection-speed-after-1.json`. 원본 읽기 512/2208회, 변경 전과 16개 반복 전체의 match records 정확 일치. `artifacts/inspection-speed-intermediate-parity.json`.
- 소수점 RED: `artifacts/decimal-input-before.log`의 `0.` 기대/`0` 실제 FAIL. 수정 후 `artifacts/decimal-input-after.log`, 20/20 PASS. 완성 문자열만 쓰지 않고 한 글자씩 입력한다.
- 최종 CPU 4: `artifacts/inspection-speed-final-4.json`, `.log`; 최종 CPU 1: `artifacts/inspection-speed-final-1.json`, `.log`. CPU 1과 4 실행을 동시에 측정하지 않았다.
- 실제 소수점 입력: 검사 앱의 최소 점수 칸에 `0`→`.`→`7`→`2`→`0`을 각각 입력하고 Tab으로 이동했다. `0.720`이 유지됐으며 OS 원본 캡처/접근성 기록은 `artifacts/inspection-speed-user-state/decimal-input.jpg`, `.txt`다. 세 방식·다른 숫자 형식·문화권은 20개 자동 회귀로 추가 검증했다.

## 전체 회귀와 배포

Release 전체 **746 PASS, 실패 0, skip 0**: Core 182 / Algorithm 169 / VisionInspection 68 / CUDA 27 / Canvas 100 / 앱 200. 실제 CUDA 장치 검증을 포함한다. `artifacts/inspection-speed-full-tests.log`, `artifacts/inspection-speed-tests/*.trx`.

`pwsh -File scripts/Format-Code.ps1 -Verify`의 공백·코딩 스타일·명명 검사 PASS, 빌드 경고/오류 0. 최종 로그는 `artifacts/inspection-speed-format-verified.log`다. 처음 추가한 코드/테스트의 한 줄 초기화·줄 끝 오류는 자동 포맷 후 재검증했다. `git diff --check`도 통과했다. 독립 리뷰에서 장치 공급자에 동시 호출을 강요할 가능성을 지적받아 검사 경계에 직렬 어댑터를 추가했고, worker 종료 전 해제를 막는 테스트로 보완했다. SOLID 5/GRASP 9 검토 기록은 ADR-024를 따른다. 원칙 예외 없음.

최종 별도 게시물은 `artifacts/publish-inspection-speed-verified/TeachingToolWpf.exe`다. 실제 배포 앱의 DX12 debug 통합 검사는 exit 0으로 끝났다. CPU/CUDA 256개 검출, 회전 패턴 2개, 일반 도형 4종, 닫힌 폴리곤·검색 점선·Undo/Redo·문자 입력과 렌더 오류 없음이 확인됐다. 로그는 `artifacts/inspection-speed-runtime-final/artifacts/inspection-smoke.log`다. 첫 smoke는 접힌 일반 도형 고급 Expander를 열지 않은 테스트가 점수 필드를 찾지 못해 실패했다. 테스트가 해당 패널을 펼친 뒤 입력을 검사하도록 고치고 다시 통과했으며, 제품 결함이나 검사 기준 완화로 처리하지 않았다.

새 배포 앱을 직접 조작하여 핀 하나를 학습했다(특징 159개, 피라미드 3단계, 템플릿 37개). CPU 4로 8192×8192 전체를 검사한 결과 256개, 3117.7 ms(읽기/변환 1214.3 ms), OK였다. 검출 사각형과 검색 점선을 확인했고 앱은 검사 결과 표시 상태로 유지했다. 실제 OS 화면과 접근성 증거는 `artifacts/inspection-speed-user-state/final-inspection.jpg`, `.txt`다. 이 단일 UI 실행 시간은 위의 워밍업 후 반복 검사 중앙값 2149.90 ms와 구별한다.
