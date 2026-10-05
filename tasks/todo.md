# 단계별 작업 목록

## 2026-10-05 폴리곤 점 입력과 교차점 자동 완성

기준: [ADR-028](../docs/decisions/0028-polygon-point-drawing-and-crossing.md). 사용자는 교차점에서 자동으로 닫는 동작을 선택했다.

- [x] PASS: 부모·자식 추가 버튼에서 오각형 즉시 생성 대신 빈 점 입력 시작. 사각형·원·삼각형 즉시 추가 유지. 점 취소/취소/완료/Enter/첫 점 클릭과 단일 이력 유지.
- [x] PASS: 새 선분의 첫 교차점을 새 꼭짓점으로 사용하고 닫힌 영역만 확정. 기존 꼭짓점 접촉·여러 교차·완료 시 마지막 선분·열린 꼬리 제외·24K 좌표/배율·실패 후 초안 보존과 자식/연쇄 삭제/Undo/Redo 회귀.
- [x] PASS: Core 190 + 앱 217 + Canvas 100 = 507개, 실패/skip 0. Release 전체 빌드 경고/오류 0, `scripts/Format-Code.ps1 -Verify` 통과. `artifacts/polygon-tests/*.trx`.
- [x] PASS: `artifacts/publish-polygon-drawing/TeachingToolWpf.exe`의 RTX 3060/DX12 debug HWND smoke exit 0. 실제 도형 선택·추가 버튼·교차점 자식 생성·정확한 Undo/Redo·OpenCV 마스크·그룹/삭제·8K 라이브 회귀. 화면 `artifacts/polygon-runtime-final/artifacts/polygon-crossing.png`와 `smoke.log`. [보관 증거](../docs/data/polygon-drawing-20261005.zip).
- [x] PASS: SOLID 5개/GRASP 9개 검토, 원칙 예외 없음. 기존 검사 성능 변경을 보존했다.
- [ ] NOT_RUN: 물리 마우스 전체 E2E 및 수만 꼭짓점 입력 지연/장시간 시험. HWND 메시지 smoke나 기존 사각형 성능 수치로 대체하지 않는다.

## 2026-10-04 형상 검사 속도와 소수점 입력 후속

기준: [ADR-024](../docs/decisions/0024-matching-throughput-and-decimal-input.md), [실측과 검증](../docs/matching-throughput-validation.md).

- [x] PASS: 숫자 바인딩 `0.`→`0` 재현 및 필드별 편집 문자열 보존. 패턴 3방식·일반 도형 숫자 회귀 20개. 실제 앱에서 문자별 `0.720` 입력과 Tab 후 유지 확인.
- [x] PASS: 방향 후보 정밀화의 실제 CPU 병렬화, 고정 배율 중복 계산 제거, 미사용 상위 NMS 생략. 알고리즘 169개 통과.
- [x] PASS: 원본 16 MiB+축소 4 MiB LRU, 행 변환과 복사 제거, 공간 순서 원본 읽기, 결과 원래 순서 복원. 병렬 crop 합 2 Mpx·단일 8 Mpx 한도, 장치 직렬화/취소/해제 집중 39개 통과.
- [x] PASS: 8K 256핀·24576×8192 768핀 전체 검사, 워밍업 1/측정 3회 CPU 4/CUDA 4에서 완전 검출·위치 1 px·변경 전 record 동등. 원본 읽기 768→512, 2982→1536회.
- [x] PASS: 최종 CPU 1 실측 8K 3130.67 ms/24K 9562.48 ms. 앱 기본 CPU 4는 2149.90/6532.94 ms, 기존 4253.36/16455.00 ms 대비 49.5%/60.3% 감소. CUDA 4는 2338.15/7190.75 ms로 CPU보다 느림. 3회 중앙값·합성 입력 한계 명시.
- [x] PASS: 전체 746(Core 182/Algorithm 169/Inspection 68/CUDA 27/Canvas 100/앱 200), skip 0. `Format-Code.ps1 -Verify` PASS, 빌드 경고 0/오류 0, diff 검사 PASS. 최종 포맷 로그 `artifacts/inspection-speed-format-verified.log`, 별도 게시물 `artifacts/publish-inspection-speed-verified`.
- [x] PASS: 최종 배포 DX12 debug 통합 검사 exit 0. CPU/CUDA 256개, 회전 패턴 2개, 도형 4종, 폴리곤·검색 점선·Undo/Redo·문자 입력, 렌더 오류 없음. `artifacts/inspection-speed-runtime-final/artifacts/inspection-smoke.log`. 첫 smoke의 접힌 고급 패널 탐색 실패는 테스트가 Expander를 열도록 수정한 뒤 통과했다.
- [x] PASS: 새 앱에서 핀 하나(159특징/3단계/37템플릿)를 학습하고 CPU 4로 8192×8192 전체 검사. 256개/3117.7 ms(읽기 1214.3 ms)/OK, 검출 사각형·검색 점선 확인. 단일 UI 실행과 반복 중앙값 2149.90 ms를 구별한다. 실제 OS 화면·접근성 기록 `artifacts/inspection-speed-user-state/final-inspection.jpg`, `.txt`. 앱은 검사 결과 표시 상태로 유지.

## 2026-10-04 플랫폼 독립 검사와 후보 축소 개선

기준: [승인된 작업 계획](portable-inspection-plan.md).

- [x] PASS: 공통 VisionImaging / VisionInspection net10.0 추출. 원본·취소·마스크·타일·예산·플랫폼 참조 경계 회귀. Canvas는 Core+VisionImaging만 프로젝트 참조하며 공급자 미참조.
- [x] PASS: 빠르게 선택지 제거, 과거 enum0은 기본으로 정규화. 같은40패턴 기본예산2048/64에서11→40검출, 중앙값347.1→342.5ms(워밍업1/측정5). 최소점수.72와감점1/예산/정밀화유지, 불완전경고유지. pose분산순서신규9+기존58=67집중PASS, 앱프리셋5회귀PASS.
- [x] PASS: 검사 DX12 제거, ILGPU1.5.3 CUDA 평활화+Sobel·공통CPU정규화. 실제RTX3060 22동등성/수명+5정리실패주입=27PASS. 최종pose·score·quality완전일치. 전체시간은 후속 측정 항목 참조.
- [x] PASS: JSON 레시피 CLI, 원본파일4방식실행(2/2/1/4검출), 부분옵션기본값보존·실패/취소/원자적출력/덮어쓰기보호. Linux x64 ELF/native cross-publish 및 고지포함 PASS, WPF/Windowsnative미포함. 실제Linux실행과구별.
- [x] PASS: 최종 전체687(Core182/Algorithm152/검사CLI46/CUDA27/Canvas100/앱180), skip0. `artifacts/portable-verified-tests.log` 및 TRX. 컨벤션Verify PASS, 빌드경고/오류0. 원칙예외없음.
- [x] PASS: 8K256핀6설정 각각워밍업1/측정3. CPU/CUDA pose·score·support·clutter완전일치, 정답매핑256개/1px통과. 호출전체중앙값 CPU4046.41/CUDA4084.50ms로 약0.9%느림. CPU기본유지, 가속달성주장하지않음. `docs/data/portable-cuda-benchmark-20261004.json`.
- [x] PASS: 별도게시물 `artifacts/publish-portable-inspection`, DX12 debug smoke exit0. 실제CPU/CUDA256핀·OpenCV256·회전기하2·도형4·폐폴리곤·검색점선·Undo/Redo, 렌더오류없음. `artifacts/portable-inspection-runtime/artifacts/inspection-smoke.log`.
- [x] PASS: 새앱직접실행·핀1개모델학습·CUDA모드전체검사256개/4424.6ms/OK. 새후보예산명칭·CUDA선택·실선검출위치·검색점선확인. 실제OS창원본스크린샷과접근성기록 `artifacts/portable-inspection-user-state/cuda-pins.jpg`, `.txt`. 단일UI실행시간은반복중앙값과구별. 새앱은결과표시상태로유지.
- [ ] Linux 실실행: 실행 환경 없음. Windows 통과나 Linux cross-publish를 실제 Linux PASS로 표시하지 않음.

## 2026-10-04 추가 윤곽 점수·실행 제어·일반 도형

기준: [계획](inspection-controls-plan.md), [ADR-022](../docs/decisions/0022-inspection-scoring-execution-and-primitives.md).

- [x] 점수 재현 RED9 FAIL/6 PASS → 집중56 PASS. 합성 대각선 핀 방향1.000/추가윤곽25.2%/최종.748, 깨끗한 핀1.000. 실제 마스크/회전/가림/정밀화/최종 최소점수·정렬·NMS 검증. 앱 두 방식 기본비중1, OpenCV식 유지.
- [x] 실행 프리셋/병렬도/시간 한도/방식별 설정과 GPU 실제 경로 구현. RTX3060 GPU14+fence4+중립계약5 PASS. 오류 시 무폴백, fence 완료 후 반환. 결과에실제adapter/처리량/시간 표시.
- [x] 원본8K 핀ROI 설정별워밍업1/측정3 PASS. 모든설정256/256, 1px 위치기준, CPU/GPU pose·점수동등, 최저점수약.79. 중앙값CPU균형4213.4/GPU4508.4ms. 속도프리셋효과는입력에의존하며가속달성으로표현하지않음. `docs/data/inspection-controls-20261004.json`.
- [x] 일반 도형 알고리즘 18개 / 앱 13개 집중 PASS. 회전 십자가와 타원·T/L/I·링 음성, 크기·잘림·원본 ROI·시간 한도·늦은 결과를 검증했다. 별도 패널·검사 도구 선택·샘플 버튼 연결. 실제 PNG 로더의 307,200개 원본 픽셀 일치와 4종 각 1개 검출 PASS.
- [x] 최종 Release 전체 **647개 PASS**(Core 182 / 앱 227 / Algorithm 138 / Canvas 100), skip 0. `artifacts/inspection-controls-verified-tests.log`, 프로젝트별 `inspection-controls-verified.trx`. 시간 초과 예외·원본 모델 검증·Signal 실패·정지 라이브 도구 전환 회귀 포함.
- [x] `pwsh -File scripts/Format-Code.ps1 -Verify` PASS, 빌드 경고·오류 0. `artifacts/inspection-controls-format-verified.log`. 별도 배포본 `artifacts/publish-inspection-controls` 생성. SOLID 5개·GRASP 9개 검토, 원칙 예외 없음.
- [x] 배포 DX12 debug smoke PASS(exit 0), 렌더 오류·D3D12 버퍼 경고 없음. CPU 256개 / GPU 256개, 일반 도형 4종, 검색 점선·닫힌 폴리곤·판정색·숫자 검증·검사 중 잠금·샘플 Undo/Redo를 확인했다. `artifacts/inspection-controls-runtime-verified/artifacts/inspection-smoke.log`. 초기 smoke에서 발견한 기본 힙 버퍼 초기 상태 경고와 BMP 샘플 디코드 실패를 수정했으며 실패 기록도 보존했다.
- [x] 새 배포 앱을 직접 실행하여 **도형 샘플 → 도형 검출** PASS. 640×480 PNG에서 4종 각 1개 / 78.9ms, 실제 닫힌 윤곽·위치·종류와 검색 점선 확인. OS 창 원본 스크린샷과 접근성 상태: `artifacts/inspection-controls-user-state/primitives.jpg`, `primitives.txt`. 새 앱을 검사 완료 상태로 유지하고 기존 사용자 창은 보존했다.
- [ ] 실촬영8K/24K검출률·실시간FPS·전체GPU검색·테두리도형/붙음/가림지원 NOT_RUN/미구현. 완료로표현하지않음.

