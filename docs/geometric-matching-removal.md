# 자체 기하 패턴 매칭 제거

2026-10-15 사용자가 패턴 매칭의 「기하 형상 · 자체 구현」 전체 삭제를 요청했다. 기존 main의 ROI/MVVM 변경은 보존하고 로컬 수정·검증만 수행한다.

## 변경 범위

- UI는 논문 기반 형상/OpenCV 템플릿 두 방식만 제공한다. 자체 기하의 독립 학습 상태·상세 품질 패널·검출비율/거리 RMS/개수 게이트와 결과 표시를 제거했다.
- `BuildGeometricModel`, `EvaluateGeometry`, `MinimumCoverage`, `MaximumFitErrorPixels`, `MaximumClutterRatio`, `GeometricMatchQuality`, 결과 `Geometry`를 제거했다. 삭제된 enum 값2는 거부한다.
- 논문 방식의 전체 윤곽·마스크와 추가 윤곽 감점은 `ClutterModel`/`ClutterEvaluator`로 보존한다. 대응 거리 이름은 `ClutterTolerancePixels`다. 세부 획 검증·point-to-line 자세 정밀화·회전 NG 정책·출력/후보 경고·CUDA 계산 계약을 유지한다.
- CLI는 `paper`, `template`, `primitives`만 허용한다. `geometric`을 다른 방식으로 자동 실행하지 않는다. 해당 샘플·전용 벤치마크·기하 품질 전용 테스트는 제거했다. 공통 정확성·마스크·점수·취소·CPU/CUDA 동등성 검증은 남겼다.
- ROI 좌표/편집 기하, 독립 일반 도형 검출, 논문 비교 알고리즘은 이번 삭제 대상이 아니다. Classic/Paper는 공통 형상 검색의 구현 비교 경로이며 삭제된 사용자 기하 모드와 동일한 개념이 아니다.

## 호환성과 기록

자체 기하에 대한 공개 옵션/결과 API 삭제는 의도적인 변경이다. 외부 호출자는 논문 방식으로 명시적으로 전환하고 재학습해야 한다. 원본 윤곽 감점 사용 시 `BuildClutterModel=true`를 지정한다. 기존 JSON의 삭제된 옵션은 unknown-field 오류로 거부한다. `geometricTolerancePixels`는 `clutterTolerancePixels`로 수정해야 한다. 자동으로 조건을 무시하지 않는다.

ADR-019/020/025 및 옛 성능 문서는 당시 실험 이력으로 보존한다. 현재 사용법은 README/매칭 파라미터/CLI README를 갱신했다. 이번 제거로 125ms/80ms 목표를 달성했다고 주장하지 않는다.

## 검증

최종 전체 Release 빌드는 경고0/오류0이며 `artifacts/geometric-final-build.log`에 기록했다. 전체 1,228개(Core357·앱277·Algorithm194·Canvas235·Compute52·Inspection113)가 통과했다. 최종 테스트 로그는 `artifacts/geometric-final-tests.log`다. 대상 프레임워크는 .NET10이며 설치된 SDK11.0.100-rc.2가 선택된 환경에서 검증했다. 테스트 개수 감소는 삭제한 기하 전용 검증과 기하 모드 조합의 제거 때문이다. 남겨진 방식의 합격 조건을 낮추지 않았다. 새 회귀는 두 방식만 노출·삭제 enum 거부·CLI 삭제 방식/옵션 거부·윤곽 거리 입력 검증이다.

실제 앱 자동 검증은 별도 Release 실행본에서 `--inspection-smoke`로 수행했고 종료코드0이다. RTX3060 Laptop GPU에서 선택기2개/기하 패널 부재, 논문/OpenCV256핀, CUDA 검사, 특징 표시·네 가지 판정 색상·빈 영역 NG·다각형 경계·독립 일반 도형4종을 확인했다. 로그와 화면은 `artifacts/geometric-smoke-work/artifacts/inspection-smoke.log` 및 같은 폴더의 PNG다. 기존 사용자 앱은 종료하지 않았다. 화면 검증 후 변경은 삭제한 개수 제한에 대한 툴팁 설명과 진단 설명 문자열 정리뿐이며 최종 재빌드했다.

변경 C#50개 파일에 한정한 whitespace 검사와 `git diff --check`가 통과했다. 별도 PC/Linux/현장 영상 및 성능 목표 재수용은 NOT_RUN이다. 검사 smoke의 시간은 목표 달성 증거로 사용하지 않는다.

## SOLID / GRASP 검토

- SRP: 자체 기하 판정 책임 제거, 공통 윤곽 감점은 독립 타입 유지. OCP: 기존 논문/OpenCV 전략 유지. LSP: 남긴 서비스의 취소/원본/소유권 계약 유지 및 회귀. ISP: 제거한 기능의 옵션/결과도 제거. DIP: UI/검사/Core 공급자 참조 경계 변경 없음.
- Information Expert: 윤곽 계산은 알고리즘의 모델/평가기에 유지. Creator: 모델·GPU/reader 소유자 유지. Controller: UI는 기존 VM 명령 사용. Low Coupling: 전용 상태/결과 연결 제거. High Cohesion: Clutter 타입은 추가 윤곽 책임만 보유. Polymorphism: 두 패턴 방식과 기존 계산 공급자 유지. Pure Fabrication: 새로운 서비스/계층 추가 없음. Indirection: 기존 앱 조립과 영상 어댑터 유지. Protected Variations: CLI가 삭제된 설정을 명시적으로 거부하고 기존 두 방식의 독립 모델/설정·수명을 유지.

새 의존성·설계 원칙 예외·커밋/푸시는 없다.
