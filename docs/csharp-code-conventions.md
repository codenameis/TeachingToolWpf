# Microsoft 기준 C# 코드 컨벤션

2026-10-03 사용자 요청으로 적용했다. 앱, Core, 테스트, 벤치마크의 직접 작성한 C#에 같은 기준을 사용한다. 생성 파일과 외부 라이브러리는 제외한다.

근거는 Microsoft Learn의 [C# 코딩 규칙](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions), [식별자 명명 규칙](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names), [.NET 런타임 코딩 스타일](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)이다. Microsoft 문서의 예제 표시 폭 65자는 문서용 제약이므로 제품 코드의 강제 줄 길이로 적용하지 않는다. 아래는 이 저장소가 선택한 일관된 규칙이다.

## 이름

| 대상 | 규칙 | 예 |
|---|---|---|
| 타입·메서드·지역 함수·속성·이벤트 | PascalCase | `RoiGeometry`, `ApplyTransform`, `Changed` |
| 인터페이스 | I + PascalCase | `IViewportSurface` |
| private/internal 인스턴스 필드 | _camelCase | `_viewport`, `_disposed` |
| private/internal 정적 필드 | s_camelCase | `s_procedure` |
| 상수(지역 상수 포함) | PascalCase | `SizeInBytes`, `SampleCount` |
| 공개/보호 필드·dependency property 식별자 | PascalCase | `ValueProperty` |
| 일반 매개변수·지역 변수 | camelCase | `imageWidth`, `cancellationToken` |
| 위치 기반 record 매개변수 | PascalCase | `PointD(double X, double Y)` |

`const` 규칙은 정적/인스턴스 필드 접두사보다 우선한다. 클래스/구조체의 일반 주 생성자 매개변수는 camelCase다. 불필요한 `this.`는 생략한다. 의미 있는 이름을 쓰며, 외부 API가 요구하는 이름이나 override·인터페이스 구현 계약은 유지한다. 이름 변경은 IDE 심볼 Rename 등 참조를 추적하는 방법으로 수행하고, XAML·리플렉션·직렬화·네이티브 연결에 쓰는 문자열도 확인한다.

## 괄호와 배치

- 공백 4칸, Allman 중괄호, `else`/`catch`/`finally`는 새 줄을 사용한다.
- 한 줄 조건문/반복문에도 중괄호를 쓴다. 블록 람다·예외 처리·여러 부작용을 한 줄로 압축하지 않는다.
- 한 줄에 한 문장, 한 선언에 한 변수를 쓴다. `for` 헤더와 tuple 분해 선언은 예외다. 선언 순서와 `using` 수명은 유지한다.
- 메서드·속성 정의 사이에는 빈 줄을 둔다. 연산자 양쪽과 제어 키워드 뒤에는 공백을 넣고 메서드 호출의 소괄호 안쪽에는 넣지 않는다.
- 산술/논리 우선순위가 섞이면 소괄호로 묶음을 명확히 한다. 괄호를 바꿀 때 평가 순서를 바꾸지 않는다.
- `using`은 파일 범위 namespace 밖에서 `System`부터 정렬한다. 단순 식 본문 멤버, collection expression, target-typed `new` 등 기존 현대 문법을 유지한다.
- 코드 식별자는 영어, 프로젝트 설명과 새 이유/제약 주석은 한국어로 작성한다. 불필요한 주석으로 코드를 반복하지 않는다.

```csharp
private int _acceptedPointCount;
private const int MinimumPointCount = 3;

public void ApplySelection(int pointCount)
{
    if (pointCount < MinimumPointCount)
    {
        return;
    }

    _acceptedPointCount = pointCount;
}
```

위 코드는 명명과 문법 배치 예시다.

## 자동 적용과 확인

솔루션을 한 번 복원한 뒤 저장소 루트에서 실행한다. 도구는 설치된 .NET SDK의 `dotnet format`을 사용하며 앱 의존성을 추가하지 않는다.

```powershell
dotnet restore TeachingToolWpf.slnx
pwsh -File scripts/Format-Code.ps1
pwsh -File scripts/Format-Code.ps1 -Verify
```

[`.editorconfig`](../.editorconfig)가 들여쓰기, 중괄호, 수식 괄호, namespace, 주요 명명 규칙을 지정한다. 적용 모드는 자동 수정 가능한 규칙을 고친 뒤 검사하고, `-Verify`는 파일을 바꾸지 않고 위반 시 실패한다. IDE1006의 일괄 이름 수정은 SDK 포맷터가 지원하지 않으므로 심볼 Rename으로 고친다. 의미 있는 이름, 일반 지역 변수/매개변수와 record 매개변수의 구분, 문장·선언 분리, 주석과 읽기 순서는 리뷰로 함께 확인한다. 포맷 검사 통과만으로 모든 품질 원칙을 충족했다고 판단하지 않는다.

## 이번 변경의 원칙 검토

명명·배치 정리이며 기존 MVVM, Command/Memento, State, Strategy/Adapter와 Composite 경계를 유지한다. 원칙의 성능 예외는 적용하지 않았다.

| 원칙 | 검토 결과 |
|---|---|
| SRP | 타입·함수의 책임을 이동하지 않고 이름과 배치만 정리 |
| OCP | 기존 도형·도구·영상 확장 계약 유지 |
| LSP | 입력 검증·취소·오류·이력 계약 유지, 기존 회귀 테스트로 확인 |
| ISP | 인터페이스나 강제 구현 메서드 추가 없음 |
| DIP | Core에 플랫폼 의존성 추가 없음 |
| Information Expert | 기하·포함 판정의 기존 소유 타입 유지 |
| Creator | ROI·GPU 자원 생성/해제 주체와 선언 순서 유지 |
| Controller | UI 요청과 응용 편집 책임의 경계 유지 |
| Low Coupling | 참조를 동반한 필드 Rename, 새 의존성 없음 |
| High Cohesion | 문장/멤버를 분리해 읽기 순서를 드러내고 기능을 섞지 않음 |
| Polymorphism | 기존 도형/도구 구현의 대체 관계 유지 |
| Pure Fabrication | 캐시·이력·준비 작업의 기존 서비스 유지 |
| Indirection | WPF·DX12·파일 어댑터 및 주입 지점 유지 |
| Protected Variations | 외부 계약·XAML 바인딩·GPU 필드 순서와 배치 유지 |

검증 결과는 [작업 목록](../tasks/todo.md)의 이번 컨벤션 항목에 기록한다. 스타일 변경만을 위한 새 동작 테스트는 추가하지 않고 기존 자동 테스트와 Windows/DX12 smoke로 회귀를 확인한다.