## 2026-10-04 논문 형상 일부 검출과 원본 읽기 개선

기준: [ADR-021](../docs/decisions/0021-shape-output-and-original-tile-reuse.md).

- [x] 현재 사용자 앱 조사 PASS. 논문 모드/사용자 핀 ROI/점수 .72/출력16에서16개와 생략 경고. 같은 모델 상한1000→256개/5,611.7ms/읽기3,225.2ms/64창/OK. 후보 예산 누락은 이 입력에서 재현되지 않았음. `artifacts/paper-user-state` OS 화면·접근성 상태 보존.
- [x] 앱 형상 두 모드도 독립 기본256 적용, 라이브러리16 유지. 원본 타일16MiB LRU와 풀 버퍼 소유/반환 구현. 원본 명암·축소·점수·후보 조건 변경 없음. cache 회귀 RED6개 실패→focused PASS,32MiB 예산 변이2개 실패→즉시 원복 확인. `artifacts/paper-cache-*.log`.
- [x] Release 워밍업1/측정3 반복 PASS. 256/256 핀 일대일 검출, 최대 위치오차 .205px, 전후 모든 pose/점수/지지/정밀화 결과 정확 동일. 중앙값5,825.9→4,392.8ms(24.6% 감소), 타일1604→768회. pooled 버퍼로 일반 배열 캐시의 추가 약200MB 할당 제거. 프로세스 peak104,951,808→148,307,968bytes와 cache16MiB 한도를 구별. `docs/data/paper-pins-*.json`.
- [x] Release 전체561개 PASS(Core182/앱187/Canvas100/Algorithm92), skip0, 빌드 경고/오류0. `artifacts/paper-matching-tests.log`, 프로젝트별 `paper-matching-results.trx`. `Format-Code.ps1 -Verify` PASS(`paper-format-verify.log`). 독립 리뷰 SOLID5/GRASP9·캐시 풀 소유/반환 검토 PASS, 원칙 예외 없음.
- [x] 별도 배포본 `artifacts/publish-paper-matching/TeachingToolWpf.exe` RTX3060 Laptop/DX12 debug smoke PASS(exit0). 논문 핀16개 Warning→256개/4,206.0ms/64창/OK, 각16칩에서16개. 기존 OpenCV256/숫자검증/기하 품질/폴리곤·점선/판정색/Undo 회귀 및 렌더 오류없음. `artifacts/paper-matching-runtime/artifacts/inspection-smoke.log`. 실제 GPU 진단 수치는 단일 실행이며 반복 벤치마크와 구별.
- [x] 새 배포본 직접 실행→ROI 속성 입력→핀 학습→기본상한256 전체 검사 PASS. 실제 OS 창에서256개/5,005.5ms/읽기2,068.3ms/64창/OK 확인. `artifacts/paper-user-state/fixed-paper-256.jpg`, `fixed-paper-256.txt`. 이 단일 첫 실행 수치를 워밍업 반복 중앙값4.39초와 구별하며 새 앱은 검사 완료 상태로 유지했다.
- [ ] 실촬영8K/24K 검출률·지연·장시간 메모리/실시간 FPS NOT_RUN. 합성 핀 결과와 구별. 약4.4초 검사는 실시간 성능 달성이 아님.

## 2026-10-04 OpenCV 일부 검출과 세 방식 분리

기준: [ADR-020](../docs/decisions/0020-matching-modes-and-result-limits.md), [파라미터 안내](../docs/matching-parameters.md).

- [x] 실행 중 사용자 앱 원형 보존하여 조사 PASS. 8K 샘플/핀 107×130/점수 .72에서 최대 결과 수16 때문에 16개만 표시했고 OK로 보고한 결함 확인. 동일 모델 상한1000→256개/16창/10,169.4ms. 전후 OS 캡처 `artifacts/opencv-user-state`; 코드 수정 전 실측.
- [x] NMS 후 N+1 독립 결과 유무로 출력 생략 감지, 후보 예산과 별도 경고 및 개별 검출 Orange 표시. 정확상한·중복·여러 검색창·동시경고 회귀 PASS. 알고리즘91개 및 앱 판정15개 focused PASS.
- [x] 세 방식별 모델/설정, 추가 파라미터, 학습 dirty·숫자 검증·취소/늦은 결과 회귀 PASS. 논문 모드는 기하 자원 생성/평가 제외, 기하 모드만 별도 학습/품질 검증. 형상 일반/피라미드의 출력 상한 누락도 독립 리뷰로 찾아 N+1 진단과 경고를 통일.
- [x] Release 전체 **542개 PASS**(Core182 / 앱168 / Canvas100 / Algorithm92), skip0: `artifacts/matching-modes-tests.log`. 신규 테스트 준비의 잘못된 이미지 초기화 호출/ImmutableArray 참조 비교를 수정하고 정확한 이미지 교체·요소 비교로 검증. 재학습 차단 조건 제거 변이에서11개 FAIL→원복 후 모드21개 PASS: `matching-modes-mutation.log`, `matching-modes-restored.log`.
- [x] `pwsh -File scripts/Format-Code.ps1 -Verify` PASS, 빌드 경고/오류0. `artifacts/matching-modes-format.log`. 독립 리뷰의 파라미터/상태/취소/수명 검토, SOLID5/GRASP9 확인. 원칙 예외 없음.
- [x] 별도 배포본 `artifacts/publish-matching-modes/TeachingToolWpf.exe` 실제 RTX3060 Laptop/DX12 debug 검사 smoke PASS(exit0), 렌더 오류 없음. 사용자 핀 ROI 재현: cap16 Warning(10,500.0ms)→cap256 정확히256개 Pass(9,610.9ms), 모든16칩에서각16개. 단일 실행 수치이며 속도 개선 비교 아님. 숫자 abc/범위오류 차단·복구·방식전환, 기하 전용 설정 바인딩·검사 중 잠금, 기존 폴리곤·점선·4판정색 회귀 PASS: `artifacts/matching-modes-runtime/artifacts/inspection-smoke.log`.
- [x] 새 배포본 직접 실행→ROI 속성 입력→OpenCV 선택→핀1개 학습→전체8K 검사 PASS. 기본상한256에서256개/16창/9,496.1ms/OK 확인. 실제 OS 창 스크린샷과 접근성 텍스트 `artifacts/opencv-user-state/fixed-app-256.jpg`, `fixed-app-256.txt`, 세 방식 목록 `three-modes-window.jpg`. 수정 앱을 검사 완료 상태로 유지하고 기존 사용자 앱도 보존했다.
- [ ] 실촬영 8K/24K 검출률/시간·상용 동등성·모델/설정 영구 저장 NOT_RUN/후속. 합성 핀256개 성공으로 대체하지 않는다.

## 2026-10-04 기하 형상 매칭 1차 확장

기준: [ADR-019](../docs/decisions/0019-geometric-match-quality.md), [단계별 계획](geometric-matching-plan.md), [실측 보고](../docs/geometric-matching-validation.md). PatMax 공개 기능을 참고한 자체 C# 확장이며 Cognex 소스/SDK 포팅·동등 성능을 주장하지 않는다.

- [x] 최종 원본 pose의 선택 윤곽 특징 대응비율, 실제 에지 거리 RMS, 전체 학습 윤곽 기반 clutter와 선택형 합격 기준 구현 PASS. 정확한 마스크/기준각/배율/극성/가림 고정 분모/원본 crop/취소/8 Mpx·262,144점 제한 검증. 기본 방향 점수·기존 후보 제한 유지.
- [x] 오른쪽 기하 검증 패널, 후보 확대 4096/256 옵션, 결과 수치·미계산·측정 불가 구분, 형상/유휴 상태 제한, 조건 변경 무효화·늦은 결과 차단, 축소 후보 계산 생략·원본 기준 적용 PASS. 앱 신규 20개 회귀 포함.
- [x] 기존 기본 정책은 추가 전후 15/18이며 한계를 그대로 기록. 명시적 정밀 정책(후보4096/256, coverage .75, fit1px, clutter .75)에서 합성18/18 PASS. 최대 위치오차 .18028px/각도 .26862°/배율 .00625. 기본 후보 예산 경고 회귀2개 보존. 정밀 정책은 주변 획/복합에서 더 느리며 현장 성능과 구별.
- [x] 정밀화 거절 시 반환 pose와 다른 잔차/대응 개수 보고 수정. 공개 정밀화 재호출의 오래된 Geometry 유지도 독립 리뷰에서 발견해 RED 재현 후 null 무효화. ShapeMatcher는 최종 pose에서 다시 계산. SOLID 5/GRASP 9 검토, 원칙 예외 없음.
- [x] 전체 **491개 PASS**(Core182 / 앱123 / Canvas100 / Algorithm86), skip0. `artifacts/geometric-solution-tests.log`, 프로젝트별 `geometric-results.trx`. `Format-Code.ps1 -Verify` PASS(`artifacts/geometric-format-verify.log`), 빌드 경고/오류0, git diff --check PASS.
- [x] 새 배포본 `artifacts/publish-geometric/TeachingToolWpf.exe`의 RTX3060 Laptop / DX12 debug 검사 PASS(exit0). 실제 원본 학습→8K 칩16개, 회전 폴리곤·실선/검색점선, 두 패턴, OpenCV, 특징점, 기존 판정색·미검출 회귀 확인. 새 품질 옵션 실제 WPF 바인딩·검사 중 비활성·원본 품질 표시 PASS. `artifacts/geometric-runtime-final/artifacts/inspection-smoke.log`. 초기 진단은 펼친 Expander 레이아웃 갱신 전 요소 탐색으로 실패했고 절차 수정 후 재검증 PASS; 기준 완화 없음.
- [x] 두 패턴 품질 검사 91.9ms 단일 실행, 둘 다 특징160/160·clutter0, RMS0.000/0.328px. 회전 패턴은 정밀화 Rejected이므로 CHECK 유지. `artifacts/geometric-runtime-final/artifacts/inspection-geometric-quality-app.png`는 WPF+DX12 readback 진단 캡처이며 OS 창 스크린샷이 아님.
- [ ] 새 배포본 직접 조작·OS 창 스크린샷: NOT_RUN/중단. 새 앱 실행까지 진행했으나 사용자 Escape로 Computer Use가 중단되어 추가 조작/촬영을 멈췄다. 기존 앱 종료는 자동 승인 검토가 미저장 상태 손실 이유로 거절해 기존 `artifacts/publish`와 창을 보존하고 별도 배포했다. 기존 이미지·페이지를 최신 OS 캡처로 표현하지 않는다.
- [ ] 실촬영 8K/24K·독립 holdout 검출률·실시간 FPS·상용 비교 NOT_RUN. 비균일 배율/affine·비선형 변형·CAD 학습·모델/레시피 저장은 후속 계획이며 이번 1차 완료와 구별.


