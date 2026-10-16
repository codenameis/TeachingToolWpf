# WPF에서 세 CPU 매칭 방식 비교

**CPU+CUDA 후속:** 세 방식의 이름에 `CPU/CUDA`를 표시하며, `상세 설정 → 후보 예산 · 실행 제어 → 템플릿 검사 실행 장치`에서 CPU 또는 CPU+CUDA를 선택한다. 모델 재학습 없이 방식별로 선택을 유지한다. [최신 검증·속도·한계](cpu-matching-cuda.md), [쉽게 읽는 알고리즘](cpu-matching-algorithm-guide.md). 아래 최초 연결 시점의 CPU 전용 설명은 이 후속으로 대체한다.

**현재 화면:** [오른쪽 패널 사용법](inspection-sidebar.md)을 따른다. 아래 이전 명칭의 학습/검색은 `검사 → 기본`, CPU 배율·정밀 검색과 후보 설정은 `상세 설정`, 위치·점수는 `결과`, 시간/JSON은 `로그`에 있다. ROI 검색 여유는 `영역` 탭에서 변경한다.

**정확도 우선 후속:** 아래 표는 최초 연결 시점의 기록이다. 이후 같은 점수0.75에서 Chamfer의 Kensa 추가 오검출을 수정하고 대상 없는 영역과 추가 합성 검증을 더했다. 최신 결과는 [정확도 보정·재측정](cpu-matching-accuracy.md), 설계는 [ADR-056](decisions/0056-cpu-matching-accuracy-first.md)을 따른다. 기존 실패 수치와 화면은 비교 이력으로 남긴다.

2026-10-16. **패턴 학습·검사 → 매칭 방식**에 `CPU · Line2D 방향 응답`, `CPU · Sub-NCC 선택 픽셀`, `CPU · Directional Chamfer`를 추가했다. 기존 방식과 별도로 각 모델·학습 ROI·검색 설정을 보관한다. 모델은 앱 세션 안에서 이미지 교체 후에도 유지되며 모델 파일 저장은 추가하지 않았다. [알고리즘 구현 범위](cpu-template-matching.md), [ADR-055](decisions/0055-cpu-matchers-in-wpf.md).

## 사용법

1. 새 빌드의 프로그램에서 `kensa.png`를 연다. 실행 중인 이전 프로그램은 이 변경을 자동 로드하지 않는다.
2. 사각형 ROI를 선택한다. 18V 재현 좌표는 중심 **X194.5 / Y371 / 너비19 / 높이12 / 각도0°**. 부품은 **X200.5 / Y184 / 너비89 / 높이52 / 각도0°**다.
3. CPU 방식을 선택하고 **학습**한다. 해당 ROI의 템플릿 등록이며 신경망 학습이 아니다.
4. `선택 ROI 안에서 기준점 검색`을 켜고 ROI 속성의 검색 범위를 설정한다. 18V는 좌우 여백50.5/상하44로120×100, 부품은 좌우83.5/상하102로256×256이 된다. 다른 ROI를 선택해 기존 템플릿을 그 안에서 찾을 수도 있다.
5. `CPU 검색 설정`과 공통 점수/후보 설정을 조절하고 **검사 실행**한다. 방식마다 한 번 학습하고 돌아오면 저장된 모델/설정을 재사용한다.
6. 원본 X/Y·영상 기준 시계 방향 각도·배율·점수를 확인한다. `검사 시간 로그`에서 읽기/검색/게시/표시 시간을 보고 JSON으로 저장한다.

|파라미터|범위·의미|
|---|---|
|최소/최대 각도|학습 ROI 기준 편차, 각 값-360~360°, 범위 폭 최대360°|
|각도 간격|0.25~30°, 원본 정밀화 포함|
|최소/최대 배율|0.5~2, 두 값1이면 배율 고정|
|배율 간격|0.01~0.5|
|피라미드|1~5단계, 작은 템플릿은 자동으로 적게 사용|
|중복 억제 반경|원본1px 이상, 기본8px|
|최소 점수|0 초과~1, 기본0.75; 방식별 점수 의미는 다름|
|최대 결과/후보|앱 결과1~1000(기본256), 후보16~16384(기본2048)|
|검색 범위|선택 도형/확장 범위 안의 기준점. OFF는 전체 이미지|
|시간 제한|0~600초,0은 없음. 준비/행/후보 사이에서 취소하므로 즉시 중단 상한 아님|

