# 271,255 ROI 그룹화·회전·Esc 후속 검증

2026-10-05. 사용자 보고: 271,255개 ROI를 그룹화한 뒤 확대·축소·이동·회전이 느려지고, 회전 확정 중 Esc를 누르면 `Warm`의 `ThrowIfCancellationRequested`에서 `OperationCanceledException`이 관찰됐다. 사용 경로는 **마우스 회전 핸들 드래그**다. 현재 브랜치에서 로컬 수정·검증만 진행했다.

수용 목표는 작업 중 2D 표시·입력의 60FPS(약16.67ms)다. 검사 CPU125ms/CUDA80ms와 무관하며, 비동기 작업의 총 완료 시간 단축을 이 목표의 달성으로 해석하지 않는다.

## 확인한 원인과 수정

- 그룹/해제 명령이 UI 스레드에서 불변 문서와 이력, 전체 목록을 만들었다. 그룹 외곽을 구하려고 별도 BVH까지 구축했다. 기존 비동기 편집 준비·목록 생성·원자적 게시 경로로 연결하고, 준비된 전체 Bounds 또는 실제 하위 도형 Bounds의 선형 합집합을 사용한다. 동기 Core API도 같은 그룹 규칙을 사용한다.
- 그룹 준비에서 공간·선택·변형·회전 경계를 함께 준비한다. 게시 후 첫 회전 샘플이 별도 Core 준비를 기다리지 않게 한다. GPU 최초 업로드 준비와는 별개다. 부모/자손의 실제 형상·명시적 ParentId·한 번의 변형과 Undo/Redo는 유지한다.
- 렌더 캐시는 같은 공간 블록의 CPU 원본 비용을 확대 단계마다 다시 계산했다. 271,256개에서 원본 추정 비용만 단계당 약66MiB여서 128MiB 예산을 빠르게 넘었다. 원본은 identity당 한 번, 단계별 GPU/역할 배열은 각각 계산한다. 예산 자체는 늘리지 않았다.
- 요약이 없는 상세 형상은 검증된 aggregate 이하에서 동일하다. 실제 패킹의 summary 유무로 확인하고 GPU 버퍼 하나를 재사용한다. 더 큰 aggregate는 다시 확인하며, 요약이 생기는 단계는 기존 LOD를 유지한다. 폴리곤 변 수·중복 형상 때문에 인스턴스 개수만으로 판정하지 않는다.
- 반복 확대의 추가 측정에서 작은 GPU 버퍼의 최소64KiB 개별 할당 때문에 캐시 퇴출이 여전히 반복됐다. 형상·역할 구간을 기본1MiB GPU 페이지에32byte 정렬로 나눠 저장하고, 페이지의 실제 할당 비용을 한 번만 계산한다. 페이지 전체의 copy/draw 상태 전환을 지키며, 퇴출과 GPU fence 이후 마지막 lease 해제를 구별한다. 완전히 비활성인 페이지 우선 퇴출 후에도 예산을 넘으면 활성 페이지 안의 비활성 CPU 원본도 퇴출한다. 페이지 재사용을 위해 GPU가 읽는 범위를 덮어쓰지 않는다.
- Mouse Up 후에는 입력 gesture가 이미 끝나 있어 기존 `Input.Cancel()`만으로 화면의 변형 preview를 즉시 제거하지 못했다. Esc가 세션 preview도 즉시 취소하고, 취소된 결과가 늦게 게시되지 않게 한다. 진행 중 worker는 실제 종료까지 편집 제한을 유지한다.
- 취소 가능한 전체 이동/회전 및 이력 fallback의 refit에 token을 전달하고, 새 snapshot을 게시하기 전에 계산한다. 공유 Lazy에는 완성된 색인만 넣어 취소 예외가 이후 읽기에 남지 않게 한다. token 없는 기존 동기 경로의 지연 평가는 유지한다.
- 정상 취소만 처리하도록 예외 필터를 좁혔고, 준비 시작 알림 단계의 실패에서도 작업 상태를 정리한다. 예상 밖 오류는 정상 취소로 숨기지 않는다.

`ThrowIfCancellationRequested`는 요청된 취소를 전달하는 정상적인 .NET 경로다. 디버거가 throw 시점에 중단한 사실만으로 미처리 예외라고 판단하지 않았다. 실제 Esc 이후 문서/선택/이력 보존, 편집 가능 상태 복구, 후속 재실행을 검사한다. 예외 설정 변경이나 예외 무시로 문제를 감추지 않는다.