## 2026-10-04 검출 박스·폴리곤과 검색 점선

기준: [ADR-018](../docs/decisions/0018-inspection-boundaries.md). NMS 마스크 AABB/crop 사각형을 표시 경계로 재사용하는 원인 확인.

- [x] 학습 도형의 불변 스냅샷을 방식별 모델과 함께 보관. 회전 사각형·삼각형·원·오목 폴리곤의 실제 윤곽, 형상 검출 절대각/배율, OpenCV 고정 방향/크기 검증 PASS. 학습 후 ROI 편집·이미지 교체, 재학습 실패·취소·오래된 결과에서 기존 모델/도형 보존 PASS.
- [x] 검색 범위는 화면 DIP 기준 8/5 점선, 검출 윤곽은 닫힌 실선과 대비용 배경 획으로 구분. 짧은 번호/점수 라벨을 먼저 그려 인접 윤곽 가림 방지. 화면 밖 구간을 자른 뒤 대시를 만들며 꼭짓점·닫는 변·클리핑의 위상 유지. 배율/DPI·거대 화면 밖 선·오목 윤곽의 모든 변 회귀 PASS.
- [x] Release 빌드와 전체 **422개 PASS**(Core 182 / 앱 103 / Canvas 100 / Algorithm 37), skip 0. 근거: `artifacts/inspection-boundary-solution-tests.log`. 독립 읽기 전용 리뷰에서 이번 변경의 확정 결함 없음, SOLID 5/GRASP 9 확인. 원칙 예외 없음.
- [x] RTX 3060 Laptop / DX12 debug 실제 파일 학습·검사 smoke PASS(exit 0), 렌더 오류 없음. 8K 합성 칩 16개(검사 6072.7 ms), 기존 형상 2개/OpenCV 1개·특징점·선택 유지·미검출 회귀 포함. 근거: `artifacts/boundary-runtime/artifacts/inspection-smoke.log`. 해당 1회 수치를 현장 성능으로 확대하지 않음.
- [x] 640×400 합성 PNG의 오목 6각형 ROI를 실제로 학습하고 별도 검색 폴리곤 안에서 회전 패턴 1개 검출: X419.875/Y230.125, 20.156°, 1.2000배, 점수 .999, 60.9 ms. 알려진 정답 대비 각 꼭짓점 오차 1.5px 미만, GPU 닫힌 6개 변과 검색 대시/공백 89/31개 표본·18회 전환 확인. 정밀화 `Rejected`는 CHECK/Gold로 유지하며 OK로 바꾸지 않음.
- [x] `artifacts/boundary-runtime/artifacts/inspection-polygon-boundaries-app.png` 직접 확인. 검색 점선 사이 기존 파란 선택 ROI 선/핸들을 보존하며, 검출된 학습 폴리곤의 회전·오목 꼭짓점은 Gold 실선으로 표시. 이 PNG는 실제 WPF UI와 DX12 readback을 합친 **진단 캡처**이며 OS 앱 창 스크린샷이 아님.
- [x] `pwsh -File scripts/Format-Code.ps1 -Verify` PASS, 빌드 경고/오류 0. `artifacts/inspection-boundary-format.log`, `inspection-boundary-publish.log`. `git diff --check` PASS.
- [x] 수정 배포본 `artifacts/publish/TeachingToolWpf.exe`를 직접 실행해 마우스로 칩 ROI 생성→학습→검사. 8K 합성 칩 16개/OK, 각 칩의 실선 박스·짧은 라벨과 전체 이미지 검색 점선 직접 확인. OS 앱 창 스크린샷: `artifacts/actual-app-screenshot-20261004/inspection-boundaries-window.jpg`. 앱을 검사 완료 상태로 유지.

## 2026-10-04 기본 칩 학습·검사 후속

기준: [ADR-017](../docs/decisions/0017-default-chip-inspection.md). 기본 8K의 칩 내부/테두리 ROI를 구분해 재현했다. 아래 검증은 이전 중앙 교차선 512 ROI 시험과 다른 입력이다.

- [x] 평평한 512 ROI는 윤곽 부족 학습 실패를 재현. 학습 기준을 낮추지 않고 ROI 테두리·핀 포함 안내를 패널 상단과 캔버스 경고에 연결. 이전 모델 보존 여부도 명시. 처리 중 GPU 배너·진행 막대·취소·입력 무효화 안내 PASS.
- [x] 원본 후보 정밀화의 전체 방향 응답맵 재생성 제거, gradient 피라미드 연속 pose 최적화/point-to-line 유지. 큰 원본·비정수 위치/회전/배율·작은 원형 검색 영역 회귀 PASS. 수렴 초기 오차 범위의 전 입력 동등성은 주장하지 않음.
- [x] 동일 8192²/1600×1120 칩 ROI 3회 중앙값 **4567.4→3460.7ms**, 약 24% 감소. 각각 16개/불완전 false. 원시 JSON `docs/data/chip-before-8192.json`, `chip-after-8192.json`. UI native ROI 첫 실행 약 5.3초는 별도 기록.
- [x] **패턴 샘플** 상단 버튼, 자체 제작 기존 640×400 PNG 자산 배포 동봉. 실제 버튼 파일 열기·ROI 초기화·모델 보존·Undo/Redo PASS. 오른쪽 도형 검출을 위한 배율 검색/최대 1.2 이상 안내.
- [x] RTX 3060 Laptop/DX12 debug: 실제 HWND ROI 그리기→학습→16개 위치/오차 1px 미만·GPU 윤곽 확인. 평평한 ROI 실패·처리 중 캡처, 전용 샘플/기존 형상·OpenCV·특징점/네 색/NG smoke PASS. `artifacts/chip-ui/artifacts/inspection-smoke.log` 및 PNG.
- [x] 독립 읽기 전용 리뷰의 샘플 최대 배율 안내 누락 수정. IsBusy 조건 반전 시 회귀 테스트 FAIL 확인 후 원복. `artifacts/chip-mutation.log`. SOLID 5/GRASP 9 검토, 원칙 예외 없음.
- [x] 최종 전체 **389개 PASS**(Core 182 / 앱 78 / Canvas 92 / Algorithm 37), skip 0. `artifacts/chip-all-tests.log`, 각 TestResults/chip-results.trx. `pwsh -File scripts/Format-Code.ps1 -Verify` PASS(`artifacts/chip-format-verify.log`). 빌드 경고/오류 0, git diff --check PASS.
- [x] 최종 배포본 검사 smoke PASS(exit 0): `artifacts/chip-publish-runtime/artifacts/inspection-smoke.log`, native 칩 ROI 16개/약 5.2초, 패턴 샘플 자산/Undo/Redo/두 pose/OpenCV/색/특징점 확인. 기존 앱 smoke도 PASS(exit 0): `artifacts/chip-regression-runtime/artifacts/smoke.log`, 명시적 계층·도형·이력·필터·미니맵·8K 라이브 보존. 배포 경로 `artifacts/publish/TeachingToolWpf.exe`, 자산 SHA-256 원본/배포 일치.
- [ ] 현장 영상 8K/24K 정확도/속도, 가림·저대비 변화 통계, 실시간 검사 FPS: NOT_RUN. 기본 합성 이미지 성공과 구별.

## 2026-10-04 OpenCV 기본 템플릿·큰 ROI 검색·논문 재검토

기준: [ADR-016](../docs/decisions/0016-template-matching-and-pyramid-search.md), [성능·검증](../docs/matching-performance-2026-10-04.md). 사용자 재현은 ROI 하나 추가→학습→검사(크기 512 이상 추정)이며 기본 8K 합성 샘플로 확인했다.

- [x] OpenCV CCOEFF_NORMED 방식 선택, 별도 모델·원본 crop/도형 mask·검출 위치/점수/판정 표시. 고정 각도/배율과 특징점 미지원 명시. Mat은 알고리즘 Imaging 내부에 격리. 전환/취소/세대 변경의 오래된 결과 제거와 모델 보존 PASS.
- [x] 기존 원본 반복 전처리 구조를 원본 타일 검사 피라미드→후보 주변 원본 정밀화로 변경. 특징 분산·반경 기반 각도/배율 샘플·피라미드 단계별 자세 보정·정확한 검색 ROI·SIMD/scalar 동등성 PASS. 최종 동일 자세 중복은 값이 완전히 일치할 때만 제거.
- [x] 동일 8192²/512 ROI 전체 검색 3회: 이전 중앙값 30,830ms→최종 3,258ms, 약 9.46배. 모두 9개/잘림 없음, 원본 중심 위치 유지. OpenCV 중앙값 2,807ms/9개. 768 ROI 5,004ms, 기본 크기 2621×1966 ROI 5,945ms는 각 1회. 원시 JSON을 docs/data에 보존. 현장 검출률 근거로 사용하지 않음.
- [x] P1 TPAMI 2012와 Steger ISPRS 2002 원문 대조. 2001 제목/2002 링크 혼용 수정. RGB 방향 채택, signed score, 조기 종료, 4D 보간, 정밀화 법선/가중치의 미구현·차이를 명시. 논문 전체 재현/상용 동등성으로 표현하지 않음.
- [x] 전체 **385개 PASS**(Core 182 / 앱 76 / Canvas 92 / Algorithm 35), skip 0. artifacts/matching-all-tests.log와 각 TestResults/matching-results.trx. 큰 패턴의 비정수 이동·회전·배율, 지름 2px 원 검색, 원본 타일 축소, OpenCV mask/NaN/stride/cancel/NMS 경계 포함.
- [x] 독립 리뷰 4건 수정: 내부 seed 잘림 경고, OpenCV 전역 NMS 1회, 작은 뷰의 원본 전체 복사 방지, 초기 pose 유효성. 후속 읽기 전용 리뷰에서 추가 확정 결함 없음. NMS 버그를 임시 재주입하면 새 테스트 FAIL(2개 예상/1개 실제), 복원 후 PASS. artifacts/matching-mutation.log.
- [x] 최종 `pwsh -File scripts/Format-Code.ps1 -Verify` PASS. artifacts/matching-format-verify.log. Mat 공개/앱 유출 없음, SOLID 5/GRASP 9 검토, 승인되지 않은 예외 없음.
- [x] 최종 빌드 GPU 검사 smoke PASS: 실제 PNG reader·WPF 방식 선택/버튼·두 형상 위치·OpenCV 1개·특징점 토글·선택 유지·네 색·실제 미검출. RTX 3060 Laptop/DX12 debug, 렌더 오류 없음. artifacts/matching-final-runtime/artifacts/inspection-smoke.log. 배율 상한의 두 번째 형상은 정밀화 Rejected로 사용자 확인 표시를 유지했으며 정상으로 숨기지 않음.
- [x] 배포본 검사 smoke와 기존 앱 smoke 모두 PASS, exit 0. artifacts/matching-publish-runtime/artifacts/inspection-smoke.log, artifacts/matching-regression-runtime/artifacts/smoke.log. 명시적 부모/자식·이력·도형·필터·미니맵·8K 라이브 회귀 확인. 배포 경로 artifacts/publish/TeachingToolWpf.exe, docs/의 실측 JSON·논문 대조·라이선스 동봉.
- [ ] 현장 촬영 영상의 8K/24K 검출률·시간, 실시간 검사 FPS, 상용 matcher 비교: NOT_RUN. 8K/24K 표시 검증과 구별.

