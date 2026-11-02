/**
 * ManualGemini — VisionCanvas 고1 눈높이 완전 정복 인터랙티브 엔진 (v2.0)
 * 1. Light/Dark 교재 테마 토글 (기본: 눈이 편안한 Light 북 테마)
 * 2. 박스 위를 절대 지나가지 않는 직교 채널(Orthogonal Gutter Lane) UML 클래스 다이어그램 렌더러
 * 3. UML 시퀀스 다이어그램(Lifeline) 렌더러
 * 4. FHD & iPad HiDPI 대응 5대 핵심 인터랙티브 애니메이션 엔진
 */

document.addEventListener("DOMContentLoaded", () => {
  initThemeToggle();
  initMathTypesettingGuard();
  renderAllUmlDiagrams();
  initArchitecturePipelineAnim();
  initMathZoomRotationAnim();
  initHwndSimdAnim();
  initTilePyramidAnim();
  initBvhCollisionAnim();
});

function initMathTypesettingGuard() {
  const selectors = [
    ".math-box p",
    ".story-lead",
    ".concept-card p",
    ".callout p",
    ".content-section > p",
    ".content-section li"
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
      val = val.replace(/, (?=[0-9−\-+a-zA-Zθ])/g, ",\u00A0");
      tNode.nodeValue = val;
    });
  });
}

/* ============================================================================
   0. Light / Dark Book Theme Toggle
   ============================================================================ */
function initThemeToggle() {
  const saved = localStorage.getItem("manual-gemini-theme") || "light";
  document.documentElement.setAttribute("data-theme", saved);

  const btn = document.getElementById("theme-toggle-btn");
  if (!btn) return;

  const updateLabel = (theme) => {
    btn.textContent = theme === "dark" ? "☀️ 라이트 교재 모드" : "🌙 다크 모드";
  };
  updateLabel(saved);

  btn.addEventListener("click", () => {
    const cur = document.documentElement.getAttribute("data-theme") || "light";
    const next = cur === "dark" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", next);
    localStorage.setItem("manual-gemini-theme", next);
    updateLabel(next);
  });
}