## 측정 조건

Ryzen 5 5600H/32GB, NVIDIA RTX3060 Laptop GPU, DX12 하드웨어, Release. 보고된 GPU 전용 메모리5.9GiB/예산5.1GiB. 8192² 이미지의 내부4096²에 평면 사각형271,255개를 배치하고 전체 선택→그룹1개 추가(총271,256개)한다. 사용자의 실제 ROI 문서와 분포는 제공받지 않았으므로 합성 재현이다.

그룹 진단은 UI와 독립된60Hz 입력 생산자가 pan/zoom을 제공한다. pending 입력 슬롯의 지연과 생략도 기록한다. 회전 진단은 실제 `Input.Down/Move/Up` 핸들 경로로120개 측정 샘플과2개 워밍업을 실행하지만, 샘플 사이 `Task.Delay(16)`은 UI 진행에 종속된다. 따라서 회전 callback만으로60FPS를 선언하지 않는다. 창은 화면 밖이며 물리 모니터 표시 지연 측정이 아니다.

baseline은 이번 수정 전 Core/Canvas와 동일 진단을 넣은 앱을 보관해 실행했다. 첫 중간 수정본과 최종 수정본을 구분한다. GUID와 Windows 스케줄링에 따른 실행 변동이 있고, 작은 차이를 단일 실행만으로 일반화하지 않는다.

## 측정 결과

원자료·집계 스크립트·DLL SHA256·캡처: [검증 ZIP](data/group-freeze-20261005.zip). 작업 파일은 `artifacts/group-freeze`에 있다. 모든 앱 실행14회는 `stage=complete`, `error=null`, exit0이며 정확한 Undo/Redo를 확인했다.

파일명 `before-*`는 수정 전 기준(그룹/회전 각1회), `after1-*`는 비동기 그룹·원본 비용 공유 단계다. **`final-*`도 GPU 페이지 배치 이전 중간 구현**이다. `paged-*`는 페이지 배치의 첫 실행, `verified-*`는 최종 서식/빌드 후 실행이다. `paged`→`verified` 사이 기능 변경은 없으며 Canvas DLL의 서식 빌드 해시는 달라 별도 보관했다. 전체 SHA256은 ZIP의 `binary-hashes.json`, 숫자 집계는 `comparison.json` 및 `*-summary.json`을 따른다.

기준과 최종의 동일2초 pan/zoom 시나리오(`before-group`→`verified-group`, 각각1회):

| 항목 | 수정 전 | 최종 |
|---|---:|---:|
| 그룹 명령 UI dispatch | 2131.35ms | 2.15ms |
| 그룹 준비/게시 총 완료 | 2131.45ms | 1396.04ms |
| 그룹 작업 중 입력 슬롯 최대 대기 | 2150.77ms | 18.45ms |
| 그룹 작업 중 생략 입력 주기 | 129 | 4 |
| 그룹 후 pan 렌더 제출 최대 | 0.79ms | 0.73ms |
| 첫 zoom 렌더 제출 최대 | 178.03ms | 108.14ms |
| 첫 zoom Present 반환 간격 최대 | 194.05ms | 125.23ms |

baseline의 그룹 UI 정지는 새 장면 제출 자체가 끊긴다. 이 구간의 렌더 간격 집계만 보고 부드럽다고 판단하지 않으며, 입력 슬롯 대기와 생략129개를 함께 확인했다. 최종 그룹 작업 중 Present 반환 최대47.75ms, Undo 입력 슬롯53.40ms도 남았다. 그룹의 누적 GC pause160.16ms는 여러 정지의 합이며 한 번의160ms 정지로 해석하지 않는다.

반복 확대는 별도6.5초 시험의 **3.2초 이후**(한 왕복 뒤)를 비교했다. 페이지 배치 전 `final-cancel-zoom` 1회와 적용 후 `paged-cancel-zoom`/`verified-cancel-zoom` 2회다.

| 반복 확대 후반 구간 | 페이지 배치 전 | 적용 후2회 |
|---|---:|---:|
| 최대 렌더 제출 | 109.25ms | 0.50 / 1.16ms |
| ROI 업로드 합계 | 3,866,940bytes | 0 / 0bytes |
| 패킹 / GPU 할당 최대 | 75.42 / 44.23ms | 모두0ms |
| active cache 최대 | 약128.0MiB | 약74.1MiB |