## 2026-10-04 검사 위치·판정색·특징점 표시

기준: [ADR-015](../docs/decisions/0015-inspection-result-display.md). 아래는 기존 패턴 구현 이후 추가 검증이며 당시 NOT_RUN 기록과 구분한다.

- [x] 전체 이미지 검사 결과가 ROI 선택 해제 때 사라지는 버그: 테스트 FAIL 재현 → 수정 PASS. 검색 입력/세대 변경 시에는 기존·늦은 결과를 폐기한다. 새 실행/오류/취소에 이전 성공 위치가 남지 않도록 수정.
- [x] 정상 LimeGreen / 불량 Tomato / 경고 Orange / 사용자 확인 Gold. 불완전 검색·오류·취소와 제품 불량을 구분하고 정밀화/회전각 불확실성을 사용자 확인으로 표시. 배너·검색 경계·기준점·방향·검출 경계·원본 좌표/점수 연결.
- [x] 검사 위치/특징점 표시 옵션과 검출 위치 화면 맞춤 버튼. 모델 특징점을 검출 pose로 변환하며 재검사/문서/이력을 변경하지 않음.
- [x] 실제 PNG 파일 640×400을 UI 명령으로 학습/검색하고 RTX 3060 Laptop DX12 debug에서 두 패턴 검출. X160/Y160/0°, X420.051/Y230.030/19.961°/scale1.1991. 원본 파일 reader, WPF 체크박스·버튼 바인딩, 선택 후 결과 유지, 화면 맞춤, 실제 빈 검색 영역 NG 확인. artifacts/inspection-runtime/artifacts/inspection-smoke.log 및 inspection-app.png. 합성 생성 PNG이며 현장 데이터가 아님.
- [x] 동일 캡처 조건에서 특징점 off/on 판정색 픽셀 11,805→15,181. 네 색은 실제 검출 형상에 지정색을 주입해 GPU 출력 검증(Pass 12,303 / Fail 12,375 / Warning 12,364 / Review 12,410 pixels). 경고·사용자 확인의 판정 조건은 별도 단위 테스트로 검증. 주입 화면을 실제 불량/경고 검출 결과로 표현하지 않음.
- [x] 전체 회귀 **371개 PASS**(Core 182/앱 72/Canvas 92/Algorithm 25), skip 0. artifacts/inspection-all-tests.log 및 프로젝트별 TestResults/inspection-results.trx.
- [x] 코드 규칙 적용·`pwsh -File scripts/Format-Code.ps1 -Verify`: PASS. Release 빌드 경고/오류 0. artifacts/inspection-format-verify.log, inspection-build.log. git diff --check PASS.
- [x] 변이 검증: 검색 불완전 조건 반전 시 5개 중 4개 FAIL. 즉시 원복 후 위 전체 회귀 PASS. artifacts/inspection-mutation.log. 독립 읽기 전용 코드 리뷰: 확정적 수정 필요 사항 없음.
- [x] 배포 실행 파일 검사 smoke PASS, exit 0. artifacts/inspection-publish-runtime/artifacts/inspection-smoke.log 및 inspection-app.png. 네 GPU 색/특징점/실제 검출·미검출 재확인.
- [x] 배포 실행 파일 기존 전체 smoke도 PASS, exit 0. 명시적 부모/자식·도형·이력·필터·미니맵·8K 라이브 시작/정지 회귀 확인. artifacts/inspection-regression-runtime/artifacts/smoke.log.
- [ ] 현장 촬영 데이터 정확도, 치수/공차/기대 개수 판정, 8K/24K 매칭 시간·메모리, 최대 결과 수 GPU 성능: NOT_RUN/미구현. 기존 8K 표시 smoke와 구별.

## 2026-10-04 프로젝트 정리·VisionCanvas·명시적 ROI 계층

기준: [ADR-014](../docs/decisions/0014-canvas-and-explicit-hierarchy.md). 이 항목은 이전 자동 포함·재부모화 요구를 대체하며 기존 실측 기록을 소급 변경하지 않는다.

- [x] 앱을 src/TeachingToolWpf로 이동, src/VisionCanvas와 tests/VisionCanvas.Tests 분리. 솔루션 src/tests/benchmarks 그룹, 참조·XAML assembly·셰이더·배포 경로 갱신. 기존 미커밋 검사 기능 보존.
- [x] 부모/자식 별도 추가·그리기, 겹침 자동 계층 제거. 자식의 부모 밖 이동에도 관계 유지. 복사 ID 매핑·선택 트리만 그룹화·중첩 해제·연쇄 삭제·대량 편집/이력 보존.
- [x] 이동 단계 331개 PASS, 변경 후 전체 339개 PASS(Core 182/앱 45/Canvas 87/Algorithm 25), skip 0. Release 빌드 경고/오류 0. 로그: artifacts/canvas-hierarchy-tests.log, artifacts/canvas-build.log.
- [x] RTX 3060/DX12 debug 실행에서 새 버튼·그리기·이력·겹침 독립·기존 도형/필터/미니맵/8K 라이브 확인. artifacts/canvas-hierarchy-runtime/artifacts/smoke.log 및 PNG.
- [x] `pwsh -File scripts/Format-Code.ps1 -Verify`: PASS. 분리 컨트롤의 Debug 참조 어셈블리를 먼저 준비해 WPF 디자인타임 로딩 경고도 해결했다.
- [x] 배포 산출물 smoke: PASS, 종료 코드 0. RTX 3060 Laptop / driver 581.83 / 6144 MiB / 실행 budget 5.1 GiB. artifacts/canvas-publish-runtime/artifacts/smoke.log, exit-code.txt. 셰이더·네이티브 필터·문서/고지 동봉 확인.
- [x] 변이 검증: AddChild의 부모 연결을 임시 제거하면 신규 테스트 5개 실패. 원상복원 후 전체 339개 PASS. artifacts/hierarchy-mutation.log. [검증 요약 JSON](../docs/data/canvas-hierarchy-validation-2026-10-04.json).
- [ ] 물리 포인터 전체 E2E, 새 정책의 24K 실파일/대량 ROI 부하 성능 재측정: NOT_RUN.
## 2026-10-04 VisionAlgorithm 고전적 2D 형상 매칭

기준: [구현 계획](pattern-matching-plan.md), [ADR-013](../docs/decisions/0013-vision-algorithm-and-inspection.md), [검증 보고](../docs/shape-matching-validation.md). 딥러닝 제외 확정. 주력은 LINE-2D 계열 후보 검색 → 연속 방향 검증 → robust point-to-line 정밀화 → NMS 결합이다. 이전 NCC 중심 계획을 대체하며, 아래 상태는 기존의 미완료 UI/GPU/대용량 작업 상태를 덮지 않는다.

검증 표기: `PASS`는 실행 근거 있음, `FAIL`은 실행했으나 실패, `NOT_RUN`은 실행하지 않음, `BLOCKED`는 환경/권한에 막힘이다. 합성 fixture와 현장 품질을 구분한다.

- [x] 프로젝트 경계: `src/VisionAlgorithm`, 앱 명시적 참조, OpenSrc/Cognex/Halcon 폴더. algorithm 프로젝트는 WPF/Core/OpenCV/native SDK 참조가 없다.
- [x] 주력 엔진: 불변 모델, ROI mask/기준점, 회전·균일 배율, 제한 후보, 다중 대상 NMS, polarity/continuous score, robust subpixel point-to-line, 취소/diagnostics.
- [x] 앱 연결 코드: 원본 타일/프레임 reader, 기존 ROI adapter, 오른쪽 학습/검사 패널, async/stale 결과 폐기, DX12 read-only markers.
- [x] 정확성 수정: 결과 각도는 이미지 기준 절대각이고 검색 범위는 학습 ROI 기준 편차. NMS/마커 경계는 희소 gradient 점 대신 학습 mask의 전체 pixel-cell bounds.
- [x] `dotnet test TeachingToolWpf.slnx -c Release --no-build --no-restore`: 331/331 PASS(Core 174, Imaging/WPF 132, VisionAlgorithm 25), skip 0.
- [x] 주력 경로와 E1/E2/E3 비교 benchmark 실행, 종료 코드 0; 입력 641×385 합성 positive 3개/상수 negative. E1 2/3, E2 3/3, E3 BOLD 3/3 (비교 위치 허용 10 px); 원시 결과는 [JSON](../docs/data/shape-matching-benchmark-2026-10-04.json).
- [x] `pwsh -File scripts/Format-Code.ps1 -Verify`: PASS, exit 0.
- [x] `dotnet build TeachingToolWpf.slnx -c Release --no-restore`: PASS, 경고/오류 0. Windows SDK 메타데이터 읽기 권한이 필요했다.
- [x] WPF 통합 테스트: 132/132 PASS. 추가한 matcher 연계 테스트 3개 포함.
- [x] `TeachingToolWpf.exe --smoke --gpu-debug`: PASS, exit 0. 기존 DX12/ROI 앱 경로 확인이며 실제 패턴 패널을 클릭한 smoke는 아니다.
- [ ] 패턴 패널의 수동 클릭·실제 ROI/타일 원본 검사는 CUA native 창 입력 미제공으로 NOT_RUN. RTX adapter 정보 측정, 8K/24K 실파일 정확성·시간·메모리도 NOT_RUN. 합성 641×385 결과를 실물 달성으로 확대하지 않는다.
- [x] E1 directional chamfer, E2 gray-gradient fusion, E3 BOLD식 CPU 비교기를 OpenSrc/Comparisons에 분리하고 같은 합성 데이터로 측정했다. E1/FDCM 원문 충실 구현, E2 Winograd FNM/가속, E3 BOLD 원문 detector와 전체 매칭 절차의 재현은 미완료다. 실측 오차·시간·메모리와 구현 차이는 [비교 보고](../docs/shape-matching-validation.md)에 기록했다.
- [ ] 알고리즘 자체의 원본 코드 포팅/원본 출력 비교 및 HALCON/Cognex 비교: 미수행. `meiqua/shape_based_matching` commit `3df935f8e4b8c8a8ed72984211543e1b7696452e` (BSD-2-Clause), OpenFDCM commit `5c6c1d422afc8010edf6eeb149329f353b2e77ce` (MIT)를 참조/감사했으나 코드는 복사하지 않았다.

이번 작업 검증의 상세 측정, 오차, 제한과 설계 리뷰는 [shape matching 검증 보고](../docs/shape-matching-validation.md)에 기록한다. 테스트를 생략하거나 기준을 완화하여 완료 표시하지 않는다.

