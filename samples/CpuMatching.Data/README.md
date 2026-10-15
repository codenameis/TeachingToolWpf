# CPU 매칭 공통 테스트 데이터

PNG는 Gray8입니다. cases.json에 템플릿, 검색 이미지, 검색 조건, 정답 중심/시계 방향 각도/배율이 있습니다. 정답 좌표는 픽셀 인덱스입니다. 합성 장면은 고정 seed 8451와 OpenCV WarpAffine로 생성했습니다. 원본 Kensa crop 시험과 Kensa crop을 변환한 합성 시험을 구분하세요. 현장 검출률 데이터셋이 아닙니다. 평가 기준은 위치 1px, 각도 1도이며 점수는 각 알고리즘 내에서만 해석합니다.

index.html을 브라우저에서 열면 장면과 결과를 함께 볼 수 있습니다. results.csv는 요약이고 results.json이 원시 측정입니다. Development 12장, Validation 3장, 전체 라벨 미확정 Kensa 원본 1장입니다. 새 matcher의 합성15장 PASS는 현장 품질 보장이 아닙니다.

저장소 루트 실행: dotnet run --project tools/CpuMatching.Evaluation -c Release -- run samples/CpuMatching.Data artifacts/cpu-results.json all 3

측정은 CPU 영상 준비/검색/보정 포함, PNG 읽기와 UI 표시 제외입니다. 프로젝트 안내: docs/cpu-template-matching.md
