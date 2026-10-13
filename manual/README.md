# 신입 개발자 HTML 실습서

브라우저에서 [index.html](index.html)을 열어 시작한다. HTML 13페이지와 CSS/JavaScript는 오프라인으로 작동한다. 외부 공식 문서 링크만 인터넷이 필요하다. 파일을 옮길 때는 `manual` 전체를 함께 복사한다. 실제 소스/검증 링크까지 쓰려면 저장소 안의 원래 위치를 유지한다.

- 픽셀·좌표·필터 → 설계 원칙 → 편집·영상 수명 → DX12·검사 → 직접 구축·리뷰 순서.
- 수식 기호·단위·숫자 예·실험5개·정답 풀이·용어 검색 제공.
- 본문에 실제 소스16곳(전체 파일/연속 발췌)을 포함한다. 짧은 코드는 펼쳐 두고 긴 코드는 같은 페이지에서 펼친다. 9장은 실습 두 프로젝트의 설정과 C# 파일 전체를 포함하므로 소스 링크로 이동하지 않아도 읽을 수 있다.
- 6장은 타일 주소·가장자리·가시 타일 계산·피라미드/DPI·부분 읽기·캐시·세대·halo를 손계산, 실제 코드, 이동 실험으로 설명한다. 표시용 단계와 검사 원본, 교육용 설계와 현재 구현을 구분한다.
- `labs/Foundations`: GPU 없는 좌표·이력 실습. `labs/CanvasHost`: 8K 합성 타일을 표시하는 작은 WPF 호스트.
- 실행법은 [9장](09-build.html), 변경/검증과 미검증 범위는 [검증 기록](../docs/beginner-manual-validation.md).

본문을 수정할 때는 해당 HTML을 편집한다. 공통 화면/동작은 `assets/manual.css`, `assets/manual.js`에 있다. 서버·CDN·추가 문서 빌드 도구는 필요 없다. 장 제목/경로를 변경하면 모든 페이지의 차례와 JS의 첫 화면 카드 목록도 함께 갱신한다.

코드 블록은 파일을 실행 시 가져오지 않고 HTML에 직접 저장한다. `code[data-source]`는 원본 상대 경로, `data-kind`는 `full`(파일 전체) 또는 `excerpt`(연속 발췌)다. 원본을 바꾸면 해당 HTML 코드도 함께 갱신하고 HTML 이스케이프를 적용한다. 검증에서는 HTML을 디코딩한 코드와 실제 파일을 비교했다. 3장의 짧은 이진화 코드는 제품 코드 발췌가 아닌 별도 교육 예제다.

수식은 HTML 안의 표준 MathML로 작성한다. 분수(`mfrac`), 제곱근(`msqrt`), 상하 첨자(`msub`/`msup`), 합의 범위(`munderover`), 평균 윗줄(`mover`), 행렬(`mtable`)을 사용한다. 기본 시스템 수학 글꼴을 쓰며 CDN·웹 폰트·JavaScript 렌더러 다운로드는 없다. MathML을 지원하는 현대 브라우저를 대상으로 한다. [MDN MathML 안내](https://developer.mozilla.org/en-US/docs/Web/MathML), [행렬과 수식 정렬](https://developer.mozilla.org/en-US/docs/Web/MathML/Tutorials/For_beginners/Tables).

수식 수정 시 표시 구조와 `aria-label`/`annotation`의 선형 설명을 함께 갱신한다. 긴 수식 상자는 키보드로 초점을 맞춰 가로로 읽을 수 있고, 인쇄 스타일은 흰 배경과 작은 수식 크기를 사용한다. 실제 브라우저·보조공학·PDF 결과는 정적 마크업 검사와 구별한다.