## 2026-10-03 Microsoft C# 코드 컨벤션

- [x] C1: 앱·Core·테스트·벤치마크의 Allman 중괄호, 4칸 공백, 한 문장/선언씩 배치, 명확한 수식 괄호, `_camelCase`/`s_camelCase`/PascalCase 이름과 참조를 정리했다.
- [x] C2: AGENTS.md와 [상세 규칙](../docs/csharp-code-conventions.md)에 지속 지침을 추가하고 `.editorconfig`, `scripts/Format-Code.ps1` 적용/읽기 전용 검사 명령을 제공했다. SOLID 5개·GRASP 9개 검토, 원칙 예외 없음.
- [x] C3: Release 경고/오류 0, Core 174 + Imaging/WPF 129 통과, 최종 컨벤션 검사 통과. 원본 C# 174개 파일의 리터럴·주석 내용 보존 확인. 최종 publish의 RTX 3060/DX12 debug smoke 종료 0으로 도형/필터/계층/Undo/Redo 확인. [검증 기록](../docs/data/csharp-style-validation.json).

이번 스타일 정리에서는 성능을 새로 측정하지 않았다. 기존 성능 목표와 미검증 항목은 그대로 유지한다.

## 2026-10-03 사각형·원·삼각형·폴리곤 ROI

- [x] D1: 공통 RoiGeometry + 불변 도형 전략, 실제 윤곽 포함/BVH/회전 경계, 원 지름 제약과 오목·자기 교차 검증. 기존 사각형 전제 제거.
- [x] D2: 도형 선택/즉시 추가/드래그/폴리곤 클릭·완료·점 취소·Esc. GPU 도형 윤곽과 투명 필터 마스크, 복사/그룹/연쇄 삭제/Undo/Redo 보존.
- [x] D3: Core 174 + Imaging/WPF 129, Release 경고/오류 0. 실제 RTX 3060/DX12 debug 및 최종 publish 폴더 smoke 종료 0과 캡처 확인. [설계·14개 원칙 검토·검증](../docs/decisions/0012-roi-shapes.md).
- [ ] D4: 혼합 도형 수백만 개/큰 점 수 폴리곤의 정식 성능 및 물리 포인터 전체 E2E. 개별 꼭짓점 편집·타원·복합 도형은 후속 범위.

## 2026-10-02 코드 가독성 정리

- [x] H1: Win32 입력의 숫자/좌표/캡처 규칙을 이름 있는 경계로 정리. 기존 입력 동작 유지.
- [x] H2: ROI GPU 전달 구조의 필드·배치·바인딩 명명, 캐시/업로드 흐름 정리. 기존 메모리 배치와 반복 업로드 유지.
- [x] H3: 편집 준비/확정/취소/Undo·Redo와 렌더 프레임 수명을 읽기 쉬운 단계로 정리. 한국어 코드 읽기 안내 및 지속 규칙 추가.
- [x] H4: Core 158 + Imaging/WPF 123, 총 281개 테스트와 Release 빌드 경고/오류 0. RTX debug smoke와 기존 PNG 3개 완전 일치. 20만 평면/PCB 전후 12회+PCB 교차 6회에서 정확한 Undo/Redo·반복 업로드 0 B, 할당 유지 확인. [검증 및 원칙 검토](../docs/readability-validation.md).

## 2026-10-02 계층별 ROI 색상과 두께

- [x] S1: 부모·자식·기본의 독립적인 불변 색상/두께 계약 및 유효 값 검증. 기본 부모 핑크, 자식/기본 기존색.
- [x] S2: 실제 계층의 역할 변경·선택/핸들·GPU 캐시·Undo/Redo와 연동. 오른쪽 설정 패널에 역할별 두께 적용/초기화.
- [x] S3: Core 158 + Imaging/WPF 97 통과. 실제 RTX/DX12 debug·150% DPI에서 설정 패널 입력/적용·색상/두께·자식 이탈/복원 확인. 20만 개 평면/PCB 회전에서 반복 ROI 업로드 0 B, 정확한 Undo/Redo 확인. Release 배포와 문서 갱신. [설계](../docs/decisions/0011-roi-role-styles.md), [검증](../docs/roi-style-validation.md).

## 2026-10-02 대량 ROI 회전의 반복 작업 제거

- [x] G1: 기존 Release의 4만/20만 ROI 회전과 이미지 경계 회전을 재현. Move마다 전체 형상 복제·경계 탐색 할당이 UI를 막는 병목을 측정했다.
- [x] G2: 불변 선택 + 공통 강체 변환, DX12 보관 instance의 셰이더 회전, 최초 경계 이탈 각도 캐시, 전체 강체 확정의 BVH refit 구현. 속성·핸들·부모 태그·OpenCV가 같은 미리보기 형상을 사용한다.
- [x] G3: 최초 자손/부분 선택 준비를 worker로 이동하고 최신 요청 하나만 유지. 취소 후 재드래그도 준비 worker를 직렬화하며 Mouse Up은 기존 원자적 확정과 한 번의 Undo/Redo를 사용한다.
- [x] G4: Core 158 + Imaging/WPF 69 통과. 최종 Release 실제 RTX/DX12 debug smoke(회전 버튼·OpenCV 회전 미리보기·취소·이력·미니맵·라이브) 종료 0. 4만/20만/40만, PCB 부모 선택 3회, 부분 선택, 경계, logical CPU 1개, debug 등 10개 회전 시험에서 정확한 Undo/Redo 확인. [실측](../docs/rotation-performance.md), [원칙 14개 검토](../docs/decisions/0010-retained-rigid-rotation.md), [원시 자료](../docs/data/rotation-measurements.zip).
- [ ] G5: 최초 대규모 준비가 끝나기 전 표시 지연, 큰 확정/이력의 GC·UI 꼬리 지연, 실제 저사양 GPU 및 현장 PCB+8K 라이브 동시 부하. 반복 입력 개선을 모든 구간 무지연 달성으로 표현하지 않는다.

## 2026-10-02 CPU 제약 환경의 편집 응답성

- [x] L1: `fb97900`와 최종판의 32,940개 평면/PCB 분산 선택을 logical CPU 1개 조건에서 각 3회 비교. 중앙값 약 3.4/3.8배 개선, UI 할당 약 69–71% 감소.
- [x] L2: 대량 선택/이동 확정/삭제/붙여넣기/이력의 불변 준비·검증·원자적 게시, 단일 pending, 처리 중 탐색·Esc, UI 목록 슬롯/Reset 일괄 처리, worker 우선순위 제한 구현.
- [x] L3: Core 117 + Imaging/WPF 46, 변이 검증, Release 경고/오류 0, 최종 publish RTX/DX12 debug smoke, 300→19,200 PCB 복제, 32,940개 반복/10만 개 정확성 및 입력 응답 검증. [실측·한계](../docs/low-spec-performance.md), [14개 원칙 검토](../docs/decisions/0009-responsive-bulk-editing.md).
- [ ] L4: 일반 복사·생성·그룹화·필터 등록·resize preview 및 큰 인덱스 초기 구축, GC/스케줄링 꼬리 지연. 회전은 G1–G5에서 별도 개선·검증한다. 실제 저사양 CPU/GPU·현장 PCB+8K 라이브 장시간 시험. 단일 코어에서 10만 이동 완료 약 0.97–1.54초/최대 UI 간격 139ms가 남아 있어 무지연 완료로 표시하지 않는다.

## 2026-10-02 반복 렌더·입력 비용 제거

- [x] R1: 1만/32,940개 평면·PCB·중첩에서 UI/렌더 단계 시간·할당·업로드 기준 측정. 경계 드래그의 40회 전체 복제와 매 프레임 CPU 윤곽/태그 생성 확인.
- [x] R2: DX12 instance block 재사용·선택 facts·정확한 BVH hit·화면 밀도 태그 및 생성 예산·불변 이동 preview 적용. 필터/속성/이름표도 표시 위치를 공유하며 이력·정확성 유지.
- [x] R3: 32,940개 pan/wide/zoom 각 3회, 10만/100만 pan, 이동 확정/Undo/Redo/취소, Core 68 + Imaging/WPF 38, RTX/DX12 debug 캡처·150% DPI·기존 미니맵/필터 smoke 검증. [실측과 검토](../docs/retained-roi-performance.md). B1 정식 장시간 수용 시험 및 물리 포인터 전체 E2E와 구분.
- [ ] R4: 변형 확정/목록/일부 전체 명령은 L1–L3에서 개선. 회전 반복 형상 경로는 G1–G5로 추적한다. resize, 남은 명령과 실제 PCB+8K 라이브 지속 부하는 L4에서 계속 추적.

## 2026-10-02 대량 복제 프리징과 미니맵

- [x] C2: 7,000개 마퀴 선택 → 복사/붙여넣기 재현. 중첩 계층 검색과 필터 GPU 슬롯 고갈 수정, 비동기 준비/취소, 새 ID·계층·Undo/Redo 검증. 300개부터 평면/PCB/중첩 각각 19,200개까지 반복 복제·UI heartbeat 통과. [실측 및 남은 지연](../docs/clipboard-minimap-validation.md).
- [x] N1: 전체 이미지/현재 가시 영역의 GPU 미니맵, 클릭/드래그 탐색, 확대·현재 DPI·이미지 교체/라이브 연동. 혼합 DPI 모니터 이동은 기존 미검증 범위 유지.
- [x] N2: Core 60 + Imaging/WPF 32 통과, Release 경고/오류 0. 최종 배포본 RTX/DX12 debug smoke 종료 0, 8K/24K/라이브 미니맵 캡처 확인, 문서·14개 원칙 검토·배포 갱신. 물리 포인터 전체 E2E 미검증.

## 2026-10-02 대량 PCB ROI 성능 개선

- [x] P1: 1,550/3,550개 평면/PCB 계층의 선택·hover·click·pan·drag UI 지연/할당과 렌더 지표 기준 측정.
- [x] P2: 공유 BVH/변경 ID 이력/목록 인덱스와 비활성 속성 표시로 순회·TSF 비용 제거. 이전 스냅샷·큰 이동·수명·Undo 회귀 검증.
- [x] P3: 10만/100만/200만 고정 ROI 실측. PCB 부품+8패드와 평면, 전체 요약 보기/확대 편집을 분리. [측정과 한계](../docs/roi-scalability.md).
- [x] P4: 최종 Release 경고/오류 0, Core 52 + Imaging/WPF 29 통과. 최종 publish RTX/DX12 debug smoke 종료 0, 원칙 14개 검토·재현 명령·반복 전후 수치 보존.
- [ ] 수백만 ROI의 초기 생성/계층 구축과 전체 선택·삭제·복원 중 UI 점유 제거. 200만 PCB 구축 약 49초, 백만 전체 명령 최대 6.84초가 남아 있음. 실제 PCB 데이터와 8K 라이브 동시 지속 부하도 미검증.

## 2026-10-02 필터 등록·복제·메모리 후속