/* ============================================================================
   1. Obstacle-Free Orthogonal Gutter UML Class Diagram & Sequence Renderer
   ============================================================================ */
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
  const stageW = Math.max(stage.clientWidth, stage.offsetWidth, 680);
  const stageH = Math.max(stage.clientHeight, stage.offsetHeight, 400);
  svg.setAttribute("viewBox", `0 0 ${stageW} ${stageH}`);

  const defs = `
    <defs>
      <marker id="uml-arrow-assoc" viewBox="0 0 12 12" refX="10" refY="6" markerWidth="8" markerHeight="8" orient="auto">
        <path d="M 1 2 L 10 6 L 1 10 z" fill="#38bdf8"/>
      </marker>
      <marker id="uml-arrow-realize" viewBox="0 0 14 14" refX="12" refY="7" markerWidth="10" markerHeight="10" orient="auto">
        <path d="M 1 2 L 12 7 L 1 12 z" fill="#0b1120" stroke="#fbbf24" stroke-width="2"/>
      </marker>
      <marker id="uml-diamond-comp" viewBox="0 0 16 12" refX="2" refY="6" markerWidth="11" markerHeight="9" orient="auto">
        <path d="M 1 6 L 7 1 L 13 6 L 7 11 z" fill="#34d399" stroke="#34d399" stroke-width="1"/>
      </marker>
    </defs>`;

  // Compute bounding rect of every box relative to stage
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

  // Compute vertical bounds of each tier row so we know the exact empty gutter channel [gutterTop, gutterBottom]
  const tierBounds = tierRows.map((rowEl) => {
    const r = rowEl.getBoundingClientRect();
    return {
      top: r.top - stageRect.top,
      bottom: r.bottom - stageRect.top
    };
  });

  // Track how many edges use each gutter between tier T and T+1 so each gets its own non-overlapping horizontal lane
  const gutterUsageCount = new Map();
  const gutterTotalCount = new Map();

  edges.forEach((e) => {
    const tA = nodeTierMap.get(e.from);
    const tB = nodeTierMap.get(e.to);
    if (tA !== undefined && tB !== undefined && tA !== tB) {
      const gKey = Math.min(tA, tB);
      gutterTotalCount.set(gKey, (gutterTotalCount.get(gKey) || 0) + 1);
    }
  });

  // Track port offsets on top/bottom edges of each node so multiple lines leaving the same box don't overlap
  const bottomPortUsage = new Map();
  const topPortUsage = new Map();
  const bottomPortTotal = new Map();
  const topPortTotal = new Map();

  edges.forEach((e) => {
    const tA = nodeTierMap.get(e.from);
    const tB = nodeTierMap.get(e.to);
    if (tA === undefined || tB === undefined || tA === tB) return;
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
      // Same-tier peer connection across the horizontal gap between adjacent columns
      const leftToRight = a.cx < b.cx;
      const x1 = leftToRight ? a.right : a.left;
      const x2 = leftToRight ? b.left : b.right;
      const y = (a.cy + b.cy) * 0.5;
      d = `M ${x1} ${y} L ${x2} ${y}`;
      labelX = (x1 + x2) * 0.5;
      labelY = a.top - 13;
      // Draw a subtle leader from label pill down to the peer line if needed
    } else {
      // Adjacent-tier connection strictly routed inside the 76px empty gutter channel!
      const goingDown = tA < tB;
      const gKey = Math.min(tA, tB);
      const upperTier = tierBounds[gKey];
      const lowerTier = tierBounds[gKey + 1];

      const gutterTop = upperTier ? upperTier.bottom : (goingDown ? a.bottom : b.bottom);
      const gutterBot = lowerTier ? lowerTier.top : (goingDown ? b.top : a.top);

      const laneIdx = gutterUsageCount.get(gKey) || 0;
      const laneTotal = gutterTotalCount.get(gKey) || 1;
      gutterUsageCount.set(gKey, laneIdx + 1);

      // Spread multiple horizontal segments cleanly inside the middle 65% of the empty gutter
      const usableGutterH = Math.max(24, (gutterBot - gutterTop) - 22);
      const laneY = laneTotal === 1
        ? (gutterTop + gutterBot) * 0.5
        : gutterTop + 11 + (usableGutterH * laneIdx) / (laneTotal - 1);

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
        <path d="${d}" fill="none" stroke="${strokeColor}" stroke-width="2.2" stroke-linejoin="round" ${dashAttr} ${markerStart} ${markerEnd}/>
        <rect x="${labelX - pillW * 0.5}" y="${labelY - 9.5}" width="${pillW}" height="19" rx="5" fill="#090d16" stroke="${strokeColor}" stroke-width="1.2"/>
        <text x="${labelX}" y="${labelY + 3.5}" text-anchor="middle" fill="#f8fafc" font-size="10.5" font-weight="700" font-family="Pretendard, sans-serif">${escapeXml(shortLbl)}</text>
      </g>`;
  }).join("");

  svg.innerHTML = defs + pathsHtml;
}

function renderAllUmlDiagrams() {
  // 1. 3-Compartment UML Class Diagrams
  document.querySelectorAll("script.structure-spec").forEach((script) => {
    try {
      const spec = JSON.parse(script.textContent);
      const nodes = spec.nodes || [];
      const edges = spec.edges || [];
      const tiers = spec.tiers || [nodes.map((_, i) => i)];

      const nodeTierMap = new Map();
      tiers.forEach((tierIndices, tIdx) => {
        tierIndices.forEach((nIdx) => nodeTierMap.set(nIdx, tIdx));
      });

      const renderBoxHtml = (idx) => {
        const n = nodes[idx];
        if (!n) return "";
        const color = n.color || "#38bdf8";
        const allMembers = n.members || [];
        const fields = allMembers.filter((m) => !m.includes("("));
        const methods = allMembers.filter((m) => m.includes("("));

        const fieldsHtml = fields.length > 0
          ? fields.map((f) => `<li>${escapeXml(f)}</li>`).join("")
          : `<li class="empty-comp">(불변 값 / 상태 캡슐화)</li>`;

        const methodsHtml = methods.length > 0
          ? methods.map((m) => `<li>${escapeXml(m)}</li>`).join("")
          : `<li class="empty-comp">(순수 데이터 구조체)</li>`;

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
      };

      const tiersHtml = tiers.map((tierIndices, tierIdx) => {
        const cols = Math.max(1, tierIndices.length);
        const cardsHtml = tierIndices.map(renderBoxHtml).join("");
        return `<div class="uml-tier-row" data-tier="${tierIdx}" style="--tier-cols:${cols}">${cardsHtml}</div>`;
      }).join("");

      const relPillsHtml = edges.map((e) => {
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
            <span><strong>◆──▶</strong> 강한 소유 (Composition)</span>
            <span><strong>╌╌▷</strong> 인터페이스 구현 (Realization)</span>
            <span><strong>───▶</strong> 호출·참조 (Association)</span>
          </div>
          <div class="uml-rel-pills">${relPillsHtml}</div>
        </div>`;

      script.parentElement.insertBefore(stage, script);

      const boxMap = new Map();
      stage.querySelectorAll(".uml-class-box").forEach((el) => {
        boxMap.set(Number(el.getAttribute("data-node-idx")), el);
      });
      const tierRows = Array.from(stage.querySelectorAll(".uml-tier-row"));

      const refreshLines = () => drawUmlConnectors(stage, boxMap, nodeTierMap, tierRows, edges);
      requestAnimationFrame(() => requestAnimationFrame(refreshLines));
      window.addEventListener("resize", refreshLines);
      if (typeof ResizeObserver !== "undefined") {
        const ro = new ResizeObserver(refreshLines);
        ro.observe(stage);
      }
    } catch (err) {
      console.error("UML Class Diagram render error:", err);
    }
  });

  // 2. Lifeline UML Sequence Diagrams
  document.querySelectorAll("script.sequence-spec").forEach((script) => {
    try {
      const spec = JSON.parse(script.textContent);
      const participants = spec.participants || [];
      const messages = spec.messages || [];
      const count = Math.max(1, participants.length);

      const partHeaderHtml = participants.map((p) => `
        <div class="seq-part-box">${escapeXml(p)}</div>`).join("");

      const lifelinesHtml = participants.map((_, i) => {
        const leftPct = ((i + 0.5) / count) * 100;
        return `<div class="seq-lifeline-col" style="left:${leftPct}%"></div>`;
      }).join("");

      const rowsHtml = messages.map((m, idx) => {
        const x1 = ((m.from + 0.5) / count) * 1000;
        const x2 = ((m.to + 0.5) / count) * 1000;
        const isReturn = Boolean(m.ret);
        const stroke = isReturn ? "#34d399" : "#38bdf8";
        const dash = isReturn ? `stroke-dasharray="6,4"` : "";

        const dir = x2 >= x1 ? 1 : -1;
        const startX = x1 + dir * 6;
        const endX = x2 - dir * 8;
        const midX = (x1 + x2) * 0.5;
        const boxW = Math.min(480, Math.max(190, String(m.text).length * 7.8 + 28));
        const clampedMidX = Math.min(1000 - boxW * 0.5 - 8, Math.max(boxW * 0.5 + 8, midX));

        return `
          <div class="seq-msg-row">
            <svg class="seq-msg-arrow-svg" viewBox="0 0 1000 42" preserveAspectRatio="none">
              <defs>
                <marker id="seq-arr-${idx}" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                  <path d="M 0 1 L 9 5 L 0 9 z" fill="${stroke}"/>
                </marker>
              </defs>
              <rect x="${x1 - 5}" y="8" width="10" height="28" rx="2" fill="#38bdf8" opacity="0.85"/>
              <rect x="${x2 - 5}" y="8" width="10" height="28" rx="2" fill="${stroke}"/>
              <line x1="${startX}" y1="28" x2="${endX}" y2="28" stroke="${stroke}" stroke-width="2.2" ${dash} marker-end="url(#seq-arr-${idx})"/>
              <rect x="${clampedMidX - boxW * 0.5}" y="2" width="${boxW}" height="19" rx="4" fill="#131c31" stroke="${stroke}" stroke-width="1"/>
              <text x="${clampedMidX}" y="15" text-anchor="middle" fill="#f8fafc" font-size="11.5" font-family="Cascadia Code, Pretendard, monospace">${idx + 1}. ${escapeXml(m.text)}</text>
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
      console.error("Sequence Diagram render error:", err);
    }
  });
}

/* ============================================================================
   2. Overview: 5-Stage End-to-End Pipeline Animation (index.html)
   ============================================================================ */
function initArchitecturePipelineAnim() {
  const canvas = document.getElementById("arch-anim-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const readout = document.getElementById("arch-anim-readout");
  const btnPlay = document.getElementById("arch-btn-play");
  const btnStep = document.getElementById("arch-btn-step");

  const stages = [
    {
      name: "1. Win32 HWND",
      sub: "ViewportWindow.WndProc",
      color: "#38bdf8",
      desc: "[1단계: Win32 입력] 마우스 좌표 패킹 해제 → (short)(lParam & 0xFFFF). WPF 버블링 없이 0.1ms 만에 포인터 좌표를 획득합니다."
    },
    {
      name: "2. Interaction",
      sub: "InteractionController",
      color: "#818cf8",
      desc: "[2단계: 좌표 역변환 & 판정] ViewportTransform.ScreenToImage(x, y)로 이미지 좌표를 구하고, BVH 트리로 클릭된 ROI를 찾습니다."
    },
    {
      name: "3. Core Session",
      sub: "EditorSession & Document",
      color: "#34d399",
      desc: "[3단계: 불변 상태 갱신] 드래그 중엔 RoiTransformPreview만 바꾸고, 마우스를 놓을 때 새 DocumentState 스냅샷을 Undo 스택에 기록합니다."
    },
    {
      name: "4. Tile & Scene",
      sub: "TilePlanner & ScenePainter",
      color: "#fbbf24",
      desc: "[4단계: 타일 & 장면 조립] 현재 배율에 맞는 피라미드 레벨(L = floor(log2(1/S)))의 512×512 타일과 ROI 인스턴스 버퍼를 준비합니다."
    },
    {
      name: "5. GPU Backend",
      sub: "DX12 / Vulkan / wgpu / Skia",
      color: "#c084fc",
      desc: "[5단계: GPU 출력] 준비된 타일 텍스처와 1억 개 ROI 압축 배치 인스턴스를 전용 스왑체인에 그려 60FPS로 화면에 표시합니다."
    }
  ];

  let activeStage = 0;
  let playing = true;
  let progress = 0;

  if (btnPlay) {
    btnPlay.addEventListener("click", () => {
      playing = !playing;
      btnPlay.textContent = playing ? "⏸ 일시정지" : "▶ 자동 재생";
    });
  }
  if (btnStep) {
    btnStep.addEventListener("click", () => {
      playing = false;
      if (btnPlay) btnPlay.textContent = "▶ 자동 재생";
      activeStage = (activeStage + 1) % stages.length;
      progress = 0;
      updateReadout();
    });
  }

  function updateReadout() {
    if (readout) readout.textContent = stages[activeStage].desc;
  }
  updateReadout();

  function render() {
    const W = canvas.width;
    const H = canvas.height;
    ctx.clearRect(0, 0, W, H);

    ctx.strokeStyle = "rgba(148,163,184,0.07)";
    ctx.lineWidth = 1;
    for (let x = 0; x < W; x += 40) {
      ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, H); ctx.stroke();
    }
    for (let y = 0; y < H; y += 40) {
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke();
    }

    const boxW = 148;
    const boxH = 86;
    const gap = 22;
    const startX = (W - (stages.length * boxW + (stages.length - 1) * gap)) / 2;
    const centerY = H / 2;

    if (playing) {
      progress += 0.014;
      if (progress >= 1) {
        progress = 0;
        activeStage = (activeStage + 1) % stages.length;
        updateReadout();
      }
    }

    stages.forEach((st, idx) => {
      const x = startX + idx * (boxW + gap);
      const y = centerY - boxH / 2;
      const isCurrent = idx === activeStage;

      if (idx < stages.length - 1) {
        const ax1 = x + boxW;
        const ax2 = x + boxW + gap;
        ctx.strokeStyle = idx < activeStage ? st.color : "#334155";
        ctx.lineWidth = 3;
        ctx.beginPath();
        ctx.moveTo(ax1, centerY);
        ctx.lineTo(ax2, centerY);
        ctx.stroke();

        if (isCurrent) {
          const px = ax1 + (ax2 - ax1) * progress;
          ctx.fillStyle = st.color;
          ctx.beginPath();
          ctx.arc(px, centerY, 5, 0, Math.PI * 2);
          ctx.fill();
        }
      }

      ctx.save();
      if (isCurrent) {
        ctx.shadowColor = st.color;
        ctx.shadowBlur = 16;
      }
      ctx.fillStyle = isCurrent ? "rgba(30,41,59,0.96)" : "rgba(15,23,42,0.88)";
      ctx.strokeStyle = isCurrent ? st.color : "#334155";
      ctx.lineWidth = isCurrent ? 2.5 : 1.5;

      ctx.beginPath();
      ctx.roundRect(x, y, boxW, boxH, 10);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = isCurrent ? st.color : "#e2e8f0";
      ctx.font = "bold 13px Pretendard, sans-serif";
      ctx.textAlign = "center";
      ctx.fillText(st.name, x + boxW / 2, y + 34, boxW - 14);

      ctx.fillStyle = "#94a3b8";
      ctx.font = "11px JetBrains Mono, monospace";
      ctx.fillText(st.sub, x + boxW / 2, y + 58, boxW - 14);
      ctx.restore();
    });

    requestAnimationFrame(render);
  }
  requestAnimationFrame(render);
}

/* ============================================================================
   3. Chapter 01: ZoomAt & 2D Rotation Matrix Interactive Simulator
   ============================================================================ */
function initMathZoomRotationAnim() {
  const canvas = document.getElementById("math-zoom-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const sliderScale = document.getElementById("math-scale-slider");
  const sliderAngle = document.getElementById("math-angle-slider");
  const readout = document.getElementById("math-zoom-readout");
  const btnReset = document.getElementById("math-btn-reset");

  let scale = 1.5;
  let angleDeg = 25;
  let cursorScreen = { x: 430, y: 155 };

  if (sliderScale) {
    sliderScale.addEventListener("input", (e) => {
      scale = parseFloat(e.target.value);
      draw();
    });
  }
  if (sliderAngle) {
    sliderAngle.addEventListener("input", (e) => {
      angleDeg = parseFloat(e.target.value);
      draw();
    });
  }
  if (btnReset) {
    btnReset.addEventListener("click", () => {
      scale = 1.5;
      angleDeg = 25;
      if (sliderScale) sliderScale.value = "1.5";
      if (sliderAngle) sliderAngle.value = "25";
      draw();
    });
  }

  canvas.addEventListener("mousemove", (e) => {
    const r = canvas.getBoundingClientRect();
    const sx = (e.clientX - r.left) * (canvas.width / r.width);
    const sy = (e.clientY - r.top) * (canvas.height / r.height);
    if (sx > 280) {
      cursorScreen = { x: sx, y: sy };
      draw();
    }
  });

  function draw() {
    const W = canvas.width;
    const H = canvas.height;
    ctx.clearRect(0, 0, W, H);

    const anchorImg = { x: 120, y: 90 };
    const offsetX = cursorScreen.x - anchorImg.x * scale;
    const offsetY = cursorScreen.y - anchorImg.y * scale;

    ctx.strokeStyle = "rgba(56,189,248,0.12)";
    ctx.lineWidth = 1;
    for (let ix = 0; ix <= 300; ix += 30) {
      const sx = ix * scale + offsetX;
      ctx.beginPath(); ctx.moveTo(sx, 0); ctx.lineTo(sx, H); ctx.stroke();
    }
    for (let iy = 0; iy <= 220; iy += 30) {
      const sy = iy * scale + offsetY;
      ctx.beginPath(); ctx.moveTo(0, sy); ctx.lineTo(W, sy); ctx.stroke();
    }

    const imgRectW = 240 * scale;
    const imgRectH = 160 * scale;
    ctx.fillStyle = "rgba(15,23,42,0.75)";
    ctx.strokeStyle = "#38bdf8";
    ctx.lineWidth = 2;
    ctx.fillRect(offsetX, offsetY, imgRectW, imgRectH);
    ctx.strokeRect(offsetX, offsetY, imgRectW, imgRectH);

    const roiCenterScreenX = anchorImg.x * scale + offsetX;
    const roiCenterScreenY = anchorImg.y * scale + offsetY;
    const rad = (angleDeg * Math.PI) / 180;

    ctx.save();
    ctx.translate(roiCenterScreenX, roiCenterScreenY);
    ctx.rotate(rad);
    const rw = 90 * scale;
    const rh = 54 * scale;
    ctx.fillStyle = "rgba(52,211,153,0.22)";
    ctx.strokeStyle = "#34d399";
    ctx.lineWidth = 2.5;
    ctx.fillRect(-rw / 2, -rh / 2, rw, rh);
    ctx.strokeRect(-rw / 2, -rh / 2, rw, rh);

    ctx.beginPath();
    ctx.moveTo(0, -rh / 2);
    ctx.lineTo(0, -rh / 2 - 18);
    ctx.strokeStyle = "#fbbf24";
    ctx.stroke();
    ctx.fillStyle = "#fbbf24";
    ctx.beginPath();
    ctx.arc(0, -rh / 2 - 18, 5, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    ctx.fillStyle = "#fb7185";
    ctx.beginPath();
    ctx.arc(cursorScreen.x, cursorScreen.y, 6, 0, Math.PI * 2);
    ctx.fill();

    ctx.fillStyle = "#f8fafc";
    ctx.font = "bold 12px JetBrains Mono, monospace";
    ctx.fillText(`고정점 P_img=(120, 90) / S=${scale.toFixed(2)}x / θ=${angleDeg.toFixed(0)}°`, 18, 26);

    if (readout) {
      const cosV = Math.cos(rad).toFixed(3);
      const sinV = Math.sin(rad).toFixed(3);
      readout.textContent =
        `[ZoomAt & RigidTransform2D] Scale=${scale.toFixed(2)}x | Offset=(${offsetX.toFixed(1)}, ${offsetY.toFixed(1)}) | 회전 행렬 R(${angleDeg}°): cos=${cosV}, sin=${sinV}`;
    }
  }
  draw();
}

/* ============================================================================
   4. Chapter 02: Win32 lParam Bit Unpacking & SIMD Vector256 Simulator
   ============================================================================ */
function initHwndSimdAnim() {
  const canvas = document.getElementById("hwnd-simd-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const readout = document.getElementById("hwnd-simd-readout");
  const btnStep = document.getElementById("simd-btn-step");
  const btnMode = document.getElementById("simd-btn-mode");

  let simdMode = true;
  let stepIndex = 0;
  const dataA = [120, 140, 200, 90, 60, 110, 180, 210];
  const dataB = [100, 160, 180, 110, 80, 90, 220, 190];

  if (btnMode) {
    btnMode.addEventListener("click", () => {
      simdMode = !simdMode;
      stepIndex = 0;
      btnMode.textContent = simdMode ? "모드: SIMD Vector256 (8개 동시)" : "모드: 일반 for문 (1개씩 8번)";
      draw();
    });
  }
  if (btnStep) {
    btnStep.addEventListener("click", () => {
      if (simdMode) {
        stepIndex = stepIndex === 0 ? 8 : 0;
      } else {
        stepIndex = (stepIndex + 1) % 9;
      }
      draw();
    });
  }

  function draw() {
    const W = canvas.width;
    const H = canvas.height;
    ctx.clearRect(0, 0, W, H);

    ctx.fillStyle = "#38bdf8";
    ctx.font = "bold 13px Pretendard, sans-serif";
    ctx.fillText(
      simdMode
        ? "🚀 SIMD Vector256<int>: CPU 클럭 1번에 8개 픽셀 평균 ((a + b + 1) >> 1) 동시 완료!"
        : "🐢 스칼라 for 루프: CPU가 1번에 1개씩 총 8번 반복해야 완료됨",
      24, 28, W - 48
    );

    const cellW = 82;
    const cellH = 40;
    const startX = 48;

    const drawRow = (label, arr, y, highlightCount, color) => {
      ctx.fillStyle = "#94a3b8";
      ctx.font = "bold 12px JetBrains Mono, monospace";
      ctx.fillText(label, 18, y + 25);

      for (let i = 0; i < 8; i++) {
        const x = startX + 86 + i * (cellW + 8);
        const active = i < highlightCount;
        ctx.fillStyle = active ? "rgba(52,211,153,0.22)" : "rgba(15,23,42,0.85)";
        ctx.strokeStyle = active ? color : "#334155";
        ctx.lineWidth = active ? 2.2 : 1.2;
        ctx.beginPath();
        ctx.roundRect(x, y, cellW, cellH, 6);
        ctx.fill();
        ctx.stroke();

        ctx.fillStyle = active ? "#f8fafc" : "#94a3b8";
        ctx.font = "bold 13px JetBrains Mono, monospace";
        ctx.textAlign = "center";
        ctx.fillText(arr[i] !== null ? String(arr[i]) : "?", x + cellW / 2, y + 25);
        ctx.textAlign = "left";
      }
    };

    const outArr = dataA.map((a, i) => (i < stepIndex ? (a + dataB[i] + 1) >> 1 : null));
    drawRow("Row A", dataA, 48, stepIndex, "#38bdf8");
    drawRow("Row B", dataB, 108, stepIndex, "#818cf8");
    drawRow("Out  ", outArr, 176, stepIndex, "#34d399");

    if (readout) {
      readout.textContent = simdMode
        ? `[Vector256 SIMD 모드] 완료된 레인: ${stepIndex}/8 | 필요 명령 횟수: ${stepIndex > 0 ? 1 : 0}회 (8배 가속)`
        : `[일반 스칼라 for문] 완료된 픽셀: ${stepIndex}/8 | 필요 명령 횟수: ${stepIndex}회`;
    }
  }
  draw();
}

/* ============================================================================
   5. Chapter 04: Image Tiling & Pyramid Level Streaming Simulator
   ============================================================================ */
function initTilePyramidAnim() {
  const canvas = document.getElementById("tile-pyramid-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const sliderScale = document.getElementById("tile-scale-slider");
  const readout = document.getElementById("tile-pyramid-readout");

  let scale = 0.35;
  let viewCenter = { x: 0.5, y: 0.5 };

  if (sliderScale) {
    sliderScale.addEventListener("input", (e) => {
      scale = parseFloat(e.target.value);
      draw();
    });
  }

  canvas.addEventListener("mousemove", (e) => {
    const r = canvas.getBoundingClientRect();
    const mx = (e.clientX - r.left) * (canvas.width / r.width);
    const my = (e.clientY - r.top) * (canvas.height / r.height);
    if (mx > 310 && mx < 790 && my > 30 && my < 270) {
      viewCenter.x = Math.max(0.18, Math.min(0.82, (mx - 320) / 460));
      viewCenter.y = Math.max(0.18, Math.min(0.82, (my - 30) / 240));
      draw();
    }
  });

  function draw() {
    const W = canvas.width;
    const H = canvas.height;
    ctx.clearRect(0, 0, W, H);

    const invScale = 1.0 / scale;
    const rawLevel = Math.floor(Math.log2(Math.max(1, invScale)));
    const selectedLevel = Math.max(0, Math.min(3, rawLevel));

    ctx.fillStyle = "#94a3b8";
    ctx.font = "bold 12px Pretendard, sans-serif";
    ctx.fillText("피라미드 레벨 (TrianglePyramid)", 22, 26);

    const levels = [
      { lv: 0, dim: "17280×30720 (원본)", grid: 8 },
      { lv: 1, dim: "8640×15360 (1/2)", grid: 6 },
      { lv: 2, dim: "4320×7680 (1/4)", grid: 4 },
      { lv: 3, dim: "2160×3840 (1/8)", grid: 2 }
    ];

    levels.forEach((info, i) => {
      const y = 44 + i * 58;
      const w = 250 - i * 32;
      const h = 44;
      const isSel = info.lv === selectedLevel;

      ctx.fillStyle = isSel ? "rgba(56,189,248,0.22)" : "rgba(15,23,42,0.85)";
      ctx.strokeStyle = isSel ? "#38bdf8" : "#334155";
      ctx.lineWidth = isSel ? 2.5 : 1.2;
      ctx.beginPath();
      ctx.roundRect(22, y, w, h, 7);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = isSel ? "#38bdf8" : "#cbd5e1";
      ctx.font = "bold 12px JetBrains Mono, monospace";
      ctx.fillText(`L${info.lv}: ${info.dim}`, 32, y + 26, w - 18);
    });

    const gx = 320;
    const gy = 34;
    const gw = 460;
    const gh = 236;
    const gridN = levels[selectedLevel].grid;
    const cellW = gw / gridN;
    const cellH = gh / gridN;

    const vpW = Math.min(gw * 0.85, (gw * 0.25) / Math.min(1, scale));
    const vpH = Math.min(gh * 0.85, (gh * 0.25) / Math.min(1, scale));
    const vpX = gx + viewCenter.x * gw - vpW / 2;
    const vpY = gy + viewCenter.y * gh - vpH / 2;

    let visibleTiles = 0;
    for (let r = 0; r < gridN; r++) {
      for (let c = 0; c < gridN; c++) {
        const tx = gx + c * cellW;
        const ty = gy + r * cellH;
        const intersects = !(tx + cellW < vpX || tx > vpX + vpW || ty + cellH < vpY || ty > vpY + vpH);
        if (intersects) visibleTiles++;

        ctx.fillStyle = intersects ? "rgba(52,211,153,0.24)" : "rgba(15,23,42,0.65)";
        ctx.strokeStyle = intersects ? "#34d399" : "#334155";
        ctx.lineWidth = intersects ? 1.8 : 1;
        ctx.fillRect(tx, ty, cellW, cellH);
        ctx.strokeRect(tx, ty, cellW, cellH);
      }
    }

    ctx.strokeStyle = "#fbbf24";
    ctx.lineWidth = 2.5;
    ctx.setLineDash([6, 4]);
    ctx.strokeRect(vpX, vpY, vpW, vpH);
    ctx.setLineDash([]);

    if (readout) {
      readout.textContent =
        `[TilePlanner] Scale=${scale.toFixed(2)}x → 1/S=${invScale.toFixed(2)} → 선택 레벨 L=${selectedLevel} | 전체 ${gridN * gridN}개 타일 중 가시 타일 ${visibleTiles}개만 GPU 업로드`;
    }
  }
  draw();
}

/* ============================================================================
   6. Chapter 04: BVH Spatial Index Tree & RoiMotionBounds Bisection Simulator
   ============================================================================ */
function initBvhCollisionAnim() {
  const canvas = document.getElementById("bvh-collision-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const readout = document.getElementById("bvh-collision-readout");

  const rois = [
    { id: 1, x: 55, y: 45, w: 85, h: 55, group: "Left" },
    { id: 2, x: 165, y: 120, w: 90, h: 65, group: "Left" },
    { id: 3, x: 90, y: 185, w: 110, h: 55, group: "Left" },
    { id: 4, x: 470, y: 50, w: 95, h: 65, group: "Right" },
    { id: 5, x: 600, y: 115, w: 110, h: 65, group: "Right" },
    { id: 6, x: 500, y: 185, w: 120, h: 55, group: "Right" }
  ];

  const leftNode = { x: 45, y: 35, w: 220, h: 215 };
  const rightNode = { x: 460, y: 40, w: 260, h: 210 };
  let mouse = { x: 200, y: 150 };

  canvas.addEventListener("mousemove", (e) => {
    const r = canvas.getBoundingClientRect();
    mouse.x = (e.clientX - r.left) * (canvas.width / r.width);
    mouse.y = (e.clientY - r.top) * (canvas.height / r.height);
    draw();
  });

  function inRect(pt, rc) {
    return pt.x >= rc.x && pt.x <= rc.x + rc.w && pt.y >= rc.y && pt.y <= rc.y + rc.h;
  }

  function draw() {
    const W = canvas.width;
    const H = canvas.height;
    ctx.clearRect(0, 0, W, H);

    const hitLeft = inRect(mouse, leftNode);
    const hitRight = inRect(mouse, rightNode);

    const drawNodeBox = (rc, hit, label) => {
      ctx.save();
      ctx.setLineDash([6, 4]);
      ctx.strokeStyle = hit ? "#fbbf24" : "#334155";
      ctx.lineWidth = hit ? 2.2 : 1.2;
      ctx.strokeRect(rc.x, rc.y, rc.w, rc.h);
      ctx.fillStyle = hit ? "#fbbf24" : "#64748b";
      ctx.font = "bold 12px JetBrains Mono, monospace";
      ctx.fillText(label, rc.x + 8, rc.y - 7);
      ctx.restore();
    };

    drawNodeBox(leftNode, hitLeft, hitLeft ? "BVH Left Node [탐색 진행!]" : "BVH Left Node [통째로 건너뜀!]");
    drawNodeBox(rightNode, hitRight, hitRight ? "BVH Right Node [탐색 진행!]" : "BVH Right Node [통째로 건너뜀!]");

    let hitRoiId = null;
    let checks = 2;

    rois.forEach((r) => {
      const parentHit = (r.group === "Left" && hitLeft) || (r.group === "Right" && hitRight);
      const isHit = parentHit && inRect(mouse, r);
      if (parentHit) checks++;
      if (isHit) hitRoiId = r.id;

      ctx.fillStyle = isHit
        ? "rgba(52,211,153,0.35)"
        : parentHit
        ? "rgba(56,189,248,0.18)"
        : "rgba(15,23,42,0.5)";
      ctx.strokeStyle = isHit ? "#34d399" : parentHit ? "#38bdf8" : "#334155";
      ctx.lineWidth = isHit ? 2.5 : 1.5;
      ctx.beginPath();
      ctx.roundRect(r.x, r.y, r.w, r.h, 6);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = isHit ? "#f8fafc" : "#94a3b8";
      ctx.font = "bold 12px JetBrains Mono, monospace";
      ctx.fillText(`ROI #${r.id}`, r.x + 12, r.y + 30);
    });

    ctx.fillStyle = "#fb7185";
    ctx.beginPath();
    ctx.arc(mouse.x, mouse.y, 5, 0, Math.PI * 2);
    ctx.fill();

    if (readout) {
      readout.textContent =
        `[RoiSpatialIndex.HitTest] 포인터=(${mouse.x.toFixed(0)}, ${mouse.y.toFixed(0)}) | 박스 검사 횟수: ${checks}회 | 적중 결과: ${hitRoiId ? "ROI #" + hitRoiId : "없음"}`;
    }
  }
  draw();
}
