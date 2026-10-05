# 2D 프레임 예산 후속 — 할당·이력 재사용, 2026-10-05

**수용 상태: FAIL — 모든 작업 중 60FPS를 달성한 상태가 아니다.** 목표는 약16.67ms 안의 표시·입력 반응이다. 형상검사 CPU125ms/CUDA80ms와 별도이며 검사 코드는 변경하지 않았다. [이전 실측](frame-cadence-validation.md) 이후의 로컬 수정·검증이고 커밋·푸시는 하지 않았다.

## 변경과 보존 조건

- 전체 선택을 복사할 때 이미 준비된 공간 색인의 Bounds를 재사용한다. 색인이 없거나 부분 선택이면 기존 형상 경계 계산을 유지한다. 클립보드 값·순서와 새 복사의 identity를 보존한다.
- 붙여넣기의 배치 위치/크기 계산을 공유하고, 임시 ROI 배열을 만든 뒤 다시 복제하던 두 단계를 최종 ROI 한 번 생성으로 합쳤다. 새 ID·ParentId 재연결·필터·순서·이름·취소·실패 원자성을 유지한다.
- 이력은 최근 한 명령의 source/target 불변 문서 쌍을 재사용한다. 첫 Undo와 이어지는 Redo에서 BVH·선택 색인과 GPU 블록 identity를 보존한다. 각 이력에 전체 문서를 추가 저장하는 방식은 아니다. 다른 명령/분기에서는 기존 delta 적용으로 돌아간다.
- 캐시 source의 ROI/선택 identity·이미지가 일치해야 재사용하며, 선택·이미지·viewport는 기록된 이력 규칙을 다시 적용한다. 공개 `EditHistory`에 기록과 다른 ROI 값을 가진 source를 넘긴 경우에는 변경 대상의 존재 여부·값을 확인하고 역방향 캐시를 보관하지 않는다. 관련 없는 ROI 변경은 기존처럼 유지한다. 정상 캐시 경로에는 전체 ROI 비교를 추가하지 않는다.
- 렌더러가 패킹용 List/Dictionary를 소유하고 블록 사이에 재사용한다. 형상은 span으로 즉시 업로드하고 역할 캐시만 독립 배열을 소유한다. 폴리곤/삼각형은 Outline 임시 배열 없이 같은 좌표식으로 모든 변을 만든다.
- 성공/실패 후 사전의 형상 참조를 비운다. 보관하는 형상/역할 배열은 각각32768개 이하, 사전은4096용량 이하로 제한한다. 큰 폴리곤도 모든 변을 처리한 뒤 큰 scratch를 놓으며 도형/꼭짓점을 생략하지 않는다. GPU 리소스·fence 소유권과128MiB 기존 블록 캐시 정책은 유지한다.

이력 `HistoryBytes`는 기존 delta 추정치다. 최근 한 쌍의 파생 색인과 scratch까지 포함하는 프로세스 메모리 상한이 아니며, 재계산을 줄이는 대신 최근 자료를 보관하는 비용이 있다. 원본 영상 reader·pixel buffer를 이력 캐시에 넣지 않는다.

## 측정과 GC 해석

Release/디버거 미연결, Ryzen5 5600H/RAM32GB/RTX3060 Laptop, 실제 렌더러 RTX/VRAM5.9GiB/budget5.1GiB. 드라이버·모니터 WMI 정보는 이전 측정과 같은 장비다. 화면 밖 WPF 창,8192×8192 합성 이미지, PCB 계층 ROI4만 전체 선택→8만 붙여넣기. UI와 독립된 절대60Hz pan 공급을 유지한다. 같은 최종 앱에서 Core·Canvas 전후 DLL을 교체해 번갈아3프로세스씩 실행했다. 명령 이후1초의 탐색까지 포함하고 fixture/초기 선택 준비·정적 idle은 활성 구간과 구별한다.