- [x] M1: 실제 8K RGBA / 24K Gray16 파일을 각 60회 교체하는 메모리 진단과 수정 전 기준 측정. Private Bytes/managed heap/handles/이력 크기 및 DX12 오류 기록.
- [x] F1: 필터 설정을 ROI 값으로 저장하고 Undo/Redo에 포함. 등록된 ROI 재선택 시 자동 표시, 미등록 ROI는 원본, 다중 선택은 각각의 설정 적용. 숫자는 LostFocus 확정, 혼합 선택 등록 해제 지원.
- [x] C1: 부모/자손 포함 복사·붙여넣기, 새 ID·이미지 경계·이력 원자성, 버튼/Ctrl+C/Ctrl+V/Del 및 텍스트 편집 단축키 보존. Core와 실제 WPF/DX12 명령·HWND 메시지 smoke 검증.
- [x] M2: 같은 조건 120회 및 600회 교체 재측정. 지속 누수 징후 미관찰. 타일 큐 backpressure/100회 소스 교체의 정확히 한 번 해제 회귀 검증. 상세 수치와 한계는 [메모리 보고서](../docs/memory-replacement-validation.md).
- [x] 문서·설계 원칙 14개 검토, Release 경고/오류 0, Core 44 + Imaging 26 통과, 최종 publish DX12 debug smoke 종료 코드 0. 물리 입력 전체 E2E 및 10분 이상 부하는 미검증으로 유지.

기준: [구현 계획](plan.md), [작업 지침](../AGENTS.md). 상태: 2026-10-02 샘플 기능 구현 및 실제 RTX 검증. 체크된 항목만 해당 수용 범위 검증을 완료했고, 남은 항목은 아래에 구체적으로 기록한다. 실행/제약은 [README](../README.md), 근거는 [런타임 검증](../docs/runtime-validation.md)과 [성능 기록](../docs/performance-baseline.md)을 따른다.

각 단계의 예상 변경 파일은 대표 경로다. 한 단계가 약 5개 파일 또는 한 작업 세션을 넘는다면 구현 전에 하위 작업으로 나눈다. 테스트 파일과 문서도 변경 범위에 포함한다. 상위 의존 작업이 완료되기 전에 해당 기능 완료를 선언하지 않는다.