왕복 첫 구간은 여전히 새 LOD의 준비가 필요하다. 두 실행의 첫 zoom 제출 최대123.92/116.11ms, Present 반환 최대138.56/142.84ms다. 반복 업로드 제거를 모든 확대에서 무지연이라고 표현하지 않는다.

마우스 핸들 회전(`before-rotation`→`verified-rotation`, 각각1회, debug=false):

- pointer-down9.56→3.88ms. 초반 preview 미준비 샘플3→0개, 입력122개 중 callback 최대3.67ms. 선택1개/변형 대상271,256개.
- 드래그 동안 ROI 업로드는 전후 모두0bytes다. 최종 렌더 paint+submit 최대17.24ms 프레임에는 누적 GC pause counter 증가14.89ms가 겹쳤고, 그 외 관측 피크7.73ms였다. GC를 제외해60FPS 달성을 주장하지 않는다.
- Mouse Up 이후 관측 렌더 제출 최대102.44→20.80ms, 해당 피크 GPU 자원 생성83.01→1.30ms. 준비/게시 총 완료1510.73→1600.82ms와 Dispatcher 최대43.51→43.95ms는 개선되지 않았다. GPU 준비 개선과 편집 전체 완료 시간을 구별한다.

**전체 작업 구간60FPS는 FAIL**이다. 그룹의 수초 UI 정지와 반복 확대의 캐시 재생성은 개선됐으나 최초 확대·회전 확정·일부 이력/GC는 프레임 예산을 넘는다. 실제 모니터 표시 FPS를 측정한 것도 아니다.

## 취소 및 정확성

- Core: cold/warm 외곽, 전체/부분/중첩 그룹과 해제, 부모 밖 자손, 공통 부모, bulk5000개, 원자적 순서 번호, stale·취소·Redo 분기·Undo/Redo.
- 앱: 그룹/해제 중 navigation 보존, 준비 취소 후 명령 복구, 예상 밖 오류 전파, Mouse Up 이후 Esc의 즉시 preview 복원. ICommand의 async-void 정리와 `EditCompletion`의 완료 시점을 구별한다.
- 렌더: 확대 단계 사이 원본 비용 공유, 별도 identity, 마지막 퇴출·Clear 참조 해제, 사각형/원/삼각형/오목 폴리곤·중복 형상·summary·계층 역할 갱신.
- 취소 후 공유 인덱스를 다시 읽고 편집할 수 있는지 확인한다. pre-cancel 단위시험은 실행 중 취소 지연을 입증하지 않으므로 별도 앱 진단을 함께 사용한다.

최종 `verified-cancel-zoom`: 그룹 dispatch 후 실제156.35ms에 Esc를 보냈다(요청 지연150ms). Esc handler1.60ms, 작업 완료까지2.72ms, CanEdit 회복까지2.38ms. 원본문서·선택·이력 bytes/Undo/Redo 상태를 보존하고 재그룹화했다.

최종 `verified-cancel-rotation`: Mouse Up 후 실제357.11ms에 Esc를 보냈다(요청350ms). handler1.61ms, 작업 완료까지3.72ms. handler 반환 직후 preview 제거와 원본 map/selection identity 복원을 확인하고, 전체 문서 값·이력·CanEdit 복구 후 같은 핸들로 재회전했다. 이 시각은 GPU/물리 화면 복원 완료 시각이 아니다. 앞선 중간 실행은 완료까지13.49ms였으며 취소 지연이 항상 일정하다고 보장하지 않는다.

최종 검증 상태:

- PASS: 전체 Release 빌드 경고0/오류0. Core232 + Canvas154 + 앱246 = **632개** 회귀(실패/skip0). 페이지 수명 추가 전625개 결과와 구별한다.
- PASS: 새 WARP7개 시험은32byte 정렬/비겹침, 실제 페이지 비용, active/resident 구분, 중복 Retire/Dispose, 대기 lease 생존, 큰 요청·실패 입력, 페이지 공유 상태를 확인했다.
- PASS: 최종 RTX271,255개와 WARP40,000개의 `--gpu-debug` 회전·게시·Undo/Redo, RTX 작업 중 Esc. D3D12 메시지/렌더 오류 없음. preview/committed 캡처의 그룹 윤곽·각도·핸들·하위 ROI·미니맵 확인. WARP 기능 성공을 저사양 PC60FPS 보장으로 해석하지 않는다.
- PASS: 변경23개 C# 파일의 whitespace·style·명명 검사 및 diff 검사. 이전에 기록된 무관한 전체 형식 위반 파일은 이번 변경에 섞어 수정하지 않았다.
- NOT_RUN: 검사 알고리즘 별도 테스트. 검사 구현을 변경하지 않았고 관련 Core/Canvas/앱 회귀만 실행했다.