`roi_pack_ms`, `roi_create_ms`, `roi_upload_ms`를 추가해 CPU의 패킹·DX12 자원 생성·업로드 명령 기록을 분리했다. `roi_upload_ms`는 GPU 실행/전송 완료 시간이 아니다. `submit_ms`와 세 하위 단계는 포함 관계이며 더하지 않는다. 개선 후 `gc_pause_counter_delta_ms`는 렌더 구간 주변에서 읽은 누적 counter의 차이다. native 정지 구간과 프레임의 정확한 교집합이 아니며 로깅 배열 생성 전에 끝값을 읽는다. 이전 Canvas에는 마지막 GC counter 열이 없으나 같은 세 하위 단계 계측을 넣었다.

`--cadence-gc-events`는 별도 보조 실행에서만 켠다. .NET native provider의 suspension 시작(ID9, GC/GCPrep)부터 재개 끝(ID3)까지의 **이벤트 발생 시각**을 저장한다. 비동기 callback 도착 시각을 정지 길이로 쓰지 않는다. UTC 읽기 전후 QPC bracket 중간값과 반폭을 남기고 구간별 누적 counter와 비교한다. 이벤트 소실/늦은 도착은 가능하며500ms 대기는 완전한 drain 보장이 아니다. 관리 목록 overflow와 unmatched가0이어도 완전 수집을 보장하지 않는다. 집계의 허용오차는1ms+bracket 반폭의2배인 추정치이며 보장된 오차 상한이 아니다. 주 성능 비교에는 listener를 켜지 않았다.

GC의 누적 시간·한 번의 최대 정지·렌더 준비 시간을 구별한다. 이번 측정에서 긴 패킹이 GC만으로 설명되지 않아 할당/자료구조 재사용을 개선했으며 언어의 한계에 도달했다는 근거로 해석하지 않는다. 공식 근거: [GC 이벤트](https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-garbage-collection-events), [.NET10 native event 전달](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/NativeRuntimeEventSource.cs), [EventPipe dispatcher](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventPipeEventDispatcher.cs).

## 반복 실측 결과

표는 **각 프로세스 활성 구간의 최대값을 구한 뒤3회의 평균**이다. 평상시 프레임 평균으로 긴 정지를 희석하지 않는다. Present 반환 시각은 물리 화면 표시 시각이 아니며 GPU timestamp도 별도 완료 슬롯의 측정값이다.

| 작업 | CPU 렌더 준비 피크 평균 전→후 | Present 반환 간격 피크 평균 전→후 | 개선 후 최악 Present 간격 |
|---|---:|---:|---:|
| 복사·붙여넣기 | 160.03→137.79ms | 176.70→158.55ms | 166.65ms |
| Undo | 62.58→0.13ms | 78.37→27.91ms | 32.26ms |
| Redo | 162.79→0.17ms | 175.68→26.64ms | 28.24ms |

붙여넣기 피크 프레임의 렌더 스레드 할당 평균은20,285,147→641,163bytes(약96.8% 감소)였다. 전체 명령/프로세스 할당을 뜻하지 않는다. 패킹124.92→105.57ms, 자원 생성32.80→30.00ms가 남아 **최초 게시를 한 프레임에 끝내는 방식의 비용은 아직 크다**. 붙여넣기 렌더 준비 피크는 약13.9% 줄었다. Undo/Redo는 개선 후 모든 반복에서 ROI 업로드0bytes로 기존 GPU 블록을 재사용했다. 공간 분할/가시 범위는 실행 시 생성된 ID와 탐색 시점에 따라 조금 달라질 수 있으며 업로드 약2.96MB의 전후 차이를 도형 생략 효과로 해석하지 않는다.