총 21개 작업이다. T04A에서 Undo/Redo 기반, T08A에서 부모 생성형 그룹화, T10A에서 뷰포트 밖 수동 조작 버튼을 구현한다. 모든 작업의 공통 완료 조건은 클린 코드·적절한 디자인 패턴·SOLID 5개·GRASP 9개 검토다. 성능 때문에 원칙 예외가 불가피하면 측정 근거와 대안을 제시하고 사용자 명시적 승인 전에는 해당 변경을 적용하지 않는다. [예외 절차](../AGENTS.md#필수-설계-원칙과-성능-예외-절차)를 따른다.

## 공통 검증 명령

아래 축약명은 각 작업의 검증 항목에서 사용한다. 테스트/벤치마크 프로젝트를 만든 이후에만 실행할 수 있다.

```powershell
# B: 솔루션 빌드 (패키지 변경 직후에는 먼저 restore)
dotnet restore .\TeachingToolWpf.slnx
dotnet build .\TeachingToolWpf.slnx -c Release --no-restore

# TC: Core 자동 테스트
dotnet test .\tests\TeachingToolWpf.Core.Tests\TeachingToolWpf.Core.Tests.csproj -c Release

# TI: 이미지/버퍼 자동 테스트
dotnet test .\tests\TeachingToolWpf.Imaging.Tests\TeachingToolWpf.Imaging.Tests.csproj -c Release

# RUN: 실제 WPF/GPU 동작 확인
dotnet run --project .\TeachingToolWpf.csproj -c Release

# BM: CPU 측정 도구 (GPU 및 표시 성능은 실제 앱에서 별도 측정)
dotnet run --project .\benchmarks\TeachingToolWpf.Benchmarks\TeachingToolWpf.Benchmarks.csproj -c Release
```

## T01 — Core와 테스트의 프로젝트 경계

- [x] 완료
- 목적: WPF와 무관한 모델/좌표 테스트를 추가할 수 있는 최소 구조를 만든다.
- 수용 기준: 루트 앱이 하위 소스를 중복 컴파일하지 않음; Core가 WPF/DX12 패키지를 참조하지 않음; 기존 WPF 창이 계속 실행됨.
- 검증: B, RUN. 테스트 프로젝트의 검색/실행 경로를 확인하되 빈 테스트 실행을 기능 검증으로 기록하지 않는다.
- 의존: 없음.
- 예상 파일: `TeachingToolWpf.csproj`, `TeachingToolWpf.slnx`, `src/TeachingToolWpf.Core/TeachingToolWpf.Core.csproj`, `tests/TeachingToolWpf.Core.Tests/TeachingToolWpf.Core.Tests.csproj` (4개).

## T02 — WPF 내부 DX12 표시 실험

- [x] 완료
- 목적: 전용 자식 HWND에 DX12 배경색을 표시하고 기술 선택을 실제 환경에서 확인한다.
- 수용 기준: 기준 RTX 3060 Laptop을 우선 선택하고 실제 adapter/feature 정보 표시; WPF 주변 패널과 GPU viewport가 표시됨; 종료 후 GPU/창 자원이 정리됨.
- 검증: B, RUN, DX12 debug layer. Ryzen 5 5600H / RAM 32 GB / RTX 3060 Laptop 6 GB에서 adapter·드라이버·전원 조건·패키지 버전·배포 요구사항을 `docs/rendering-spike.md`에 기록. 내장 GPU 사용/지원 실패는 명시적으로 표시한다.
- 의존: T01.
- 예상 파일: `TeachingToolWpf.csproj`, `MainWindow.xaml`, `Controls/Dx12Viewport.cs`, `Rendering/Dx12Renderer.cs`, `docs/rendering-spike.md` (5개).

## T03 — 좌표 변환과 viewport 입력

- [ ] 전체 수용 기준 검증 완료
- 현재: 구현됨. 실제 150% DPI/기본 resize/커서 앵커 검증. 100/200% 실제 모니터 전환은 미검증.
- 목적: 확대·이동·Fit·1:1 및 HWND 포인터/키보드 입력 경계를 만든다.
- 수용 기준: 커서 중심 확대와 역변환 왕복이 허용오차 안에 있음; DIP와 물리 픽셀·1:1 의미가 일치; 포커스/포인터 캡처/기본 resize가 동작함.
- 검증: B, TC, RUN. 합성 격자에서 100/150/200% DPI와 큰 이미지 좌표의 왕복/커서 앵커 검증.
- 의존: T02.
- 예상 파일: `src/TeachingToolWpf.Core/ViewportTransform.cs`, `tests/TeachingToolWpf.Core.Tests/ViewportTransformTests.cs`, `Controls/Dx12Viewport.cs`, `Rendering/Dx12Renderer.cs`, `MainWindow.xaml` (5개).

### 체크포인트 A — 표시 경로

- [ ] 실제 하드웨어에서 창 표시·입력·resize·DPI·종료를 확인하고 T02의 기술 선택 근거를 기록했다.
- [ ] 소프트웨어 렌더 결과와 하드웨어 결과, 검증 불가능한 항목을 구분했다.

## T04 — 8K 배경 및 24K 폭 분할 표시

- [ ] 전체 수용 기준 검증 완료
- 현재: 8K/24K 분할 표시는 구현·실행됨. 분수 배율 선형 필터의 타일 gutter/seam 보장은 후속 항목.
- 목적: 실제 배경 이미지와 채널 보존 업로드를 연결한다. 첫 단계부터 단일 texture 축을 넘는 입력도 검증한다.
- 수용 기준: 8K Gray8/Gray16 및 BGRA32 정적 소스 표시; 24576×1024 합성 스트립이 분할되어 경계 없이 표시; 파일 교체·실패 시 ROI 좌표 정책과 소스 세대 정책 적용.
- 검증: B, RUN. 패턴 이미지의 픽셀/계조·stride padding·타일 경계를 확인하고 업로드량 기록. 정적 파일 교체 시 이전 결과가 뒤늦게 나타나지 않음 확인.
- 의존: T03.
- 예상 파일: `Imaging/ImageFrame.cs`, `Imaging/StaticImageSource.cs`, `Rendering/ImageTextureUploader.cs`, `Shaders/Image.hlsl`, `Rendering/Dx12Renderer.cs` (5개).

## T04A — 필수 Command/Memento 및 Undo/Redo 기반

- [x] 완료
- 목적: 첫 ROI 편집부터 공통 명령과 복원 가능한 문서 상태를 사용하도록 만든다.
- 수용 기준: WPF 비의존 편집 명령/이력과 ROI 문서 계약; Undo/Redo 및 성공한 새 명령의 Redo 분기 소거; 실패/취소/no-op가 문서와 이력을 바꾸지 않음.
- 검증: B, TC. 단순 ROI 생성/변경 명령으로 반복 Undo/Redo, 빈 스택, 새 분기, 상태 스냅샷 불변성, 건수/바이트 예산을 검증한다. 이력이 이미지/GPU 객체를 복제하지 않는지 리뷰한다.
- 의존: T01, T04.
- 예상 파일: `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiDocument.cs`, `src/TeachingToolWpf.Core/IEditCommand.cs`, `src/TeachingToolWpf.Core/EditHistory.cs`, `tests/TeachingToolWpf.Core.Tests/EditHistoryTests.cs` (5개).

## T05 — ROI 생성·단일 선택·삭제

- [x] 완료
- 목적: T04A의 회전 사각형 모델과 이력에 기본 사용자 편집을 연결한다.
- 수용 기준: 생성·삭제가 공통 명령을 통해 Undo/Redo 가능; ROI 내부/경계 hit test가 zoom/DPI에 일관됨; 퇴화/비정상 수치 거부.
- 검증: B, TC, RUN. 큰 좌표와 작은 ROI에서 hit test·네 꼭짓점 표시 일치, 생성/삭제의 Undo/Redo 및 ID 보존 확인.
- 의존: T04A.
- 예상 파일: `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `Rendering/RoiRenderer.cs`, `Controls/Dx12Viewport.cs`, `tests/TeachingToolWpf.Core.Tests/RoiGeometryTests.cs` (5개).

## T06 — 크기·회전·절대 0도 초기화

- [x] 완료
- 목적: 핸들 및 수치 입력으로 ROI 형상을 조절하고 각도를 읽을 수 있게 한다.
- 수용 기준: 회전된 상태의 8개 resize 핸들과 회전 피벗 동작; 이미지 기준 절대 각도 표시/입력/0도 초기화; Shift 회전 스냅과 편집 취소 동작.
- 검증: B, TC, RUN. 0/90/180/359.9도, 반대 핸들 고정, 각도 wrap, 최소 크기 경계 확인. resize/회전/0도 초기화 각각이 한 명령으로 Undo/Redo되는지 검증.
- 의존: T05.
- 예상 파일: `src/TeachingToolWpf.Core/RoiEditor.cs`, `Rendering/RoiRenderer.cs`, `ViewModels/RoiPropertiesViewModel.cs`, `MainWindow.xaml`, `tests/TeachingToolWpf.Core.Tests/RoiTransformTests.cs` (5개).

### 체크포인트 B — 단일 ROI 티칭

- [ ] 8K 이미지 교체 → 확대/이동 → 생성 → resize/회전 → 0도 초기화 → 삭제가 연결되고 ROI 변경을 Undo/Redo할 수 있다.
- [ ] 24K 폭 합성 스트립에서 texture 경계와 ROI 위치가 맞는다.

## T07 — 다중 선택과 겹침 탐색

- [ ] 전체 수용 기준 검증 완료
- 현재: Ctrl/Shift/마퀴/Alt 겹침 탐색 및 다중 이동·회전 구현. 별도 활성 ROI와 혼합 수치의 일괄 입력 UI는 미구현; 다중 선택은 개수/공통 경계로 표시.
- 목적: Ctrl/Shift·마퀴·Alt 순환 선택과 다중 이동/회전을 제공한다.
- 수용 기준: 선택 집합과 활성 ROI가 명확함; 선택별 표시/공통 피벗/혼합 속성 표시 동작; 텍스트 입력 중 단축키가 가로채지 않음.
- 검증: B, TC, RUN. 겹침·마퀴 완전 포함·선택 토글·빈 영역/취소 동작, 다중 이동/회전의 한 단계 Undo/Redo 확인.
- 의존: T06.
- 예상 파일: `src/TeachingToolWpf.Core/SelectionState.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `Rendering/RoiRenderer.cs`, `Controls/Dx12Viewport.cs`, `tests/TeachingToolWpf.Core.Tests/SelectionTests.cs` (5개).

## T08 — 회전 ROI의 부모·자식 관계

- [x] 완료
- 목적: 완전 포함에 따른 관계와 부모 변형 상속을 구현한다.
- 수용 기준: 최소 포함 부모·동률·경계 정책이 결정적이며 순환 없음; 재부모화 시 이미지 기준 형상 보존; 부모+자식 동시 선택 변형의 중복 방지와 부모 연쇄 삭제/전체 복원 동작.
- 검증: B, TC, RUN. 3단계 중첩, 부분 겹침, 바깥 ROI 나중 생성, 자식 이탈, 부모 resize/회전, 중복 선택 변형 검증. 선택하지 않은 자손도 연쇄 삭제되고 겹친 타 ROI는 남으며, 한 번의 Undo로 전체 트리/ID/이름/형상/선택이 복원되고 Redo로 같은 집합이 삭제됨을 테스트.
- 의존: T07.
- 예상 파일: `src/TeachingToolWpf.Core/RoiHierarchy.cs`, `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `tests/TeachingToolWpf.Core.Tests/RoiHierarchyTests.cs`, `tests/TeachingToolWpf.Core.Tests/SelectionTests.cs` (5개).

## T08A — 새 부모 ROI로 그룹화 및 그룹 해제

- [x] 완료
- 목적: 선택 ROI들을 감싸는 실제 부모 ROI를 만들고, 형상을 보존하며 묶음을 해제한다.
- 수용 기준: 회전 형상과 여백을 포함하는 0도 부모 및 생성 경로 메타데이터; 완전 포함 규칙과 모든 선택 ROI의 소속 검증; 그룹화/해제/연쇄 삭제의 구별과 한 단계 Undo/Redo.
- 검증: B, TC. 회전된 여러 ROI, 선택된 부모+자식, 선택 밖 ROI 포함, 기존 더 작은 부모와 충돌하는 경우, 중첩 그룹, 해제 후 world 형상/재부모화 및 반복 Undo/Redo를 검증. 실패/no-op는 문서/Redo를 보존한다.
- 의존: T08, T04A.
- 예상 파일: `src/TeachingToolWpf.Core/GroupRoisCommand.cs`, `src/TeachingToolWpf.Core/UngroupRoisCommand.cs`, `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiDocument.cs`, `tests/TeachingToolWpf.Core.Tests/GroupCommandTests.cs` (5개).

## T09 — 부모 이름표와 ROI 트리

- [x] 완료
- 목적: 계층과 부모 이름을 화면·패널에서 확인하고 수정할 수 있게 한다.
- 수용 기준: 한글 부모 이름과 자식 수 표시; ROI 회전/zoom에도 이름표는 읽을 수 있음; 트리 선택/이름/각도와 viewport가 동기화됨.
- 검증: B, RUN. 한글/영문/긴 이름과 이름 변경 Undo/Redo, 재부모화, 중첩 배지, DPI 이동 및 화면 밖 ROI의 트리 접근 확인.
- 의존: T08A.
- 예상 파일: `Rendering/LabelAtlas.cs`, `Rendering/RoiRenderer.cs`, `ViewModels/RoiTreeViewModel.cs`, `ViewModels/RoiPropertiesViewModel.cs`, `MainWindow.xaml` (5개).

### 체크포인트 C — 계층 편집

- [ ] R1–R6의 핵심 경로와 다중 선택의 부모·자식 동작을 실제 화면에서 재현했다.
- [ ] 사용자 요청대로 부모 resize의 초안 정책(자식 크기 유지)을 계획/지침/테스트에 동일하게 반영했다.
- [ ] 부모 연쇄 삭제와 전체 하위 트리 Undo/Redo가 확인되며, 클린 코드·SOLID·GRASP 검토 및 사용 패턴의 이유를 기록했다.

## T10 — 필수 Undo/Redo 통합 및 실패 원자성

- [x] 완료
- 목적: T04A부터 연결한 필수 이력을 전체 편집·수동 배경 교체 경로에 통합하고 실패 처리를 완성한다.
- 수용 기준: 한 드래그/다중 편집/연쇄 삭제가 한 undo 단위; 형상·계층·이름·선택 및 수동 소스 교체 복원; 취소/실패/no-op가 문서와 이력 위치를 유지.
- 검증: B, TC, RUN. 전체 편집의 반복 왕복, 새 명령 뒤 Redo 제거, 예산 경계, 원본 파일 변경/삭제로 배경 복원 실패, 부분 실패 rollback, 캡처 상실 확인. 라이브 프레임 도착은 이력을 쌓거나 지우지 않아야 한다.
- 의존: T09.
- 예상 파일: `src/TeachingToolWpf.Core/EditHistory.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `Controls/Dx12Viewport.cs`, `ViewModels/TeachingViewModel.cs`, `tests/TeachingToolWpf.Core.Tests/EditHistoryTests.cs` (5개).

## T10A — 외부 수동 조작 툴바와 속성 패널

- [x] 완료
- 목적: 단축키를 모르는 사용자도 이미지 교체, zoom/pan, ROI 편집, 그룹화를 화면 버튼으로 시작할 수 있게 한다.
- 수용 기준: 계획 7.1절의 파일/보기/도구/편집/속성 버튼이 실제 명령과 연결; 버튼·단축키가 같은 명령/이력 사용; 이미지/선택/로딩/이력에 맞는 CanExecute와 활성 도구/배율 표시.
- 검증: B, TC, RUN. 버튼으로 이미지 열기→확대/축소/Fit/1:1→이동 도구/방향 이동→ROI 2개 생성→그룹화→각도 초기화→그룹 해제→Undo/Redo→부모 연쇄 삭제를 재현한다. 빈 문서, 잘못된 배율, 선택 부족, 파일 취소/실패, 도구 전환 중 드래그 취소, 작은 창/고 DPI를 확인한다. 합성 라이브 시작/정지 연결은 T12에서 검증한다.
- 의존: T10, T08A.
- 예상 파일: `MainWindow.xaml`, `ViewModels/TeachingViewModel.cs`, `ViewModels/ViewportViewModel.cs`, `Controls/Dx12Viewport.cs`, `tests/TeachingToolWpf.Core.Tests/ToolbarCommandTests.cs` (5개). Core 테스트는 WPF 타입 없이 공통 명령의 실행 가능 조건을 검증하고 WPF 바인딩은 RUN으로 확인한다.

## T11 — 프레임 수명과 제한된 표시 큐

- [x] 완료
- 목적: 장비 소스로 연결할 수 있는 lease·세대 ID·취소·최신 프레임 전달을 구현한다.
- 수용 기준: 대기 슬롯 1개와 제한된 진행 프레임; 버린/완료된 버퍼가 정확히 한 번 반환; GPU 작업 완료 전 재사용 없음.
- 검증: B, TI. 생산/소비 속도 역전, 소스 교체, 취소·종료, stride/길이 불일치, GPU 완료 지연 모의 시험. imaging 테스트 프로젝트를 솔루션에 등록한다.
- 의존: T04, T10A.
- 예상 파일: `Imaging/FrameMailbox.cs`, `Rendering/ImageTextureUploader.cs`, `tests/TeachingToolWpf.Imaging.Tests/TeachingToolWpf.Imaging.Tests.csproj`, `tests/TeachingToolWpf.Imaging.Tests/FrameLifetimeTests.cs`, `TeachingToolWpf.slnx` (5개).

## T12 — 최소 8K 라이브 소스와 계측

- [ ] 전체 수용 기준 검증 완료
- 현재: 7680×4320 Gray8/Gray16/BGRA32 + ROI 500, 각 3회 측정 완료. 8192² 실시간/모든 추가 진단 지표는 미검증. 정지 후 마지막 원본 유지.
- 목적: 프레임마다 달라지는 8K 패턴을 표시하면서 ROI를 계속 편집한다.
- 수용 기준: 외부 시작/정지 버튼으로 7680×4320 이상 합성 입력을 조작하고 Gray8/Gray16/BGRA32 각각 시험; FPS·source-to-Present 지연·드롭·업로드량 측정; ROI/선택/뷰포트가 프레임 도착으로 초기화되지 않고 정지 시 마지막 프레임 유지.
- 검증: B, TI, RUN. B2의 임시 60 FPS 부하를 형식별로 측정하고 8192×8192 스트레스 결과를 분리. 원본 생산 속도와 렌더 속도를 각각 기록한다.
- 의존: T11.
- 예상 파일: `Imaging/SyntheticFrameSource.cs`, `Rendering/RenderMetrics.cs`, `Rendering/Dx12Renderer.cs`, `ViewModels/DiagnosticsViewModel.cs`, `MainWindow.xaml` (5개).

### 체크포인트 D — 실시간

- [ ] 4K 결과로 대체하지 않고 최소 8K 실측을 기록했다.
- [ ] GPU fence·UI 스레드·프레임 큐 간 교착이 없고 10분 실행 중 버퍼 수가 제한된다.
- [ ] 기준 장비의 실제 adapter/드라이버/전원 조건, 미정인 입력 FPS/픽셀 형식과 달성하지 못한 목표를 기록했다.

## T13 — 24K급 일반 이미지 파일의 메모리가 제한된 읽기

- [x] 완료
- 목적: 스캔 완료된 큰 이미지를 파일 열기로 읽어 디코더가 제공하는 영역/순차 읽기를 타일 계약에 연결한다.
- 수용 기준: 지원 일반 형식의 크기·픽셀 형식 자동 인식; 디코더의 영역/순차 기능에 맞게 제한된 메모리로 읽고 불가하면 이유 표시; 취소·소스 교체·큰 크기/오프셋 검증.
- 검증: B, TI. 실제 또는 생성한 24K급 PNG/JPEG/BMP 파일 열기와 취소, 형식별 메모리 사용, 손상된 메타데이터를 검증. 3/6 GiB RAW fixture는 별도 오프셋/스트레스 시험에만 사용하고 일반 파일 시험을 대체하지 않는다. TIFF/BigTIFF는 지원한 변형을 별도 기록한다.
- 의존: T11.
- 예상 파일: `Imaging/ITileSource.cs`, `Imaging/IImageDecoder.cs`, `Imaging/ImageDecoderFactory.cs`, `Imaging/ImageImportService.cs`, `tests/TeachingToolWpf.Imaging.Tests/ImageImportTests.cs` (5개).

## T14 — 피라미드와 취소 가능한 전처리

- [ ] 전체 수용 기준 검증 완료
- 현재: 디스크 단계 생성/버전/manifest 마지막 게시/사전 취소 테스트 구현. 프로세스 강제 종료 뒤 partial 캐시 자동 정리와 디코드 도중 강제 취소 반복 시험은 후속 항목.
- 목적: 24K급 긴 이미지를 축소 표시할 overview와 재개방 캐시를 만든다.
- 수용 기준: 제한된 작업 메모리로 단계 생성; 파일 정체성/메타데이터/변경 버전에 따라 캐시 무효화; 진행률·취소·중단 캐시 복구 동작.
- 검증: B, TI, RUN. 비정형 크기/Gray16 계조/타일 경계/캐시 변경 검증. cold 생성 시간과 warm 재개방 시간 분리.
- 의존: T13.
- 예상 파일: `Imaging/PyramidBuilder.cs`, `Imaging/PyramidTileSource.cs`, `Imaging/DerivedImageCache.cs`, `ViewModels/ImageLoadViewModel.cs`, `tests/TeachingToolWpf.Imaging.Tests/PyramidTests.cs` (5개).

## T15 — 가시 타일 요청과 메모리 예산

- [ ] 전체 수용 기준 검증 완료
- 현재: 512px LOD/overview/32개 준비 큐/가시 타일 우선 LRU 구현. 가시 타일·fence 지연 해제는 논리 캐시 예산과 별도이고 gutter는 미구현. 엄격한 전체 프로세스 메모리 상한을 주장하지 않음.
- 목적: 현재 viewport와 zoom에 필요한 타일을 우선 표시한다.
- 수용 기준: LOD/가시 영역/제한된 prefetch 적용; CPU/GPU 캐시가 바이트 상한 준수; 저해상도 대체 표시와 고해상도 교체 시 seam·세대 혼입 없음.
- 검증: B, TI, RUN. B3 3/6 GiB 원본의 빠른 pan/zoom, eviction, 취소, 작은 GPU 예산 모의 시험. tile hit/miss·캐시 사용량·전체 프로세스 메모리를 각각 기록.
- 의존: T12, T14.
- 예상 파일: `Imaging/VisibleTileScheduler.cs`, `Imaging/CpuTileCache.cs`, `Rendering/GpuTileCache.cs`, `Rendering/Dx12Renderer.cs`, `tests/TeachingToolWpf.Imaging.Tests/TileSchedulingTests.cs` (5개).

### 체크포인트 E — 대용량

- [ ] 24K급 폭이 여러 texture로 나뉘어 동작하고 3/6 GiB 원본을 전체 적재하지 않는다.
- [ ] 스캔 완료된 일반 파일을 사용자 메타데이터 수동 입력 없이 열었고, 카메라 라인 획득/조립 기능을 범위에 추가하지 않았다.
- [ ] 전체 Fit을 위한 최초 overview 비용과 이미 준비된 타일의 탐색 성능을 별도로 기록했다.

## T16 — 창·장치·종료 수명 강화

- [ ] 전체 수용 기준 검증 완료
- 현재: resize/최소화 중 제출 중단/복원/재연결/종료 경로 구현. 실제 장치 제거 및 혼합 DPI 모니터 이동은 미검증.
- 목적: 사용 중 창/장치 상태가 바뀌어도 편집 데이터와 자원 수명을 유지한다.
- 수용 기준: 최소화/복원/0 크기/반복 resize/다중 DPI에서 정상 동작; device removal에 복구 또는 명시적 재시도 제공; 닫는 중 작업 취소·GPU 완료·HWND 정리 순서가 안전함.
- 검증: B, TI, TC, RUN 및 DX12 debug layer. 최소화·resize·소스 교체 반복, 모의 장치 제거, 닫는 도중 업로드 확인.
- 의존: T15.
- 예상 파일: `Controls/Dx12Viewport.cs`, `Rendering/Dx12Renderer.cs`, `Rendering/DeviceRecovery.cs`, `Imaging/FrameMailbox.cs`, `docs/runtime-validation.md` (5개).

## T17 — 선택 디자인과 키보드 검증

- [ ] 전체 수용 기준 검증 완료
- 현재: 고정 DIP 이중 윤곽/핸들/각도/태그/외부 버튼/단축키 구현. 150% 합성 패턴 캡처 확인. 전체 접근성/모든 DPI와 대비 조건 수동 검증은 후속 항목.
- 목적: 선택·호버·포커스·각도 HUD·작은 ROI 조작을 일관된 제품 동작으로 마무리한다.
- 수용 기준: R8 상태 구분과 고정 DIP 크기 유지; 밝고 어두운 영상 모두 선택/이름이 읽힘; 키보드/트리/속성으로 마우스 조작을 보완하고 포커스가 사라지지 않음.
- 검증: B, RUN. 100/150/200% DPI, 밝기/패턴이 다른 8K 영상, 조밀한 다중 선택과 작은 ROI를 수동 검증해 기록한다.
- 의존: T16.
- 예상 파일: `Rendering/RoiRenderer.cs`, `Rendering/LabelAtlas.cs`, `Controls/Dx12Viewport.cs`, `MainWindow.xaml`, `docs/runtime-validation.md` (5개).

## T18 — 성능 비교와 샘플 사용 문서

- [x] 완료
- 목적: B1–B5와 R1–R12의 최종 결과를 재현 가능한 형태로 남긴다.
- 수용 기준: 기준 장비·입력·설정·반복 조건/결과 공개; 미달 항목은 병목 근거와 함께 유지하고 원칙 예외는 사전 사용자 승인 및 ADR 확인; 필수 Undo/Redo·설치·실행·단축키·지원 형식·알려진 제약 문서화.
- 검증: B, TC, TI, BM, RUN. B1–B5를 계획의 절차로 측정하고 변경 전후 비교. 성능 수정이 필요하면 별도 작은 작업으로 나눈 후 관련 시나리오만 재측정한다.
- 의존: T17.
- 예상 파일: `benchmarks/TeachingToolWpf.Benchmarks/TeachingToolWpf.Benchmarks.csproj`, `benchmarks/TeachingToolWpf.Benchmarks/Program.cs`, `TeachingToolWpf.slnx`, `docs/performance-baseline.md`, `README.md` (5개).

### 최종 체크포인트

- [ ] R1–R12의 수용 기준과 미검증 항목을 구분해 기록했다.
- [ ] 최소 8K 실시간과 24K급 라인 스캔을 실제 또는 명시된 합성 입력으로 검증했다.
- [ ] ROI 포함·절대 각도·0도 초기화·다중 선택·부모 이름·부모 연쇄 삭제·필수 Undo/Redo가 같은 규칙을 따른다.
- [ ] 외부 버튼으로 기본 사용자 흐름을 완료했고 그룹화/해제와 삭제의 결과 및 Undo/Redo가 각각 검증되었다.
- [ ] 클린 코드·디자인 패턴·SOLID 5개·GRASP 9개를 검토했고, 예외가 있다면 사용자 승인 내용과 구현 범위가 일치한다.
- [ ] 빌드/테스트/실행/메모리·성능 측정 결과가 있으며, 통과하지 않은 목표를 임의로 낮추지 않았다.

## 이번 구현의 검증 요약

- [x] Core 28개 / 영상 12개 자동 테스트 통과; Release 빌드 경고·오류 0.
- [x] 실제 DX12/RTX HWND 입력, 그룹/연쇄 삭제/Undo/Redo, 수치 회전, Escape, GPU 재연결을 smoke로 검증.
- [x] 8K Gray8/Gray16/BGRA32 + ROI 500개 3회 반복 측정, 드롭/백분위/조건을 기록.
- [x] 24576×131072 BigTIFF Gray8/Gray16, 3/6 GiB 디코드 데이터와 마지막 타일 검증. RAW 시험은 실제 BigTIFF 시험으로 대체.
- [x] 24K 폭 PNG/JPEG 및 8K WebP의 채널/경계 검증. WebP 자체 크기 제한 때문에 24K WebP는 지원 대상으로 표시하지 않음.
- [x] SOLID 5개 / GRASP 9개와 적용 패턴의 근거 기록. 승인되지 않은 원칙 예외 없음.
- [ ] B1 연속 정적 편집의 정식 반복 측정, 8192² 실시간, B4 GPU 10,000 ROI 조작, B5 10분 연속 안정성.
- [ ] 실제 카메라/현장 이미지, 혼합 DPI, 실제 장치 제거, 물리 모니터 표시 지연 측정.

위 미완료 항목은 구현된 샘플의 제한/추가 검증이며 완료 체크로 덮지 않는다. 앞의 원래 체크포인트는 전체 조건을 검증하기 전까지 미체크로 유지한다.

## 2026-10-02 사용자 피드백 후속

- [x] STATIC의 HTTRANSPARENT 입력 원인 수정, 전용 HWND 및 실제 포인터로 ROI 생성 확인.
- [x] 도구/목록/캔버스/속성 분리, ROI 즉시 추가와 그리기 도구 분리.
- [x] 이미지 교체 시 ROI/선택 초기화, Undo/Redo로 소스/형상/뷰 복원, 실패 시 라이브 보존.
- [x] 그리기 교집합 및 이동/크기/회전/그룹/수치의 이미지 경계 제약, 0도 복원 보정.
- [x] 얇은 파란 선택 윤곽·흰 resize 핸들·원형 회전 핸들.
- [x] OpenCV Binarize/Canny 필터 선택, 명시적 미리보기, 선택/변형 변경 갱신, 원본 복귀.
- [x] 대용량 LOD 추출, 회전 ROI 픽셀 매핑, 단일 작업/늦은 결과 폐기, 라이브 최대 5Hz.
- [x] Core 36개 + Imaging 22개 테스트 통과; 실제 클릭/드래그와 RTX DX12 디버그 smoke 확인.
- [x] SOLID 5 / GRASP 9 검토, ADR/사용법/의존성 라이선스 갱신.
- [ ] 최종 publish 빌드에서 OpenCV 필터 포함 smoke 및 화면 표시 재확인.
