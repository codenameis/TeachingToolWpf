# 헤드리스 검사 CLI

WPF·DX12 없이 원본 파일을 학습하고 검사한 결과를 JSON으로 저장한다. 모델을 영구 저장하는 형식은 이번 범위가 아니며, 패턴 레시피 실행마다 학습 후 검사한다.

저장소 루트에서 실행한다.

```text
dotnet run --project src/VisionInspection.Cli -c Release -- --recipe samples/inspection/primitives.json --output artifacts/primitives-result.json
dotnet run --project src/VisionInspection.Cli -c Release -- --recipe samples/inspection/paper.json --output artifacts/paper-result.json
```

`samples/inspection`에는 `paper`, `template`, `primitives` 예제가 있다. 이미지 상대 경로는 **레시피 파일이 있는 디렉터리**를 기준으로 해석한다. `--recipe`와 `--output`의 상대 경로는 프로세스의 현재 디렉터리 기준이다. 이미지 크기는 레시피에서 받지 않고 실제 원본 로더의 `ITileSource.Info`에서 확인한다.

## 레시피 1

| 필드 | 의미 |
|---|---|
| `schemaVersion` | `1` |
| `method` | `paper`, `template`, `primitives` |
| `inspectionImage` | 검사할 원본 파일 경로 |
| `trainingImage`, `teachingRoi` | 패턴 두 방식에서 필수. 도형 검출에는 지정하지 않음 |
| `searchRoi` | 생략하면 전체 원본 검색. 패턴은 기준점 위치, 도형 검출은 전체 윤곽 포함 기준 |
| `trainingOptions` | `VisionAlgorithm.TrainingOptions`의 공개 설정. 회전 편차·배율·특징·gradient·피라미드 예산 |
| `searchOptions` | `SearchOptions`의 공개 설정. 점수·추가 윤곽 감점·후보/출력 예산·병렬도·실행 장치. `region` 대신 `searchRoi` 사용 |
| `primitiveOptions` | `PrimitiveDetectionOptions`의 공개 설정. 이진화·크기·종류·점수·출력 상한 |
| `timeoutSeconds` | `0`은 한도 없음, 최대 `600`. 파일 준비·학습·검색 전체에 적용 |

프로퍼티 이름은 대소문자를 구별하지 않으며 예제는 camelCase다. 알려지지 않은 필드와 숫자 enum은 거부한다. `searchOptions`를 일부만 지정해도 생략한 필드는 CLI 기본 출력 256개/추가 윤곽 비중 1을 유지하며, 두 값도 명시적으로 지정하면 그 값을 사용한다. 다른 생략 필드는 해당 알고리즘 계약의 기본값을 사용한다. enum은 `cpu`, `gpuPreprocessing`, `preserve`, `ignoreLocal`, `auto`, `bright`, `dark`, `all` 같은 문자열이다. 양의 무한대 기본값인 `maximumFitErrorPixels`는 한도를 적용할 때만 유한한 양수를 지정한다. 공개 JSON 설정으로 레시피를 직렬화할 때 무제한 값은 생략하고, 읽을 때 같은 기본값으로 복원한다.

`paper`는 추가 윤곽 점수를 위해 `BuildClutterModel=true`를 적용한다. 삭제된 `geometric` 방식은 실행 오류로 거부한다. 삭제된 기하 옵션 필드도 JSON 오류로 거부하므로 기존 레시피는 `paper`로 명시적으로 전환하고 재학습해야 한다. 윤곽 대응 허용 거리는 `clutterTolerancePixels`다. `template`은 회전·크기가 고정된 NCC다.

## ROI 좌표

사각형·삼각형·원은 `shape`, `centerX`, `centerY`, `width`, `height`, 선택형 `angleDegrees`로 지정한다. 좌표는 원본 픽셀이고 회전은 시계 방향 양수다. 원은 너비와 높이가 같은 지름이어야 한다. 삼각형은 회전 전 상단 중앙·우하단·좌하단 꼭짓점을 가진다.

폴리곤은 다음처럼 원본 좌표 꼭짓점만 지정한다. 별도 중심·폭·높이·회전은 허용하지 않으며, 자기 교차·중복 점·0 면적은 거부한다. ROI가 이미지 밖이면 실패하고 자동 축소하지 않는다.

```json
{
  "shape": "polygon",
  "vertices": [{"x":120,"y":120}, {"x":200,"y":120}, {"x":190,"y":200}, {"x":120,"y":200}]
}
```

## 결과와 종료 코드