UI와 GC까지 해결됐다는 뜻은 아니다. 개선 후 최대 입력 대기 슬롯은 복붙34.37ms/Undo25.26ms/Redo17.23ms였고, Undo의 입력 실행 간격은 한 실행에서40.82ms였다. steady의 Present 간격p95는24.94–26.12ms다. 타이머/스케줄링/GC가 포함된 합성 입력이며 실제 모니터 FPS로 변환하지 않는다. 별도의 steady GC 누적 평균은1.00→9.85ms로 증가했다. 캐시가 자료를 더 오래 보관하므로 할당 감소가 모든 구간의 GC 감소를 보장하지 않는다.

보조 native 이벤트 실행은 전후 각1회로, 주3회 비교의 평균과 섞지 않는다. bracket 반폭 전0.00060ms/후0.00055ms, 관리 overflow0/unmatched0이며 구간별 counter 차이는 추정 허용오차 안이었다. 완전 수집 증명은 아니다.

| 활성 구간 | 관측 GC 정지 누적 전→후 | 한 번의 최대 정지 전→후 | 정지 횟수 전→후 |
|---|---:|---:|---:|
| 복사·붙여넣기 | 123.44→120.93ms | 17.97→23.01ms | 15→11 |
| Undo | 30.43→6.41ms | 15.75→4.73ms | 4→2 |
| Redo | 112.36→20.93ms | 26.30→13.35ms | 14→2 |

개선 후에도 복붙에서16.67ms보다 긴 개별 정지3회가 관측됐다. 같은 보조 실행의 가장 긴 붙여넣기 렌더 준비144.70ms는 native GC와의 전체 프레임 교집합이0ms였다(패킹113.33ms/자원 생성29.34ms). 따라서 남은 긴 렌더 준비를 GC나 언어 자체의 한계로 단정할 수 없다.

| 바이너리 | SHA256 |
|---|---|
| 공통 앱/진단 | `E73816BBF7B88201268B2C9ED6BD79869CE75416AAFEAC2237D2AE679298B536` |
| Core 이전 | `16DF55F28D08E13037435B3A578F1D7B3B65E3A920AEA42C52C05A2F4772698B` |
| Core 이후 | `D1D8CBB61825B43E9F7D0F6080570CDFFE687E9870BEFB5861D5EEC815A3C9E6` |
| Canvas 이전(세부 계측 포함) | `D71E451C8C34B09056F497E81F11964BB10E93541A165C78D5F0E71DA3529787` |
| Canvas 이후 | `73F50555E9E89C1CED6CDBC86618D790DE44F174684DA2BA504AAA8FE6782767` |

원시 JSON/CSV, native GC 시각, 집계, 해시, GPU 캡처는 [증거 ZIP](data/frame-budget-20261005.zip)에 보관한다. 앞선 탐색용 `before-1/after-1`이나 이전 문서의 다른 바이너리 결과를 이 표의3회 반복에 섞지 않았다.

## 검증과 한계

- PASS: 최종 전체 솔루션 Release 빌드 경고0/오류0. Core218개, Canvas133개, 앱 통합240개(합591개) 테스트 통과. 검사 알고리즘은 변경하지 않아 해당 별도 테스트를 이번 후속에서 재실행하지 않았다.
- PASS: 복제의 경계/scale/회전/원·삼각형·오목 폴리곤·나중에 나타나는 부모·이름·필터·단일 이력. 캐시의 최신 viewport/기록 선택/이미지 복원, 취소·stale·초과 예산·분기 후 수명과 임의 source 값 복원 검증.
- PASS: 패킹 buffer 재사용·역할 배열 독립 소유·실패 후 재사용·형상 참조 해제,2048×17개 변 보존, 원점과1e12 좌표에서 도메인 Outline과 동일한 패킹.
- PASS: 전후6프로세스에서 복제 위치/형상/부모/이름/선택 및 Undo/Redo exact, exit0. native GC 보조2프로세스도 complete/error=null.
- PASS: 최종 RTX DX12 debug에서4만PCB 확대·축소/선택 전후 exit0, debug 메시지/렌더 오류 없음. GPU 캡처2장의 윤곽·역할 색·이름표·선택·미니맵 확인. WARP debug의4만→8만 복제/원본·선택·계층·목록·Undo/Redo exact, exit0. WARP의 debug 경고 수집은 이 Clipboard 진단에서 별도로 하지 않았다.
- PASS: 변경15개 C# 파일의 whitespace·style·명명 검사와 diff 검사. 무관한 기존 전체 형식 검사 실패6개 파일은 수정하지 않았다.
- FAIL: 60FPS 유지. 첫 붙여넣기의 단일 프레임 준비, 일부 개별 GC, UI 대기와 입력 공급 간격이 예산을 초과한다.
- NOT_RUN: 실제 모니터의 표시 FPS·물리 입력 지연·장시간 편집·사용자 ROI 자료·별도 GPU 없는 PC. WARP 기능 실행을 저사양 PC60FPS 보장으로 해석하지 않는다. 8K 라이브/24K 파일의 전체 성능 요구를 이8K 정적 자동 시험으로 대체하지 않는다.