검색 설정은 재학습 없이 적용된다. **검색 각도 범위는 제품 OK/NG 허용각이 아니다.** 새 세 방식은 위치·자세를 표시하고 제품 판정은 사용자 확인으로 남긴다. 후보/결과 상한·취소·오류는 경고이며 NG로 처리하지 않는다.

마스크 없는 사각형을 각 변4~512px로 학습한다. 실제 추출 크기는 정수 반올림한 크기로 표시한다. 이 문서의 최초 검증은2,097,152px 검색 예산/Kensa1024×1536 기준이었다. **후속 구현은8192×8192 전체 검색과 방식별 파라미터를 지원한다.** [새 사용법·전체8K 검증](cpu-matching-performance.md)을 따른다. ROI/영상을 몰래 축소하지 않는다. 학습 마스크/알고리즘 설정 변경은 재학습 전 검사를 막고 원래 설정으로 복원하면 다시 검사할 수 있다.

## Kensa 비교

Ryzen5 5600H / Windows x64 / .NET10 Release. 매칭은 모두 CPU이며 화면은 RTX3060 DX12다. 점수0.75, 배율1, 각도±30°/10°(179° 시험만±180°), 후보2048, 결과256을 고정했다. 중복 억제는 학습 짧은 변×0.6으로 동일하게 적용했다. 매 검사에서 원본 reader를 열고 읽었다.

실제 WPF 선택 바인딩·학습/검사 명령으로5장면×3방식×3회=45회 실행한다. 회차0은 첫 검사,1~2는 반복이다. JIT·읽기를 제외하지 않는다. 별도 offscreen 창에서 명령 진입→읽기/검색/게시/Present fence와 다음 WPF Rendering 관찰을 구분한다. **물리 클릭 메시지 대기·scan-out 및 정상 위치의 대화형 창125ms 수용 시험은 미검증**이다.

- 원본18V: 세 방식 모두 알려진 위치1개, 해당 대상 위치/각도 오차0.
- 원본 부품256×256: Line2D4개, Sub-NCC4개, Chamfer8개. 학습한 대상 위치/각도 오차0. 캡처에서 Chamfer의 추가4개는 다른 영역의 오검출로 확인했다. 전체 장면의 정밀 수작업 라벨이 없으므로 전수 정확도 합격으로 집계하지 않는다.
- Kensa18V crop의-12° 합성 변환: 각각1개, 위치≤0.15px, 각도≤1°.
- Kensa 부품 crop의14° 합성 변환: 각각1개, 위치≤0.08px, 각도≤0.23°.
- 별도 형상의179° 합성 변환: 각각1개, 위치1px·각도1° 기준 통과. Kensa의180° 현장 촬영 시험은 아니다.

[원시 보고서](cpu-wpf-kensa.json), [부품 Line2D 화면](data/cpu-wpf-component-line2d.png), [Sub-NCC 화면](data/cpu-wpf-component-subncc.png), [Chamfer 화면](data/cpu-wpf-component-chamfer.png). `accuracyPassed`는 기록한 정답의 기준이다. **`CompleteTruth=false`인 원본 Kensa 행은 전체 누락/오검출 합격을 뜻하지 않는다.**

최종 비교의 반복2회 평균(ms)은 아래와 같다. 첫 회와 모든 개별값은 원시 JSON/CSV에 보존했다. 반복 표본이 적고 실행 중인 사용자 앱을 종료하지 않아 성능 순위를 일반화할 수 없다. 별도 빌드·테스트 없이 최종 비교를 실행했지만 렌더/UI 및 CPU 지연 변동이 남아 있다. **제한 영역에서도125ms 목표는 미달**이며 기존 독립 Find 측정과 동일한 시간 경계가 아니다.