| `status` | 종료 코드 | 의미 |
|---|---:|---|
| `completed` | 0 | 설정한 검색을 완료. 0개 검출도 가능하며 제품 OK를 뜻하지 않음 |
| `incomplete` | 2 | 후보·출력 상한 또는 잘린 도형으로 검색 불완전 |
| `cancelled` | 3 | Ctrl+C 또는 시간 초과. `cancellationReason`은 `requested`/`timeout` |
| `failed` | 1 | 입력·설정·네이티브 런타임·실행 오류 |

`matches`에는 원본 기준 중심, 절대 라디안 각도, 배율, 점수, 암묵적으로 닫힌 윤곽을 반환한다. 패턴의 `contourSource=transformedTeachingRoi`는 실제 학습 ROI 도형을 pose로 변환한 경계이며, 영상에서 재측정한 경계가 아니다. 원 ROI의 표시 윤곽은 128개 점으로 근사하지만 학습/검색 마스크는 해석적 원을 유지한다. 도형 검출의 `detectedBinaryContour`는 실제 이진 영상 윤곽이다. 정밀화 잔차가 측정 불가능한 경우 JSON에서 `null`이며 0으로 바꾸지 않는다.

`diagnostics`에는 원본 읽기·학습·검색 시간, 후보 상한, 출력 상한, 창별 진단 및 실제 GPU 전처리 정보를 담는다. `incomplete`와 `status`를 확인한 뒤 결과를 소비해야 한다. 제품의 합격/불합격 정책은 이 CLI가 결정하지 않는다.

원본 파일과 레시피를 결과 경로로 지정하면 실행 전에 거부한다. 심볼릭 링크를 통한 동일 경로도 확인한다. 완성된 JSON을 결과 디렉터리의 임시 파일에 기록·flush한 뒤 이동하므로 처리 도중 부분 JSON으로 기존 결과를 대체하지 않는다. 검사 오류·취소도 결과 JSON으로 남긴다. 레시피 자체가 읽히지 않거나 JSON 구문/필드가 잘못됐거나 결과 경로가 안전하지 않으면 stderr와 종료 코드만 반환하고 결과 파일은 변경하지 않는다. 장치/native 호출은 안전하게 종료된 뒤 취소를 보고하므로 시간 한도는 강제 프로세스 종료 시각이 아니다.

## 실행 장치와 배포

기본 자체 파일 로더는 [코덱 빌드](../../experiments/VisionImaging.Codecs/README.md) 후 이 프로젝트를 빌드/게시한다. [NetVips 제거](../../docs/netvips-removal.md). native 파일 준비와 검사 CPU/CUDA 선택은 별개다. Windows의4개 예제는 두 로더의 검출 결과가 일치했다. Linux 실행은 환경 준비 후 검증한다.

기본은 CPU다. `paper`의 `searchOptions.computeMode="gpuPreprocessing"` 문자열은 기존 레시피 호환성을 위해 유지한다. 현재 CLI는 `CudaInspectionSession`의 lease를 주입하며 CUDA가 방향 응답·템플릿 후보 점수를, CPU가 전처리·연속 검증·원본 최종 판정을 수행한다. `diagnostics.candidateScoring`에서 실제 adapter·호출 수·전송 포함 시간을 확인한다. CLI는 한 번의 실행이므로 최초 장치 준비도 전체 검사 시간에 포함한다. NVIDIA CUDA GPU/드라이버가 필요하고 사용 불가 시 자동 폴백하지 않는다. `template`/`primitives`의 GPU 선택은 현재 오류로 보고한다.

`build/InspectionNativeRuntime.props`가 호스트 또는 명시한 RID에 맞춰 OpenCV native 패키지를 선택한다. 지원 배포 RID는 현재 `win-x64`, `linux-x64`다.

```text
dotnet publish src/VisionInspection.Cli -c Release -r linux-x64 --self-contained false -o artifacts/publish-inspection-linux
```

Linux RID 게시물 생성과 실제 Linux 실행은 별도 검증이다. 이 프로젝트가 `net10.0`을 대상으로 한다는 사실만으로 Linux의 native ABI·드라이버·이미지 디코드·CUDA 실행이 검증된 것으로 간주하지 않는다.

통합 코드에서는 `RecipeRunner.RunAsync(recipe, recipeDirectory, token)`으로 결과 객체를 받고, 파일 실행·원자적 결과 저장까지 필요하면 `RecipeFileRunner.RunAsync(recipePath, outputPath, token)`을 사용한다. 테스트는 동일 공개 계약을 사용한다.