## 남은 한계

공유 인덱스의 최초 cold 생성과 일부 Warm 단계는 내부 전체가 협력 취소되는 것은 아니다. Esc는 미리보기를 즉시 복원하지만 새 편집 허용은 worker가 실제로 끝난 뒤다. 버퍼 생성·요약 패킹의 최초 비용, 편집 시 GC와 게시 비용도 남는다. 캐시 비용은 fence 대기 중인 퇴출 리소스나 프로세스 전체 메모리의 상한이 아니다.

실제 사용자 문서·가시창 물리 입력/표시·장시간 편집·다른 GPU/CPU·별도 저사양 PC는 미검증이다. 숫자 속성 변경/resize의 별도 동기 경로를 마우스 회전 측정으로 검증했다고 표현하지 않는다. 관련 없는 검사 알고리즘은 변경하지 않았다.

## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
& ./src/TeachingToolWpf/bin/Release/net10.0-windows/TeachingToolWpf.exe --frame-cadence-check --cadence-group --rois 271255 --cadence-report artifacts/group-freeze/run.json --render-profile artifacts/group-freeze/run-render.csv --metrics artifacts/group-freeze/run-metrics.csv
./scripts/Summarize-FrameCadence.ps1 -Path artifacts/group-freeze/run.json
# 그룹 실행 도중 취소 및 여러 확대 왕복: 위 명령에 추가
# --cadence-cancel-ms 150 --cadence-navigation-ms 6500
& ./src/TeachingToolWpf/bin/Release/net10.0-windows/TeachingToolWpf.exe --roi-rotation-benchmark --rois 271255 --rotation-selection group --rotation-samples 120 --rotation-warmup 2 --report artifacts/group-freeze/rotation.json --render-profile artifacts/group-freeze/rotation-render.csv --metrics artifacts/group-freeze/rotation-metrics.csv
# Mouse Up 확정 도중 Esc, 정확성 검증 후 같은 핸들 재실행: --rotation-cancel-ms 350
```

취소 지연 옵션은 명령 dispatch 후 경과 시간이다. 특정 `Warm/Spatial` 진입을 보장하지 않는다. Esc 전에 작업이 끝나면 시험 미성립으로 실패 처리한다.

## 설계 원칙 리뷰

| 원칙 | 검토 |
|---|---|
| SRP | 계층 규칙은 Core, UI는 비동기 명령/취소, 버퍼 재사용은 렌더 책임 |
| OCP | 기존 동기 API와 준비/게시 계약, LOD 경로 보존 |
| LSP | 취소·실패·stale·원자성·이력·도형 의미 보존 회귀 |
| ISP | 새 공개 인터페이스 없음, 기존 prepared 명령 형태로 그룹/해제 추가 |
| DIP | Core에 WPF/DX12/알고리즘 의존 추가 없음 |
| Information Expert | 하위 트리/경계는 ROI 데이터, summary 여부는 패킹 순회가 판단 |
| Creator | 문서가 그룹 생성, 렌더가 GPU 버퍼 단일 소유 |
| Controller | ViewModel은 기존 준비/게시 흐름으로 위임 |
| Low Coupling | 불변 문서·기존 입력/렌더 경계 유지 |
| High Cohesion | 그룹 helper는 그룹/해제, 캐시는 비용·수명만 처리 |
| Polymorphism | 새 대체 구현이 없어 불필요한 다형성 추가 없음 |
| Pure Fabrication | 기술 캐시는 렌더 내부, 진단은 앱 Diagnostics |
| Indirection | 기존 adapter/명령 경계 유지, 서비스 로케이터 없음 |
| Protected Variations | GPU fence·도형·좌표·명시적 계층·이력 보호 |

독립 교차 리뷰를 수행했으며 승인된 원칙 예외에 의존하지 않는다.