|장면|방식|개수|검색|서비스|명령→Present|다음 WPF 관찰|첫 회 WPF 관찰|
|---|---|---:|---:|---:|---:|---:|---:|
|kensa-18v-original|CpuDirectionalChamfer|1|225.9|230.2|266.8|442.9|342.3|
|kensa-18v-original|CpuLine2D|1|183.5|187.5|212.2|765.7|1943|
|kensa-18v-original|CpuSubNcc|1|1415.8|1419.8|1434|1698.6|1632.8|
|kensa-18v-rotation|CpuDirectionalChamfer|1|59.8|64.9|277.6|528.7|532.9|
|kensa-18v-rotation|CpuLine2D|1|45.5|52|244.6|465.2|649.9|
|kensa-18v-rotation|CpuSubNcc|1|153.8|158.6|193.3|447.3|432.6|
|kensa-component-original|CpuDirectionalChamfer|8|236.8|241.2|265.9|531|448.3|
|kensa-component-original|CpuLine2D|4|295.7|300.4|335.2|579.6|464.8|
|kensa-component-original|CpuSubNcc|4|140.9|145.6|209.5|353.9|365.4|
|kensa-component-rotation|CpuDirectionalChamfer|1|77.4|80|238.9|447.8|394.6|
|kensa-component-rotation|CpuLine2D|1|23.3|26.1|261.9|423|382.7|
|kensa-component-rotation|CpuSubNcc|1|41.2|44.5|210.2|463.5|364.8|
|synthetic-179-degrees|CpuDirectionalChamfer|1|114.8|117.4|253.3|539.6|396.8|
|synthetic-179-degrees|CpuLine2D|1|42.1|44.7|247.7|448.8|453.8|
|synthetic-179-degrees|CpuSubNcc|1|118.1|120.8|232.4|366.8|868.9|

## 재현·검증

저장소 루트에서 실행한다. 별도 출력 폴더로 이미 실행 중인 앱을 덮어쓰지 않는다.

```powershell
dotnet build src/TeachingToolWpf/TeachingToolWpf.csproj -c Release --artifacts-path artifacts/cpu-wpf -p:RestorePackagesPath=.packages -p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true
& ./artifacts/cpu-wpf/bin/TeachingToolWpf/release/TeachingToolWpf.exe --cpu-kensa-comparison
```

결과는 `artifacts/cpu-wpf-kensa.json`, 화면은 장면/방법별 PNG다. `kensa.png`와 [이전 테스트 데이터](../samples/CpuMatching.Data/README.md)를 사용한다. 정답으로 검출 위치를 주입하지 않고 검색 창만 검사 조건으로 고정했다.

WPF 관련58개(새 통합4개 포함), 독립 CPU7개, 총65개 회귀 통과. 회전90° 학습의 상대각 검색·지름4px 검색 원의 원본 좌표·0.5px 변환, 마스크/대형검색 거부·취소, 모델/설정 복원, 진행 중 방식 변경 결과 폐기, 잘못된 숫자/범위, 로그의 표시 완료 연결을 확인했다. Release 빌드0경고/0오류. 새 파일/공통 CPU 변경의 포맷·스타일 검사 통과.

전체 `scripts/Format-Code.ps1 -Verify`는 기존 PoseMatch 벤치마크/기존 TDCM VM 등의 공백 규칙에서 실패했다. 상세 출력은 `artifacts/cpu-wpf-format-global.log`에 보존했다. 이번 범위와 무관한 파일을 일괄 재서식하지 않았다.

## 설계 원칙 검토

|원칙|검토|
|---|---|
|SRP|수치 알고리즘은 라이브러리, 원본/좌표 변환은 서비스, 상태/명령은 VM, 화면은 XAML|
|OCP|기존 선택/주입 경계로 추가; 기존 알고리즘 구현 변경 없음|
|LSP|동일 좌표·각도·취소·예산/오류 계약을 세 방식 시험으로 확인|
|ISP|CPU 전용 작은 학습/검색 계약, 기존 공급자에 미사용 메서드 강제 없음|
|DIP|VM은 서비스 계약, App이 구현 주입. Canvas/Core에 공급자 의존성 없음|
|Information Expert|ROI 변환은 RoiGeometry, 픽셀 좌표 변환은 reader 어댑터|
|Creator|App이 서비스, 서비스가 템플릿 생성 및 reader 해제|
|Controller|View 바인딩, VM은 요청과 오래된 응답 폐기 조율|
|Low Coupling|라이브러리에 WPF/OpenCV 타입이 없고 중립 영상/결과 사용|
|High Cohesion|CPU 옵션/명령 partial·로그 데이터·진단을 분리|
|Polymorphism|세 matcher가 ITemplateMatcher 구현|
|Pure Fabrication|검사 서비스가 기술 변환과 수명 담당|
|Indirection|기존 서비스/앱 조립 지점을 통한 라이브러리 연결|
|Protected Variations|표시/문서/이력과 무관하게 알고리즘 선택·시험 가능|

문서·ROI 이력은 변경하지 않는다. 중심 predicate는 최종 NMS/출력 제한 전에 적용한다. coarse 후보 예산은 완전성의 수학적 보장이 아니며 초과 시 경고한다. 설계 원칙 예외 없음. 커밋/푸시 없음.
