/* ==========================================================================
   GeminiManual v2 — Shared Interactive Simulation & Textbook Typesetting Engine
   Provides:
   1. Light/Dark Textbook Theme Switcher (Default: Crisp Light Textbook Theme)
   2. Automatic chapter navigation & section table-of-contents rendering
   3. HiDPI HTML5 Canvas setup & pointer coordinate normalization
   4. Interactive mathematical & Kent Beck Composed Method visual simulators
   ========================================================================== */

(function () {
  "use strict";

  const CHAPTERS = [
    { id: "index", num: "00", file: "index.html", title: "전체 로드맵 & 아키텍처 청사진" },
    { id: "ch01", num: "01", file: "01-csharp-and-math-prereq.html", title: "사전지식: C# 실전 문법 & 2D 벡터 수학" },
    { id: "ch02", num: "02", file: "02-viewport-and-coordinates.html", title: "좌표계와 뷰포트 변환 직접 만들기" },
    { id: "ch03", num: "03", file: "03-roi-shapes-and-polygons.html", title: "도형 엔진(사각형·원·폴리곤)과 폐루프 수학" },
    { id: "ch04", num: "04", file: "04-motion-bounds-and-collision.html", title: "부모-자식 경계 충돌과 이동·회전 한계 방정식" },
    { id: "ch05", num: "05", file: "05-spatial-index-and-100m-rois.html", title: "공간 색인(BVH)과 1억 개 ROI 압축 격자" },
    { id: "ch06", num: "06", file: "06-hit-testing-and-gestures.html", title: "히트테스트와 입력 제스처 상태 머신" },
    { id: "ch07", num: "07", file: "07-immutable-document-and-history.html", title: "불변 문서(DocumentState)와 Undo/Redo·그룹" },
    { id: "ch08", num: "08", file: "08-hwnd-viewport-and-render-loop.html", title: "Win32 HWND 뷰포트와 전용 렌더 스레드" },
    { id: "ch09", num: "09", file: "09-gpu-roi-pipeline-and-tiles.html", title: "타일 피라미드 LOD와 GPU ROI 인스턴싱" },
    { id: "ch10", num: "10", file: "10-mvvm-integration-and-build-guide.html", title: "WPF MVVM 조립과 밑바닥부터 직접 코딩하기" }
  ];

  function initTheme() {
    const saved = localStorage.getItem("gemini-manual-theme") || "light";
    document.documentElement.setAttribute("data-theme", saved);
  }

  initTheme();

  function setupHiDPICanvas(canvas) {
    if (!canvas) return null;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(320, Math.round(rect.width || canvas.clientWidth || 760));
    const height = Math.max(240, Math.round(rect.height || canvas.clientHeight || 350));
    const targetW = Math.round(width * dpr);
    const targetH = Math.round(height * dpr);
    if (canvas.width !== targetW || canvas.height !== targetH) {
      canvas.width = targetW;
      canvas.height = targetH;
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx, width, height, dpr };
  }

  function renderNavigation() {
    const currentFile = (window.location.pathname.split("/").pop() || "index.html").toLowerCase();
    const topPills = document.getElementById("top-chapter-pills");
    if (topPills) {
      topPills.innerHTML = CHAPTERS.map(ch => {
        const active = ch.file.toLowerCase() === currentFile ? "active" : "";
        return `<a class="${active}" href="${ch.file}" title="${ch.title}">${ch.num}</a>`;
      }).join("");

      const parent = topPills.parentElement;
      if (parent && !document.getElementById("theme-toggle-btn")) {
        const btn = document.createElement("button");
        btn.id = "theme-toggle-btn";
        btn.className = "theme-toggle-btn";
        const updateLabel = () => {
          const cur = document.documentElement.getAttribute("data-theme") || "light";
          btn.textContent = cur === "dark" ? "☀️ 교과서 라이트 모드" : "🌙 다크 모드 전환";
        };
        updateLabel();
        btn.addEventListener("click", () => {
          const cur = document.documentElement.getAttribute("data-theme") || "light";
          const next = cur === "dark" ? "light" : "dark";
          document.documentElement.setAttribute("data-theme", next);
          localStorage.setItem("gemini-manual-theme", next);
          updateLabel();
        });
        parent.appendChild(btn);
      }
    }

    const sidebarList = document.getElementById("sidebar-chapter-list");
    if (sidebarList) {
      sidebarList.innerHTML = CHAPTERS.map(ch => {
        const active = ch.file.toLowerCase() === currentFile ? "active" : "";
        return `<li><a class="${active}" href="${ch.file}"><span class="chap-num">${ch.num}</span><span>${ch.title}</span></a></li>`;
      }).join("");
    }

    const tocList = document.getElementById("page-toc-list");
    if (tocList) {
      const headings = Array.from(document.querySelectorAll(".main-article h2[id]"));
      tocList.innerHTML = headings.map(h => `<li><a href="#${h.id}">${h.textContent}</a></li>`).join("");
    }
  }

  /* ========================================================================
     00. Index Page Simulator — 4-Layer Architecture & Composed Method Flow
     ======================================================================== */
  function initArchFlowCanvas() {
    const canvas = document.getElementById("arch-flow-canvas");
    if (!canvas) return;
    const readout = document.getElementById("arch-flow-readout");
    let stepIndex = 0;
    let t = 0;

    const stages = [
      {
        layer: "1단계: Win32 HWND 입력 (VisionCanvas)",
        method: "Dx12Viewport.WndProc → DispatchInputMessage → HandlePointerDown",
        detail: "마우스 물리 픽셀 좌표를 DIP로 바꾸고 InteractionController.PointerDown(dip)을 호출합니다."
      },
      {
        layer: "2단계: 히트테스트 & 제스처 시작 (VisionCanvas.Core)",
        method: "RoiHitTester.HitSelectable → CollectSelectableCandidates → StartSelectionGesture",
        detail: "ViewportTransform.ScreenToImage(dip)로 이미지 좌표를 구한 뒤 BVH와 1억 격자에서 후보를 찾습니다."
      },
      {
        layer: "3단계: 순수 기하학 & 경계 제약 (VisionGeometry)",
        method: "RoiMotionBounds.TranslationFraction → FitsTranslationStep / RotationLimit",
        detail: "드래그 이동량(Δ)이 부모 ROI나 이미지 경계를 뚫지 않도록 이분법과 삼각방정식으로 안전 비율을 계산합니다."
      },
      {
        layer: "4단계: 전용 렌더 스레드 & GPU 출력 (VisionCanvas)",
        method: "RenderWorker.Run → ScenePainter.Build → RoiPipeline.GetOrUploadBlock",
        detail: "배경 타일 피라미드(LOD), 필터 프리뷰, 4096개 단위 GPU 인스턴스 블록, 핸들, 미니맵을 순서대로 그립니다."
      }
    ];

    document.querySelectorAll("[data-arch-step]").forEach(btn => {
      btn.addEventListener("click", () => {
        stepIndex = Number(btn.getAttribute("data-arch-step")) || 0;
        document.querySelectorAll("[data-arch-step]").forEach(b => b.classList.toggle("active", b === btn));
      });
    });

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      t += 0.03;

      ctx.clearRect(0, 0, width, height);
      const topPad = 14;
      const boxH = 58;
      const gapY = 26;
      const boxes = [
        { x: 28, y: topPad + 0 * (boxH + gapY), w: width - 56, h: boxH, color: "#f472b6", name: "1계층 · TeachingToolWpf.Presentation (WPF MVVM / MainWindowViewModel)", desc: "단축키 · 도구 전환 · 검사 사이드바 · Undo/Redo 명령 바인딩" },
        { x: 28, y: topPad + 1 * (boxH + gapY), w: width - 56, h: boxH, color: "#38bdf8", name: "2계층 · VisionCanvas.Host & 4대 백엔드 (HWND · 렌더 워커 · GPU 파이프라인)", desc: "ViewportControlBase · RenderWorkerBase · ScenePainter · RoiBlockCache · RoiPipeline" },
        { x: 28, y: topPad + 2 * (boxH + gapY), w: width - 56, h: boxH, color: "#34d399", name: "3계층 · VisionCanvas.Core (도메인 상태 · 공간 색인 · 제스처)", desc: "DocumentState · EditorSession · RoiHitTester · InteractionController · PolygonClosure" },
        { x: 28, y: topPad + 3 * (boxH + gapY), w: width - 56, h: boxH, color: "#fbbf24", name: "4계층 · VisionGeometry (순수 2D 수학 · 도형 · 연속 충돌 한계)", desc: "PointD · BoundsD · RoiGeometry · RoiShape · RoiMotionBounds (외부 참조 0개!)" }
      ];

      const highlightMap = [1, 2, 3, 1];
      const activeBoxIdx = highlightMap[stepIndex];

      // Draw downward one-way dependency arrows between layers
      for (let i = 0; i < boxes.length - 1; i++) {
        const upper = boxes[i];
        const lower = boxes[i + 1];
        const cx = width * 0.5;
        const y1 = upper.y + upper.h + 2;
        const y2 = lower.y - 3;

        ctx.strokeStyle = "#38bdf8";
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(cx, y1);
        ctx.lineTo(cx, y2 - 5);
        ctx.stroke();

        // Downward arrowhead (▼)
        ctx.fillStyle = "#38bdf8";
        ctx.beginPath();
        ctx.moveTo(cx - 5, y2 - 6);
        ctx.lineTo(cx + 5, y2 - 6);
        ctx.lineTo(cx, y2);
        ctx.closePath();
        ctx.fill();

        ctx.fillStyle = "#94a3b8";
        ctx.font = "bold 11px Pretendard, sans-serif";
        ctx.fillText("단방향 호출 (위 → 아래)", cx + 12, (y1 + y2) * 0.5 + 4);
      }

      boxes.forEach((b, idx) => {
        const isActive = idx === activeBoxIdx;
        ctx.fillStyle = isActive ? "rgba(56, 189, 248, 0.18)" : "rgba(15, 23, 42, 0.9)";
        ctx.strokeStyle = isActive ? b.color : "rgba(148, 163, 184, 0.28)";
        ctx.lineWidth = isActive ? 2.5 : 1.2;
        ctx.beginPath();
        ctx.roundRect(b.x, b.y, b.w, b.h, 10);
        ctx.fill();
        ctx.stroke();

        ctx.fillStyle = b.color;
        ctx.font = "bold 13.5px Pretendard, sans-serif";
        ctx.fillText(b.name, b.x + 16, b.y + 23, b.w - 32);

        ctx.fillStyle = "#cbd5e1";
        ctx.font = "12px Cascadia Code, monospace";
        ctx.fillText(b.desc, b.x + 16, b.y + 44, b.w - 32);

        if (isActive) {
          const pulseX = b.x + 16 + ((Math.sin(t) * 0.5 + 0.5) * (b.w - 60));
          ctx.fillStyle = b.color;
          ctx.beginPath();
          ctx.arc(pulseX, b.y + 10, 4.5, 0, Math.PI * 2);
          ctx.fill();
        }
      });

      if (readout) {
        const st = stages[stepIndex];
        readout.textContent = `[${st.layer}] 호출 체인: ${st.method} — ${st.detail}`;
      }
      requestAnimationFrame(draw);
    }
    requestAnimationFrame(draw);
  }

  /* ========================================================================
     01. Prerequisites Simulator — 2D Vectors, Dot/Cross Product & Rotation
     ======================================================================== */
  function initPrereqVectorCanvas() {
    const canvas = document.getElementById("prereq-vector-canvas");
    if (!canvas) return;
    const angleSlider = document.getElementById("prereq-angle-slider");
    const readout = document.getElementById("prereq-vector-readout");
    let targetPt = { x: 120, y: -70 };
    let dragging = false;

    canvas.addEventListener("pointerdown", e => {
      dragging = true;
      updatePointer(e);
    });
    window.addEventListener("pointerup", () => { dragging = false; });
    canvas.addEventListener("pointermove", e => {
      if (dragging) updatePointer(e);
    });

    function updatePointer(e) {
      const rect = canvas.getBoundingClientRect();
      const cx = rect.width * 0.5;
      const cy = rect.height * 0.52;
      targetPt.x = Math.round(e.clientX - rect.left - cx);
      targetPt.y = Math.round(e.clientY - rect.top - cy);
      draw();
    }

    if (angleSlider) {
      angleSlider.addEventListener("input", draw);
    }

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      const cx = width * 0.5;
      const cy = height * 0.52;
      const deg = angleSlider ? Number(angleSlider.value) : 35;
      const rad = (deg * Math.PI) / 180;
      const c = Math.cos(rad);
      const s = Math.sin(rad);

      ctx.clearRect(0, 0, width, height);

      ctx.strokeStyle = "rgba(148, 163, 184, 0.12)";
      ctx.lineWidth = 1;
      for (let x = cx % 40; x < width; x += 40) {
        ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke();
      }
      for (let y = cy % 40; y < height; y += 40) {
        ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke();
      }

      ctx.strokeStyle = "rgba(148, 163, 184, 0.4)";
      ctx.beginPath();
      ctx.moveTo(0, cy); ctx.lineTo(width, cy);
      ctx.moveTo(cx, 0); ctx.lineTo(cx, height);
      ctx.stroke();

      const axisLen = 110;
      ctx.strokeStyle = "#38bdf8";
      ctx.lineWidth = 2.5;
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.lineTo(cx + c * axisLen, cy + s * axisLen);
      ctx.stroke();
      ctx.fillStyle = "#38bdf8";
      ctx.font = "bold 13px Cascadia Code, monospace";
      ctx.fillText(`u = (cos ${deg}°, sin ${deg}°)`, cx + c * axisLen + 6, cy + s * axisLen);

      ctx.strokeStyle = "#34d399";
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.lineTo(cx - s * axisLen, cy + c * axisLen);
      ctx.stroke();
      ctx.fillStyle = "#34d399";
      ctx.fillText(`v = (-sin ${deg}°, cos ${deg}°)`, cx - s * axisLen + 6, cy + c * axisLen);

      ctx.strokeStyle = "#fbbf24";
      ctx.lineWidth = 2.5;
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.lineTo(cx + targetPt.x, cy + targetPt.y);
      ctx.stroke();

      const dotU = targetPt.x * c + targetPt.y * s;
      const crossU = targetPt.x * s - targetPt.y * c;
      const projX = cx + c * dotU;
      const projY = cy + s * dotU;

      ctx.setLineDash([5, 5]);
      ctx.strokeStyle = "#f472b6";
      ctx.beginPath();
      ctx.moveTo(cx + targetPt.x, cy + targetPt.y);
      ctx.lineTo(projX, projY);
      ctx.stroke();
      ctx.setLineDash([]);

      ctx.fillStyle = "#fbbf24";
      ctx.beginPath();
      ctx.arc(cx + targetPt.x, cy + targetPt.y, 7, 0, Math.PI * 2);
      ctx.fill();

      if (readout) {
        readout.textContent =
          `점 P = (${targetPt.x}, ${targetPt.y}) | 회전각 θ = ${deg}° | ` +
          `내적 P·u (로컬 X 투영) = ${dotU.toFixed(1)} | 외적 P×u (부호 있는 거리) = ${crossU.toFixed(1)}`;
      }
    }
    draw();
  }

  /* ========================================================================
     02. ViewportTransform Simulator — Cursor-Anchored Zoom & Pan
     ======================================================================== */
  function initViewportZoomCanvas() {
    const canvas = document.getElementById("viewport-zoom-canvas");
    if (!canvas) return;
    const readout = document.getElementById("viewport-zoom-readout");
    const resetBtn = document.getElementById("viewport-reset-btn");

    let scale = 1.6;
    let panX = 140;
    let panY = 65;
    let pointerScreen = { x: 340, y: 190 };
    let isPanning = false;
    let panStart = { x: 0, y: 0 };

    function screenToImage(sx, sy) {
      return { x: (sx - panX) / scale, y: (sy - panY) / scale };
    }

    canvas.addEventListener("pointerdown", e => {
      const rect = canvas.getBoundingClientRect();
      isPanning = true;
      panStart = { x: e.clientX - rect.left - panX, y: e.clientY - rect.top - panY };
    });
    window.addEventListener("pointerup", () => { isPanning = false; });
    canvas.addEventListener("pointermove", e => {
      const rect = canvas.getBoundingClientRect();
      pointerScreen = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      if (isPanning) {
        panX = pointerScreen.x - panStart.x;
        panY = pointerScreen.y - panStart.y;
      }
      draw();
    });

    canvas.addEventListener("wheel", e => {
      e.preventDefault();
      const factor = e.deltaY < 0 ? 1.18 : 1 / 1.18;
      const anchorImg = screenToImage(pointerScreen.x, pointerScreen.y);
      scale = Math.min(8.0, Math.max(0.35, scale * factor));
      panX = pointerScreen.x - anchorImg.x * scale;
      panY = pointerScreen.y - anchorImg.y * scale;
      draw();
    }, { passive: false });

    if (resetBtn) {
      resetBtn.addEventListener("click", () => {
        scale = 1.6; panX = 140; panY = 65;
        draw();
      });
    }

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      const imgW = 300;
      const imgH = 180;

      ctx.fillStyle = "rgba(30, 41, 59, 0.9)";
      ctx.strokeStyle = "#38bdf8";
      ctx.lineWidth = 2;
      ctx.fillRect(panX, panY, imgW * scale, imgH * scale);
      ctx.strokeRect(panX, panY, imgW * scale, imgH * scale);

      ctx.strokeStyle = "rgba(56, 189, 248, 0.2)";
      ctx.lineWidth = 1;
      for (let ix = 0; ix <= imgW; ix += 30) {
        const sx = panX + ix * scale;
        ctx.beginPath(); ctx.moveTo(sx, panY); ctx.lineTo(sx, panY + imgH * scale); ctx.stroke();
      }
      for (let iy = 0; iy <= imgH; iy += 30) {
        const sy = panY + iy * scale;
        ctx.beginPath(); ctx.moveTo(panX, sy); ctx.lineTo(panX + imgW * scale, sy); ctx.stroke();
      }

      const rx = panX + 60 * scale;
      const ry = panY + 45 * scale;
      ctx.fillStyle = "rgba(52, 211, 153, 0.22)";
      ctx.strokeStyle = "#34d399";
      ctx.lineWidth = 2;
      ctx.fillRect(rx, ry, 90 * scale, 60 * scale);
      ctx.strokeRect(rx, ry, 90 * scale, 60 * scale);

      const imgPt = screenToImage(pointerScreen.x, pointerScreen.y);
      ctx.strokeStyle = "#fbbf24";
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.arc(pointerScreen.x, pointerScreen.y, 8, 0, Math.PI * 2);
      ctx.moveTo(pointerScreen.x - 14, pointerScreen.y);
      ctx.lineTo(pointerScreen.x + 14, pointerScreen.y);
      ctx.moveTo(pointerScreen.x, pointerScreen.y - 14);
      ctx.lineTo(pointerScreen.x, pointerScreen.y + 14);
      ctx.stroke();

      if (readout) {
        readout.textContent =
          `Screen(DIP) = (${pointerScreen.x.toFixed(0)}, ${pointerScreen.y.toFixed(0)}) | ` +
          `Scale = ${scale.toFixed(2)}x | Pan = (${panX.toFixed(1)}, ${panY.toFixed(1)}) | ` +
          `Image 좌표 = ((Screen - Pan) / Scale) = (${imgPt.x.toFixed(1)}, ${imgPt.y.toFixed(1)})`;
      }
    }
    draw();
  }

  /* ========================================================================
     03. ROI Shapes, Support Function & PolygonClosure Simulator
     ======================================================================== */
  function initShapePolygonCanvas() {
    const canvas = document.getElementById("shape-polygon-canvas");
    if (!canvas) return;
    const readout = document.getElementById("shape-polygon-readout");
    const modeBtns = document.querySelectorAll("[data-shape-mode]");
    const clearPolyBtn = document.getElementById("clear-poly-btn");
    const rotSlider = document.getElementById("shape-rot-slider");

    let mode = "support";
    let polyPts = [
      { x: 180, y: 260 },
      { x: 260, y: 110 },
      { x: 430, y: 120 },
      { x: 470, y: 250 }
    ];
    let hoverPt = { x: 230, y: 220 };

    modeBtns.forEach(btn => {
      btn.addEventListener("click", () => {
        mode = btn.getAttribute("data-shape-mode") || "support";
        modeBtns.forEach(b => b.classList.toggle("active", b === btn));
        draw();
      });
    });

    if (clearPolyBtn) {
      clearPolyBtn.addEventListener("click", () => {
        polyPts = [{ x: 180, y: 250 }, { x: 280, y: 110 }, { x: 430, y: 130 }];
        draw();
      });
    }

    if (rotSlider) rotSlider.addEventListener("input", draw);

    canvas.addEventListener("pointermove", e => {
      const rect = canvas.getBoundingClientRect();
      hoverPt = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      draw();
    });

    canvas.addEventListener("pointerdown", e => {
      if (mode !== "polygon") return;
      const rect = canvas.getBoundingClientRect();
      polyPts.push({ x: e.clientX - rect.left, y: e.clientY - rect.top });
      if (polyPts.length > 9) polyPts.shift();
      draw();
    });

    function segIntersect(a, b, c, d) {
      const r = { x: b.x - a.x, y: b.y - a.y };
      const s = { x: d.x - c.x, y: d.y - c.y };
      const denom = r.x * s.y - r.y * s.x;
      if (Math.abs(denom) < 1e-6) return null;
      const qp = { x: c.x - a.x, y: c.y - a.y };
      const t = (qp.x * s.y - qp.y * s.x) / denom;
      const u = (qp.x * r.y - qp.y * r.x) / denom;
      if (t >= 0 && t <= 1 && u >= 0 && u <= 1) {
        return { x: a.x + t * r.x, y: a.y + t * r.y, t, u };
      }
      return null;
    }

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      if (mode === "support") {
        const cx = width * 0.48;
        const cy = height * 0.52;
        const rw = 190;
        const rh = 110;
        const deg = rotSlider ? Number(rotSlider.value) : 28;
        const rad = (deg * Math.PI) / 180;
        const c = Math.cos(rad);
        const s = Math.sin(rad);

        const aabbHalfW = Math.abs(c) * (rw / 2) + Math.abs(s) * (rh / 2);
        const aabbHalfH = Math.abs(s) * (rw / 2) + Math.abs(c) * (rh / 2);

        ctx.setLineDash([6, 4]);
        ctx.strokeStyle = "#fbbf24";
        ctx.lineWidth = 1.8;
        ctx.strokeRect(cx - aabbHalfW, cy - aabbHalfH, aabbHalfW * 2, aabbHalfH * 2);
        ctx.setLineDash([]);

        ctx.save();
        ctx.translate(cx, cy);
        ctx.rotate(rad);
        ctx.fillStyle = "rgba(56, 189, 248, 0.2)";
        ctx.strokeStyle = "#38bdf8";
        ctx.lineWidth = 2.5;
        ctx.fillRect(-rw / 2, -rh / 2, rw, rh);
        ctx.strokeRect(-rw / 2, -rh / 2, rw, rh);
        ctx.restore();

        const dx = hoverPt.x - cx;
        const dy = hoverPt.y - cy;
        const localX = (dx * c + dy * s) / rw;
        const localY = (-dx * s + dy * c) / rh;
        const inside = Math.abs(localX) <= 0.5 && Math.abs(localY) <= 0.5;

        ctx.fillStyle = inside ? "#34d399" : "#fb7185";
        ctx.beginPath();
        ctx.arc(hoverPt.x, hoverPt.y, 7, 0, Math.PI * 2);
        ctx.fill();

        if (readout) {
          readout.textContent =
            `[Support Function & ToLocal] 회전 θ=${deg}° | AABB 반폭=(${aabbHalfW.toFixed(1)}, ${aabbHalfH.toFixed(1)}) | ` +
            `마우스 정규화 로컬 좌표=(${localX.toFixed(2)}, ${localY.toFixed(2)}) → Contains: ${inside ? "TRUE (내부)" : "FALSE (외부)"}`;
        }
      } else {
        ctx.strokeStyle = "#38bdf8";
        ctx.lineWidth = 2.5;
        ctx.beginPath();
        polyPts.forEach((p, i) => {
          if (i === 0) ctx.moveTo(p.x, p.y);
          else ctx.lineTo(p.x, p.y);
        });
        ctx.stroke();

        const lastPt = polyPts[polyPts.length - 1];
        ctx.setLineDash([5, 5]);
        ctx.strokeStyle = "#94a3b8";
        ctx.beginPath();
        ctx.moveTo(lastPt.x, lastPt.y);
        ctx.lineTo(hoverPt.x, hoverPt.y);
        ctx.stroke();
        ctx.setLineDash([]);

        let hitInfo = null;
        for (let i = 0; i < polyPts.length - 2; i++) {
          const hit = segIntersect(polyPts[i], polyPts[i + 1], lastPt, hoverPt);
          if (hit) {
            hitInfo = { edgeIndex: i, pt: hit };
            break;
          }
        }

        polyPts.forEach((p, i) => {
          ctx.fillStyle = i === 0 ? "#fbbf24" : "#38bdf8";
          ctx.beginPath();
          ctx.arc(p.x, p.y, 5.5, 0, Math.PI * 2);
          ctx.fill();
        });

        if (hitInfo) {
          ctx.fillStyle = "#34d399";
          ctx.beginPath();
          ctx.arc(hitInfo.pt.x, hitInfo.pt.y, 9, 0, Math.PI * 2);
          ctx.fill();
        }

        if (readout) {
          readout.textContent = hitInfo
            ? `[PolygonClosure.Find] 교차 감지! Edge #${hitInfo.edgeIndex}의 (${hitInfo.pt.x.toFixed(1)}, ${hitInfo.pt.y.toFixed(1)})에서 BuildClosedLoop가 폐곡선을 닫습니다.`
            : `[PolygonClosure.Find] 마우스를 기존 변 위로 가져가거나 클릭해 꼭짓점을 추가해 보세요. (꼭짓점 ${polyPts.length}개)`;
        }
      }
    }
    draw();
  }

  /* ========================================================================
     04. RoiMotionBounds Simulator — Translation Bisection & Rotation Limit
     ======================================================================== */
  function initMotionBoundsCanvas() {
    const canvas = document.getElementById("motion-bounds-canvas");
    if (!canvas) return;
    const readout = document.getElementById("motion-bounds-readout");
    const rotDeltaSlider = document.getElementById("motion-rot-slider");

    let desiredPt = { x: 520, y: 90 };

    canvas.addEventListener("pointermove", e => {
      const rect = canvas.getBoundingClientRect();
      desiredPt = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      draw();
    });
    if (rotDeltaSlider) rotDeltaSlider.addEventListener("input", draw);

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      const parent = { x: 110, y: 45, w: width - 220, h: height - 90 };
      ctx.fillStyle = "rgba(15, 23, 42, 0.85)";
      ctx.strokeStyle = "#818cf8";
      ctx.lineWidth = 2.5;
      ctx.fillRect(parent.x, parent.y, parent.w, parent.h);
      ctx.strokeRect(parent.x, parent.y, parent.w, parent.h);

      const startChild = { cx: parent.x + 130, cy: parent.y + parent.h * 0.52, w: 110, h: 64 };
      const deg = rotDeltaSlider ? Number(rotDeltaSlider.value) : 20;
      const rad = (deg * Math.PI) / 180;
      const halfW = Math.abs(Math.cos(rad)) * (startChild.w / 2) + Math.abs(Math.sin(rad)) * (startChild.h / 2);
      const halfH = Math.abs(Math.sin(rad)) * (startChild.w / 2) + Math.abs(Math.cos(rad)) * (startChild.h / 2);

      const delta = { x: desiredPt.x - startChild.cx, y: desiredPt.y - startChild.cy };

      function fitsAt(cx, cy) {
        return (
          cx - halfW >= parent.x &&
          cx + halfW <= parent.x + parent.w &&
          cy - halfH >= parent.y &&
          cy + halfH <= parent.y + parent.h
        );
      }

      let lo = 0.0;
      let hi = 1.0;
      if (fitsAt(startChild.cx + delta.x, startChild.cy + delta.y)) {
        lo = 1.0;
      } else {
        for (let i = 0; i < 40; i++) {
          const mid = 0.5 * (lo + hi);
          if (fitsAt(startChild.cx + delta.x * mid, startChild.cy + delta.y * mid)) lo = mid;
          else hi = mid;
        }
      }

      const directPos = { x: startChild.cx + delta.x * lo, y: startChild.cy + delta.y * lo };
      const clampedSlide = {
        x: Math.min(parent.x + parent.w - halfW, Math.max(parent.x + halfW, desiredPt.x)),
        y: Math.min(parent.y + parent.h - halfH, Math.max(parent.y + halfH, desiredPt.y))
      };

      ctx.save();
      ctx.translate(desiredPt.x, desiredPt.y);
      ctx.rotate(rad);
      ctx.strokeStyle = "rgba(251, 113, 133, 0.55)";
      ctx.setLineDash([4, 4]);
      ctx.strokeRect(-startChild.w / 2, -startChild.h / 2, startChild.w, startChild.h);
      ctx.restore();

      ctx.save();
      ctx.translate(directPos.x, directPos.y);
      ctx.rotate(rad);
      ctx.strokeStyle = "#fbbf24";
      ctx.lineWidth = 2;
      ctx.strokeRect(-startChild.w / 2, -startChild.h / 2, startChild.w, startChild.h);
      ctx.restore();

      ctx.save();
      ctx.translate(clampedSlide.x, clampedSlide.y);
      ctx.rotate(rad);
      ctx.fillStyle = "rgba(52, 211, 153, 0.25)";
      ctx.strokeStyle = "#34d399";
      ctx.lineWidth = 2.5;
      ctx.fillRect(-startChild.w / 2, -startChild.h / 2, startChild.w, startChild.h);
      ctx.strokeRect(-startChild.w / 2, -startChild.h / 2, startChild.w, startChild.h);
      ctx.restore();

      if (readout) {
        readout.textContent =
          `[TranslationFraction 40회 이분법] 직선 허용 비율 t = ${lo.toFixed(4)} (노란 상자) | ` +
          `[ConstrainTranslation 벽면 슬라이딩] 최종 위치 = (${clampedSlide.x.toFixed(1)}, ${clampedSlide.y.toFixed(1)}) (초록 상자)`;
      }
    }
    draw();
  }

  /* ========================================================================
     05. Spatial Index (BVH) & BvhNodePath / RoiIdSet Simulator
     ======================================================================== */
  function initSpatialBvhCanvas() {
    const canvas = document.getElementById("spatial-bvh-canvas");
    if (!canvas) return;
    const readout = document.getElementById("spatial-bvh-readout");
    let queryPt = { x: 260, y: 170 };

    canvas.addEventListener("pointermove", e => {
      const rect = canvas.getBoundingClientRect();
      queryPt = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      draw();
    });

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      const splitX = width * 0.48;

      ctx.fillStyle = "#38bdf8";
      ctx.font = "bold 13px Pretendard, sans-serif";
      ctx.fillText("① BVH 트리: RoiSpatialIndex & BvhNodePath", 18, 26, splitX - 28);
      ctx.fillStyle = "#34d399";
      ctx.fillText("② 대량 격자 ROI: 2048 블록 & CreateGridGeometries", splitX + 18, 26, width - splitX - 28);

      const rois = [
        { id: "ROI-A", x: 30, y: 55, w: 95, h: 70 },
        { id: "ROI-B", x: 145, y: 70, w: 110, h: 80 },
        { id: "ROI-C", x: 50, y: 175, w: 120, h: 90 },
        { id: "ROI-D", x: 190, y: 205, w: 95, h: 85 }
      ];

      rois.forEach(r => {
        const hit = queryPt.x >= r.x && queryPt.x <= r.x + r.w && queryPt.y >= r.y && queryPt.y <= r.y + r.h;
        ctx.fillStyle = hit ? "rgba(56, 189, 248, 0.28)" : "rgba(30, 41, 59, 0.7)";
        ctx.strokeStyle = hit ? "#38bdf8" : "rgba(148, 163, 184, 0.35)";
        ctx.lineWidth = hit ? 2.5 : 1.2;
        ctx.fillRect(r.x, r.y, r.w, r.h);
        ctx.strokeRect(r.x, r.y, r.w, r.h);
        ctx.fillStyle = "#e2e8f0";
        ctx.fillText(r.id, r.x + 10, r.y + 24);
      });

      const originX = splitX + 28;
      const originY = 55;
      const stepX = 34;
      const stepY = 28;
      const cellW = 26;
      const cellH = 20;
      const cols = Math.floor((width - originX - 24) / stepX);
      const rows = 9;

      const hitCol = Math.floor((queryPt.x - originX) / stepX);
      const hitRow = Math.floor((queryPt.y - originY) / stepY);
      const validCell = hitCol >= 0 && hitCol < cols && hitRow >= 0 && hitRow < rows;

      for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
          const cx = originX + c * stepX;
          const cy = originY + r * stepY;
          const isHit = validCell && r === hitRow && c === hitCol;
          ctx.fillStyle = isHit ? "rgba(52, 211, 153, 0.45)" : "rgba(15, 23, 42, 0.85)";
          ctx.strokeStyle = isHit ? "#34d399" : "rgba(52, 211, 153, 0.25)";
          ctx.lineWidth = isHit ? 2.2 : 1;
          ctx.fillRect(cx, cy, cellW, cellH);
          ctx.strokeRect(cx, cy, cellW, cellH);
        }
      }

      if (readout) {
        if (validCell) {
          const virtualIdx = hitRow * 10001 + hitCol;
          readout.textContent =
            `[RepeatedRoiGrid O(1) 조회] col = floor((x - OriginX)/StepX) = ${hitCol}, row = ${hitRow} ` +
            `→ 10,001×10,001 격자 내 가상 CellIndex = #${virtualIdx.toLocaleString()} (할당 객체 0개!)`;
        } else {
          readout.textContent =
            `마우스를 왼쪽 BVH 영역 또는 오른쪽 반복 격자 위로 움직여 O(log N) 트리 탐색과 O(1) 격자 수식을 비교해 보세요.`;
        }
      }
    }
    draw();
  }

  /* ========================================================================
     06. HitTesting & Gesture State Machine Simulator
     ======================================================================== */
  function initHitTestGestureCanvas() {
    const canvas = document.getElementById("hittest-gesture-canvas");
    if (!canvas) return;
    const readout = document.getElementById("hittest-gesture-readout");
    let altCycleIdx = 0;
    let pointer = { x: 260, y: 185 };

    const candidates = [
      { id: "Parent-Group (면적 48,000)", x: 140, y: 65, w: 300, h: 210, color: "#818cf8" },
      { id: "Module-ROI (면적 19,200)", x: 185, y: 95, w: 190, h: 135, color: "#38bdf8" },
      { id: "Leaf-Pin-ROI (면적 5,400)", x: 220, y: 125, w: 100, h: 72, color: "#34d399" }
    ];

    canvas.addEventListener("pointermove", e => {
      const rect = canvas.getBoundingClientRect();
      pointer = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      draw();
    });

    canvas.addEventListener("pointerdown", () => {
      altCycleIdx = (altCycleIdx + 1) % candidates.length;
      draw();
    });

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      const hits = candidates.filter(
        c => pointer.x >= c.x && pointer.x <= c.x + c.w && pointer.y >= c.y && pointer.y <= c.y + c.h
      );
      const sortedHits = hits.slice().reverse();
      const chosen = sortedHits.length > 0 ? sortedHits[altCycleIdx % sortedHits.length] : null;

      candidates.forEach(c => {
        const isChosen = chosen && chosen.id === c.id;
        ctx.fillStyle = isChosen ? "rgba(52, 211, 153, 0.22)" : "rgba(15, 23, 42, 0.65)";
        ctx.strokeStyle = c.color;
        ctx.lineWidth = isChosen ? 3 : 1.5;
        ctx.fillRect(c.x, c.y, c.w, c.h);
        ctx.strokeRect(c.x, c.y, c.w, c.h);
        ctx.fillStyle = c.color;
        ctx.font = "bold 12.5px Pretendard, sans-serif";
        ctx.fillText(c.id, c.x + 10, c.y + 22);
      });

      if (readout) {
        if (chosen) {
          readout.textContent =
            `[RoiHitTester.HitSelectable] 겹친 후보 ${sortedHits.length}개 (작은 면적 우선 정렬) | ` +
            `현재 선택: ${chosen.id} (캔버스 클릭 시 Alt 순환 선택 시뮬레이션!)`;
        } else {
          readout.textContent = `마우스를 겹쳐진 ROI 상자 위로 올리고 클릭하여 후보 정렬과 Alt 순환 선택을 확인하세요.`;
        }
      }
    }
    draw();
  }

  /* ========================================================================
     07. Immutable DocumentState & Preview / Undo-Redo Simulator
     ======================================================================== */
  function initImmutableHistoryCanvas() {
    const canvas = document.getElementById("immutable-history-canvas");
    if (!canvas) return;
    const readout = document.getElementById("immutable-history-readout");
    const undoBtn = document.getElementById("sim-undo-btn");
    const redoBtn = document.getElementById("sim-redo-btn");

    let undoStack = [{ x: 160, y: 140 }];
    let redoStack = [];
    let previewDelta = { x: 0, y: 0 };
    let dragging = false;
    let dragStart = { x: 0, y: 0 };

    function currentCommitted() {
      return undoStack[undoStack.length - 1];
    }

    canvas.addEventListener("pointerdown", e => {
      const rect = canvas.getBoundingClientRect();
      dragging = true;
      dragStart = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      previewDelta = { x: 0, y: 0 };
      draw();
    });

    canvas.addEventListener("pointermove", e => {
      if (!dragging) return;
      const rect = canvas.getBoundingClientRect();
      previewDelta = {
        x: e.clientX - rect.left - dragStart.x,
        y: e.clientY - rect.top - dragStart.y
      };
      draw();
    });

    window.addEventListener("pointerup", () => {
      if (!dragging) return;
      dragging = false;
      if (Math.hypot(previewDelta.x, previewDelta.y) > 4) {
        const base = currentCommitted();
        undoStack.push({ x: base.x + previewDelta.x, y: base.y + previewDelta.y });
        redoStack = [];
      }
      previewDelta = { x: 0, y: 0 };
      draw();
    });

    if (undoBtn) {
      undoBtn.addEventListener("click", () => {
        if (undoStack.length > 1) {
          redoStack.push(undoStack.pop());
          draw();
        }
      });
    }
    if (redoBtn) {
      redoBtn.addEventListener("click", () => {
        if (redoStack.length > 0) {
          undoStack.push(redoStack.pop());
          draw();
        }
      });
    }

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      const base = currentCommitted();
      ctx.setLineDash([5, 4]);
      ctx.strokeStyle = "#818cf8";
      ctx.lineWidth = 2;
      ctx.strokeRect(base.x, base.y, 130, 80);
      ctx.setLineDash([]);
      ctx.fillStyle = "#818cf8";
      ctx.font = "12px Cascadia Code, monospace";
      ctx.fillText(`Committed DocumentState #v${undoStack.length}`, base.x, base.y - 8);

      const px = base.x + previewDelta.x;
      const py = base.y + previewDelta.y;
      ctx.fillStyle = dragging ? "rgba(56, 189, 248, 0.28)" : "rgba(52, 211, 153, 0.24)";
      ctx.strokeStyle = dragging ? "#38bdf8" : "#34d399";
      ctx.lineWidth = 2.5;
      ctx.fillRect(px, py, 130, 80);
      ctx.strokeRect(px, py, 130, 80);

      if (readout) {
        readout.textContent = dragging
          ? `[드래그 중: RoiTransformPreview만 갱신!] DocumentState는 불변 유지, Delta = (${previewDelta.x.toFixed(0)}, ${previewDelta.y.toFixed(0)})`
          : `[PointerUp 커밋 완료] Undo 스택: ${undoStack.length - 1}건 | Redo 스택: ${redoStack.length}건 | 위치 = (${base.x.toFixed(0)}, ${base.y.toFixed(0)})`;
      }
    }
    draw();
  }

  /* ========================================================================
     08. ScenePainter 6-Layer Frame Builder Simulator
     ======================================================================== */
  function initHwndRenderLoopCanvas() {
    const canvas = document.getElementById("hwnd-render-canvas");
    if (!canvas) return;
    const readout = document.getElementById("hwnd-render-readout");
    const layerSlider = document.getElementById("scene-layer-slider");

    if (layerSlider) layerSlider.addEventListener("input", draw);

    const layerNames = [
      "1단계: DrawBackgroundImage (배경 타일 피라미드 LOD + 캔버스 테두리)",
      "2단계: DrawFilterRegions (선택 ROI 실시간 필터 프리뷰 텍스처 합성)",
      "3단계: DrawRoiOverlays (GPU 인스턴싱 ROI 블록 + 선택/그룹 오버레이)",
      "4단계: DrawHoverAndSelectionHandles (호버 외곽선 + 8방향 크기/회전 핸들)",
      "5단계: DrawDraftOverlays (그리기 중인 폴리곤/마키 점선 프리뷰)",
      "6단계: DrawSceneLabelsAndMinimap (배지 텍스트 + 우하단 미니맵)"
    ];

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);
      const maxStage = layerSlider ? Number(layerSlider.value) : 6;

      if (maxStage >= 1) {
        ctx.fillStyle = "#111827";
        ctx.fillRect(40, 30, width - 80, height - 60);
        ctx.strokeStyle = "rgba(56, 189, 248, 0.22)";
        for (let x = 40; x < width - 80; x += 90) {
          for (let y = 30; y < height - 60; y += 75) {
            ctx.strokeRect(x, y, 90, 75);
          }
        }
      }
      if (maxStage >= 2) {
        ctx.fillStyle = "rgba(192, 132, 252, 0.25)";
        ctx.fillRect(140, 85, 170, 110);
      }
      if (maxStage >= 3) {
        ctx.strokeStyle = "#34d399";
        ctx.lineWidth = 2.2;
        ctx.strokeRect(140, 85, 170, 110);
        ctx.strokeStyle = "#38bdf8";
        ctx.strokeRect(350, 110, 140, 95);
      }
      if (maxStage >= 4) {
        const pts = [[140, 85], [225, 85], [310, 85], [140, 195], [310, 195]];
        ctx.fillStyle = "#fbbf24";
        pts.forEach(([hx, hy]) => ctx.fillRect(hx - 4, hy - 4, 8, 8));
      }
      if (maxStage >= 5) {
        ctx.setLineDash([5, 4]);
        ctx.strokeStyle = "#fb7185";
        ctx.strokeRect(480, 70, 120, 80);
        ctx.setLineDash([]);
      }
      if (maxStage >= 6) {
        ctx.fillStyle = "#34d399";
        ctx.fillRect(140, 63, 92, 20);
        ctx.fillStyle = "#061024";
        ctx.font = "bold 11px Pretendard, sans-serif";
        ctx.fillText("ROI-01 [OK]", 146, 77);

        const mw = 120, mh = 76;
        const mx = width - mw - 55, my = height - mh - 42;
        ctx.fillStyle = "rgba(8, 13, 26, 0.9)";
        ctx.strokeStyle = "#38bdf8";
        ctx.fillRect(mx, my, mw, mh);
        ctx.strokeRect(mx, my, mw, mh);
      }

      if (readout) {
        readout.textContent = `[ScenePainter.Build 단계별 실행] 현재 표시 레이어 (1~${maxStage}단계): ${layerNames[maxStage - 1]}`;
      }
    }
    draw();
  }

  /* ========================================================================
     09. GPU Tile Pyramid LOD & RoiPipeline Block Cache Simulator
     ======================================================================== */
  function initGpuTilePipelineCanvas() {
    const canvas = document.getElementById("gpu-tile-canvas");
    if (!canvas) return;
    const readout = document.getElementById("gpu-tile-readout");
    const zoomSlider = document.getElementById("gpu-zoom-slider");
    if (zoomSlider) zoomSlider.addEventListener("input", draw);

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      const scale = zoomSlider ? Number(zoomSlider.value) / 100 : 0.25;
      const lod = Math.max(0, Math.min(4, Math.floor(Math.log2(1 / scale))));
      const gridCount = Math.pow(2, 4 - lod);
      const boxSize = Math.min(width * 0.42, height - 60);
      const cell = boxSize / gridCount;

      ctx.strokeStyle = "#38bdf8";
      ctx.lineWidth = 1.5;
      for (let r = 0; r < gridCount; r++) {
        for (let c = 0; c < gridCount; c++) {
          ctx.strokeRect(36 + c * cell, 32 + r * cell, cell, cell);
        }
      }

      const rightX = width * 0.48;
      const boxW = Math.max(120, width - rightX - 28);
      ctx.fillStyle = "#34d399";
      ctx.font = "bold 13px Pretendard, sans-serif";
      ctx.fillText("RoiPipeline.GetOrUploadBlock (4,096개 단위 GPU 블록)", rightX, 42, boxW);

      for (let b = 0; b < 6; b++) {
        const by = 62 + b * 46;
        const cached = b < 4;
        ctx.fillStyle = cached ? "rgba(52, 211, 153, 0.2)" : "rgba(251, 191, 36, 0.2)";
        ctx.strokeStyle = cached ? "#34d399" : "#fbbf24";
        ctx.fillRect(rightX, by, boxW, 36);
        ctx.strokeRect(rightX, by, boxW, 36);
        ctx.fillStyle = "#e2e8f0";
        ctx.font = "12px Cascadia Code, monospace";
        ctx.fillText(
          cached
            ? `Block #${b} (4,096개) → TryGetCachedBlock HIT (VRAM 재사용)`
            : `Block #${b} (4,096개) → DeferredFallback (프레임당 2블록 제한)`,
          rightX + 10,
          by + 22,
          boxW - 20
        );
      }

      if (readout) {
        readout.textContent =
          `배율 Scale = ${scale.toFixed(2)}x → TilePlanner 선택 피라미드 레벨 LOD = ${lod} (타일 ${gridCount}×${gridCount}개만 디코딩/업로드)`;
      }
    }
    draw();
  }

  /* ========================================================================
     10. MVVM Key Routing & Filter Debounce Simulator
     ======================================================================== */
  function initMvvmKeyRouterCanvas() {
    const canvas = document.getElementById("mvvm-key-canvas");
    if (!canvas) return;
    const readout = document.getElementById("mvvm-key-readout");
    let activeBranch = 0;

    const branches = [
      { key: "Escape", path: "HandleKey → CancelActiveOperations", desc: "진행 중인 드래그 제스처 취소 + 패턴 검사 취소 + 도구 선택 해제" },
      { key: "ArrowRight (방향키)", path: "HandleKey → TryNudgeSelection(step=1px)", desc: "선택된 ROI가 있으면 1px 미세 이동 (Shift 누르면 10px 이동)" },
      { key: "Ctrl + Z / Delete", path: "HandleKey → FindShortcutCommand", desc: "UndoCommand / RedoCommand / DeleteSelectedCommand 즉시 실행" }
    ];

    document.querySelectorAll("[data-key-sim]").forEach(btn => {
      btn.addEventListener("click", () => {
        activeBranch = Number(btn.getAttribute("data-key-sim")) || 0;
        document.querySelectorAll("[data-key-sim]").forEach(b => b.classList.toggle("active", b === btn));
        draw();
      });
    });

    function draw() {
      const info = setupHiDPICanvas(canvas);
      if (!info) return;
      const { ctx, width, height } = info;
      ctx.clearRect(0, 0, width, height);

      branches.forEach((b, idx) => {
        const y = 45 + idx * 95;
        const active = idx === activeBranch;
        ctx.fillStyle = active ? "rgba(56, 189, 248, 0.2)" : "rgba(15, 23, 42, 0.85)";
        ctx.strokeStyle = active ? "#38bdf8" : "rgba(148, 163, 184, 0.25)";
        ctx.lineWidth = active ? 2.5 : 1.2;
        ctx.beginPath();
        ctx.roundRect(36, y, width - 72, 74, 10);
        ctx.fill();
        ctx.stroke();

        ctx.fillStyle = active ? "#38bdf8" : "#cbd5e1";
        ctx.font = "bold 14px Cascadia Code, monospace";
        ctx.fillText(`[입력 키: ${b.key}] → ${b.path}`, 54, y + 28, width - 108);
        ctx.fillStyle = "#94a3b8";
        ctx.font = "13px Pretendard, sans-serif";
        ctx.fillText(b.desc, 54, y + 52, width - 108);
      });

      if (readout) {
        const cur = branches[activeBranch];
        readout.textContent = `[MainWindowViewModel.HandleKey] 입력: ${cur.key} → 실행 헬퍼: ${cur.path}`;
      }
    }
    draw();
  }

  /* ========================================================================
     11. Responsive UML Class Diagram & Sequence Diagram Renderer
     Renders overflow-proof HTML/CSS UML Class Cards (with Stereotype, Title,
     Role, Fields/Methods, and Relationship Pills) and Chronological Sequence
     Timelines from declarative JSON specs in each chapter.
     ======================================================================== */
  function escapeXml(str) {
    return String(str || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function drawUmlConnectors(stage, boxMap, nodeTierMap, tierRows, edges) {
    const svg = stage.querySelector(".uml-connector-svg");
    if (!svg) return;
    const stageRect = stage.getBoundingClientRect();
    const stageW = Math.max(stage.clientWidth, stage.offsetWidth, 660);
    const stageH = Math.max(stage.clientHeight, stage.offsetHeight, 360);
    svg.setAttribute("viewBox", `0 0 ${stageW} ${stageH}`);

    const defs = `
      <defs>
        <marker id="uml-arrow-assoc" viewBox="0 0 12 12" refX="10" refY="6" markerWidth="8" markerHeight="8" orient="auto">
          <path d="M 1 2 L 10 6 L 1 10 z" fill="#38bdf8"/>
        </marker>
        <marker id="uml-arrow-realize" viewBox="0 0 14 14" refX="12" refY="7" markerWidth="10" markerHeight="10" orient="auto">
          <path d="M 1 2 L 12 7 L 1 12 z" fill="#090d16" stroke="#fbbf24" stroke-width="2"/>
        </marker>
        <marker id="uml-diamond-comp" viewBox="0 0 16 12" refX="2" refY="6" markerWidth="11" markerHeight="9" orient="auto">
          <path d="M 1 6 L 7 1 L 13 6 L 7 11 z" fill="#34d399" stroke="#34d399" stroke-width="1"/>
        </marker>
      </defs>`;

    const rectOf = (idx) => {
      const el = boxMap.get(idx);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      return {
        left: r.left - stageRect.left,
        right: r.right - stageRect.left,
        top: r.top - stageRect.top,
        bottom: r.bottom - stageRect.top,
        cx: (r.left + r.right) * 0.5 - stageRect.left,
        cy: (r.top + r.bottom) * 0.5 - stageRect.top,
        width: r.width,
        height: r.height
      };
    };

    const tierBounds = tierRows.map((rowEl) => {
      const r = rowEl.getBoundingClientRect();
      return {
        top: r.top - stageRect.top,
        bottom: r.bottom - stageRect.top
      };
    });

    const gutterUsageCount = new Map();
    const gutterTotalCount = new Map();
    const bottomPortUsage = new Map();
    const topPortUsage = new Map();
    const bottomPortTotal = new Map();
    const topPortTotal = new Map();

    edges.forEach((e) => {
      const tA = nodeTierMap.get(e.from);
      const tB = nodeTierMap.get(e.to);
      if (tA === undefined || tB === undefined || tA === tB) return;
      const gKey = Math.min(tA, tB);
      gutterTotalCount.set(gKey, (gutterTotalCount.get(gKey) || 0) + 1);
      if (tA < tB) {
        bottomPortTotal.set(e.from, (bottomPortTotal.get(e.from) || 0) + 1);
        topPortTotal.set(e.to, (topPortTotal.get(e.to) || 0) + 1);
      } else {
        topPortTotal.set(e.from, (topPortTotal.get(e.from) || 0) + 1);
        bottomPortTotal.set(e.to, (bottomPortTotal.get(e.to) || 0) + 1);
      }
    });

    const getPortX = (nodeIdx, rect, isBottom) => {
      const totalMap = isBottom ? bottomPortTotal : topPortTotal;
      const usageMap = isBottom ? bottomPortUsage : topPortUsage;
      const total = totalMap.get(nodeIdx) || 1;
      const used = usageMap.get(nodeIdx) || 0;
      usageMap.set(nodeIdx, used + 1);
      if (total === 1) return rect.cx;
      const span = Math.min(rect.width * 0.5, 90);
      const startX = rect.cx - span * 0.5;
      return startX + (span * used) / (total - 1);
    };

    const pathsHtml = edges.map((e) => {
      const a = rectOf(e.from);
      const b = rectOf(e.to);
      if (!a || !b) return "";

      const tA = nodeTierMap.get(e.from) ?? 0;
      const tB = nodeTierMap.get(e.to) ?? 0;

      const lbl = String(e.label || "");
      const isRealize = lbl.includes("implements") || lbl.includes("구현");
      const isCompose = lbl.includes("소유") || lbl.includes("보유") || lbl.includes("has-a");

      const strokeColor = isRealize ? "#fbbf24" : (isCompose ? "#34d399" : "#38bdf8");
      const dashAttr = isRealize ? `stroke-dasharray="6,4"` : "";
      const markerStart = isCompose ? `marker-start="url(#uml-diamond-comp)"` : "";
      const markerEnd = isRealize ? `marker-end="url(#uml-arrow-realize)"` : `marker-end="url(#uml-arrow-assoc)"`;

      let d = "";
      let labelX = 0;
      let labelY = 0;

      if (tA === tB) {
        const leftToRight = a.cx < b.cx;
        const x1 = leftToRight ? a.right : a.left;
        const x2 = leftToRight ? b.left : b.right;
        const y = (a.cy + b.cy) * 0.5;
        d = `M ${x1} ${y} L ${x2} ${y}`;
        labelX = (x1 + x2) * 0.5;
        labelY = Math.min(a.top, b.top) - 12;
      } else {
        const goingDown = tA < tB;
        const gKey = Math.min(tA, tB);
        const upperTier = tierBounds[gKey];
        const lowerTier = tierBounds[gKey + 1];

        const gutterTop = upperTier ? upperTier.bottom : (goingDown ? a.bottom : b.bottom);
        const gutterBot = lowerTier ? lowerTier.top : (goingDown ? b.top : a.top);

        const laneIdx = gutterUsageCount.get(gKey) || 0;
        const laneTotal = gutterTotalCount.get(gKey) || 1;
        gutterUsageCount.set(gKey, laneIdx + 1);

        const usableGutterH = Math.max(20, (gutterBot - gutterTop) - 28);
        const laneY = laneTotal === 1
          ? (gutterTop + gutterBot) * 0.5
          : gutterTop + 14 + (usableGutterH * laneIdx) / (laneTotal - 1);

        const x1 = getPortX(e.from, a, goingDown);
        const y1 = goingDown ? a.bottom : a.top;
        const x2 = getPortX(e.to, b, !goingDown);
        const y2 = goingDown ? b.top : b.bottom;

        d = `M ${x1} ${y1} L ${x1} ${laneY} L ${x2} ${laneY} L ${x2} ${y2}`;
        labelX = (x1 + x2) * 0.5;
        labelY = laneY;
      }

      const shortLbl = lbl.length > 22 ? lbl.slice(0, 21) + "…" : lbl;
      const pillW = Math.max(58, shortLbl.length * 7.2 + 16);

      return `
        <g class="uml-edge-group">
          <path d="${d}" fill="none" stroke="${strokeColor}" stroke-width="2.2" stroke-linejoin="round" ${dashAttr} ${markerStart} ${markerEnd} opacity="0.96"/>
          <rect x="${labelX - pillW * 0.5}" y="${labelY - 9}" width="${pillW}" height="18" rx="4" fill="#090d16" stroke="${strokeColor}" stroke-width="1.2"/>
          <text x="${labelX}" y="${labelY + 3.5}" text-anchor="middle" fill="#f8fafc" font-size="10.5" font-weight="700" font-family="Pretendard, sans-serif">${escapeXml(shortLbl)}</text>
        </g>`;
    }).join("");

    svg.innerHTML = defs + pathsHtml;
  }

  function renderAllDiagrams() {
    // 1. Authentic 3-Compartment Tiered UML Class Diagrams + Live SVG Connectors
    document.querySelectorAll("script.structure-spec").forEach(script => {
      try {
        const spec = JSON.parse(script.textContent);
        const nodes = spec.nodes || [];
        const edges = spec.edges || [];
        const tiers = spec.tiers || [nodes.map((_, i) => i)];

        const nodeTierMap = new Map();
        tiers.forEach((tierIndices, tIdx) => {
          tierIndices.forEach(nIdx => nodeTierMap.set(nIdx, tIdx));
        });

        function renderBoxHtml(idx) {
          const n = nodes[idx];
          if (!n) return "";
          const color = n.color || "#38bdf8";
          const allMembers = n.members || [];
          const fields = allMembers.filter(m => !m.includes("(") && !m.startsWith("↳"));
          const methods = allMembers.filter(m => m.includes("(") || m.startsWith("↳"));

          const fieldsHtml = fields.length > 0
            ? fields.map(f => `<li>${escapeXml(f)}</li>`).join("")
            : `<li class="empty-comp">(필드/상태 캡슐화)</li>`;

          const methodsHtml = methods.length > 0
            ? methods.map(m => `<li>${escapeXml(m)}</li>`).join("")
            : `<li class="empty-comp">(데이터/값 구조체)</li>`;

          const roleHtml = n.role
            ? `<span class="uml-comp-role">${escapeXml(n.role)}</span>`
            : "";

          return `
            <div class="uml-class-box" data-node-idx="${idx}" style="--node-color:${color}">
              <div class="uml-comp-header">
                <span class="uml-comp-stereo">«${escapeXml(n.stereotype || "class")}»</span>
                <span class="uml-comp-name">${escapeXml(n.title)}</span>
                ${roleHtml}
              </div>
              <ul class="uml-comp-fields">${fieldsHtml}</ul>
              <ul class="uml-comp-methods">${methodsHtml}</ul>
            </div>`;
        }

        const tiersHtml = tiers.map((tierIndices, tierIdx) => {
          const cols = Math.max(1, tierIndices.length);
          const cardsHtml = tierIndices.map(renderBoxHtml).join("");
          return `<div class="uml-tier-row" data-tier="${tierIdx}" style="--tier-cols:${cols}">${cardsHtml}</div>`;
        }).join("");

        const relPillsHtml = edges.map(e => {
          const fromTitle = nodes[e.from] ? nodes[e.from].title : `#${e.from}`;
          const toTitle = nodes[e.to] ? nodes[e.to].title : `#${e.to}`;
          const lbl = String(e.label || "참조");
          const sym = (lbl.includes("implements") || lbl.includes("구현"))
            ? "╌╌▷"
            : ((lbl.includes("소유") || lbl.includes("보유") || lbl.includes("has-a")) ? "◆──▶" : "───▶");
          return `
            <span class="uml-rel-pill">
              <span>${escapeXml(fromTitle)}</span>
              <span class="rel-sym">${sym}</span>
              <span>${escapeXml(toTitle)}</span>
              <span class="rel-lbl">(${escapeXml(lbl)})</span>
            </span>`;
        }).join("");

        const stage = document.createElement("div");
        stage.className = "uml-canvas-stage";
        stage.innerHTML = `
          <svg class="uml-connector-svg"></svg>
          <div class="uml-tiers-container">${tiersHtml}</div>
          <div class="uml-legend-bar">
            <div class="uml-legend-symbols">
              <span><strong>◆──▶</strong> 강한 소유/포함 (Composition)</span>
              <span><strong>╌╌▷</strong> 인터페이스 구현 (Realization)</span>
              <span><strong>───▶</strong> 호출/참조/의존 (Association)</span>
            </div>
            <div class="uml-rel-pills">${relPillsHtml}</div>
          </div>`;

        script.parentElement.insertBefore(stage, script);

        const boxMap = new Map();
        stage.querySelectorAll(".uml-class-box").forEach(el => {
          const idx = Number(el.getAttribute("data-node-idx"));
          boxMap.set(idx, el);
        });
        const tierRows = Array.from(stage.querySelectorAll(".uml-tier-row"));

        const refreshLines = () => drawUmlConnectors(stage, boxMap, nodeTierMap, tierRows, edges);
        requestAnimationFrame(() => requestAnimationFrame(refreshLines));
        window.addEventListener("resize", refreshLines);
      } catch (err) {
        console.error("Structure diagram error:", err);
      }
    });

    // 2. Authentic Lifeline UML Sequence Diagrams
    document.querySelectorAll("script.sequence-spec").forEach(script => {
      try {
        const spec = JSON.parse(script.textContent);
        const participants = spec.participants || [];
        const messages = spec.messages || [];
        const count = Math.max(1, participants.length);

        const partHeaderHtml = participants.map(p => `
          <div class="seq-part-box">${escapeXml(p)}</div>`).join("");

        const lifelinesHtml = participants.map((_, i) => {
          const leftPct = ((i + 0.5) / count) * 100;
          return `<div class="seq-lifeline-col" style="left:${leftPct}%"></div>`;
        }).join("");

        const rowsHtml = messages.map((m, idx) => {
          const x1 = ((m.from + 0.5) / count) * 1000;
          const x2 = ((m.to + 0.5) / count) * 1000;
          const isReturn = Boolean(m.ret);
          const isSelf = m.from === m.to;
          const stroke = isReturn ? "#34d399" : "#38bdf8";
          const dash = isReturn ? `stroke-dasharray="6,4"` : "";

          let arrowSvg = "";
          if (isSelf) {
            arrowSvg = `
              <rect x="${x1 - 5}" y="6" width="10" height="32" rx="2" fill="#fbbf24"/>
              <path d="M ${x1 + 5} 14 L ${x1 + 42} 14 L ${x1 + 42} 32 L ${x1 + 9} 32" fill="none" stroke="#fbbf24" stroke-width="2.2" marker-end="url(#seq-arr-self-${idx})"/>
              <rect x="${Math.min(720, Math.max(10, x1 - 120))}" y="0" width="260" height="19" rx="4" fill="#1e293b" stroke="#fbbf24" stroke-width="1"/>
              <text x="${Math.min(850, Math.max(140, x1 + 10))}" y="13" text-anchor="middle" fill="#f8fafc" font-size="11.5" font-family="Cascadia Code, Pretendard, monospace">${idx + 1}. ${escapeXml(m.text)}</text>`;
          } else {
            const dir = x2 > x1 ? 1 : -1;
            const startX = x1 + dir * 6;
            const endX = x2 - dir * 8;
            const midX = (x1 + x2) * 0.5;
            const boxW = Math.min(480, Math.max(180, String(m.text).length * 7.8 + 28));
            const clampedMidX = Math.min(1000 - boxW * 0.5 - 6, Math.max(boxW * 0.5 + 6, midX));
            arrowSvg = `
              <rect x="${x1 - 5}" y="8" width="10" height="28" rx="2" fill="#38bdf8" opacity="0.85"/>
              <rect x="${x2 - 5}" y="8" width="10" height="28" rx="2" fill="${stroke}"/>
              <line x1="${startX}" y1="28" x2="${endX}" y2="28" stroke="${stroke}" stroke-width="2.2" ${dash} marker-end="url(#seq-arr-${idx})"/>
              <rect x="${clampedMidX - boxW * 0.5}" y="2" width="${boxW}" height="19" rx="4" fill="#131c31" stroke="${stroke}" stroke-width="1"/>
              <text x="${clampedMidX}" y="15" text-anchor="middle" fill="#f8fafc" font-size="11.5" font-family="Cascadia Code, Pretendard, monospace">${idx + 1}. ${escapeXml(m.text)}</text>`;
          }

          return `
            <div class="seq-msg-row">
              <svg class="seq-msg-arrow-svg" viewBox="0 0 1000 42" preserveAspectRatio="none">
                <defs>
                  <marker id="seq-arr-${idx}" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                    <path d="M 0 1 L 9 5 L 0 9 z" fill="${stroke}"/>
                  </marker>
                  <marker id="seq-arr-self-${idx}" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto">
                    <path d="M 0 1 L 9 5 L 0 9 z" fill="#fbbf24"/>
                  </marker>
                </defs>
                ${arrowSvg}
              </svg>
            </div>`;
        }).join("");

        const stage = document.createElement("div");
        stage.className = "seq-uml-stage";
        stage.innerHTML = `
          <div class="seq-uml-inner">
            <div class="seq-participants-grid" style="grid-template-columns: repeat(${count}, minmax(0, 1fr))">
              ${partHeaderHtml}
            </div>
            <div class="seq-lifelines-Area">
              ${lifelinesHtml}
              ${rowsHtml}
            </div>
          </div>`;
        script.parentElement.insertBefore(stage, script);
      } catch (err) {
        console.error("Sequence diagram error:", err);
      }
    });
  }

  function initMathTypesettingGuard() {
    const selectors = [
      ".math-explain",
      ".math-example-box",
      ".math-legend-table td",
      ".prereq-card p",
      ".feynman-step p",
      ".section-card > p",
      ".section-card li"
    ].join(", ");

    const containers = document.querySelectorAll(selectors);
    const opRegex = / (\+|-|−|·|×|÷|=|≤|≥|<|>|≈|≠|∈|∉|⊇|↔|→|⟺|⟹|∧) /g;
    const trigRegex = /\b(cos|sin|tan|log|min|max) /g;

    containers.forEach((el) => {
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
        acceptNode(node) {
          const parent = node.parentElement;
          if (!parent) return NodeFilter.FILTER_REJECT;
          const tag = parent.tagName;
          if (tag === "CODE" || tag === "PRE" || tag === "SCRIPT" || tag === "STYLE") {
            return NodeFilter.FILTER_REJECT;
          }
          return NodeFilter.FILTER_ACCEPT;
        }
      });

      const textNodes = [];
      let current;
      while ((current = walker.nextNode())) {
        textNodes.push(current);
      }

      textNodes.forEach((tNode) => {
        let val = tNode.nodeValue;
        if (!val) return;
        val = val.replace(opRegex, "\u00A0$1\u00A0");
        val = val.replace(trigRegex, "$1\u00A0");
        val = val.replace(/, (?=[0-9−\-+a-zA-Zθℋ])/g, ",\u00A0");
        tNode.nodeValue = val;
      });
    });
  }

  window.addEventListener("DOMContentLoaded", () => {
    renderNavigation();
    initMathTypesettingGuard();
    renderAllDiagrams();
    initArchFlowCanvas();
    initPrereqVectorCanvas();
    initViewportZoomCanvas();
    initShapePolygonCanvas();
    initMotionBoundsCanvas();
    initSpatialBvhCanvas();
    initHitTestGestureCanvas();
    initImmutableHistoryCanvas();
    initHwndRenderLoopCanvas();
    initGpuTilePipelineCanvas();
    initMvvmKeyRouterCanvas();
  });
})();