최초 새 공간 블록의 패킹·자원 생성은 아직 한 렌더 프레임에서 실행한다. 다음 구조적 개선은 편집 준비 중 GPU 자료를 작은 배치로 준비하고 **전체 준비 성공 뒤 문서를 원자적으로 게시**하는 방향이다. 기존 장면의 탐색, 취소·오류·장치 재시작·새 입력 처리와 캐시 예산을 함께 검증해야 한다. 이번 변경에 이 준비 프로토콜을 구현했다고 주장하지 않는다.

## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
& ./src/TeachingToolWpf/bin/Release/net10.0-windows/TeachingToolWpf.exe --frame-cadence-check --rois 40000 --cadence-report artifacts/frame-budget/run.json --render-profile artifacts/frame-budget/run-render.csv --metrics artifacts/frame-budget/run-metrics.csv
# 프로세스 종료/stage=complete/error=null 확인 후
./scripts/Summarize-FrameCadence.ps1 -Path artifacts/frame-budget/run.json
# 별도 실행에 --cadence-gc-events를 추가한 보고서는
./scripts/Summarize-GcCadence.ps1 -Path artifacts/frame-budget/run.json
```

## 설계 원칙 리뷰

| 원칙 | 판단 |
|---|---|
| SRP | 위치 규칙은 clipboard, 이력은 EditHistory, 패킹/업로드는 렌더 내부, 측정은 진단에 유지 |
| OCP | 기존 delta fallback과 공개 편집 계약 유지; 새 플랫폼 분기 없음 |
| LSP | 취소·stale·선택/이미지/뷰와 임의 source에 대한 이력 계약을 회귀로 확인 |
| ISP | 공개 인터페이스 확대 없음 |
| DIP | Core에 WPF/DX12/앱 참조 추가 없음 |
| Information Expert | DocumentEdit가 기록값 복원, RoiGeometry가 위치 변환, 렌더 타입이 인스턴스 계산 |
| Creator | 기존 렌더 소유자가 scratch/GPU 자료, 이력 소유자가 최근 쌍을 보관 |
| Controller | EditorSession의 준비/검증/게시 흐름 유지 |
| Low Coupling | 불변 문서와 기존 앱/렌더 경계 유지 |
| High Cohesion | 각 변경은 할당 또는 같은 자료의 반복 준비를 줄이는 책임 안에 한정 |
| Polymorphism | 새 변형 지점/대체 구현이 없어 추가 다형성은 불필요 |
| Pure Fabrication | 기술 캐시는 기존 이력/렌더 책임에 한정 |
| Indirection | 새 중계 계층/서비스 로케이터 없음 |
| Protected Variations | reader 수명·GPU fence·도형/계층·이력 계약 유지 |

설계 원칙 예외 없음. 독립 리뷰에서 발견된 선택 복원과 임의 source의 역방향 캐시 회귀는 기대값을 유지한 테스트를 추가해 수정했다.
