"use strict";

// 선택된 사각형 한 개의 기본 동기 편집 경로를 설명하는 교육 모형이다.
// 실제 WPF/Core/GPU를 브라우저에서 실행하는 것이 아니다.
function createRoiLessonState() {
    return { stage: "ready", center: { x: 100, y: 80 }, delta: { x: 0, y: 0 },
        start: null, pointer: { x: 200, y: 160 }, dragging: false, history: 0 };
}

function updateRoiLesson(state, action, point = state.pointer) {
    if (action === "down") {
        // SVGPoint/DOMPoint의 x·y는 프로토타입 접근자일 수 있으므로 명시적으로 읽는다.
        state.start = { x: point.x, y: point.y };
        state.pointer = { x: point.x, y: point.y };
        state.delta = { x: 0, y: 0 };
        state.dragging = true;
        state.stage = "down";
    } else if ((action === "move" || action === "up") && state.dragging) {
        state.pointer = { x: point.x, y: point.y };
        const dx = point.x - state.start.x;
        const dy = point.y - state.start.y;
        const moved = Math.hypot(dx, dy) >= 2;
        // 가상 화면의 2DIP가 원본 1px이다. 이 실습은 250×160px 이미지 안으로 이동을 제한한다.
        state.delta = {
            x: Math.max(30 - state.center.x, Math.min(220 - state.center.x, moved ? dx / 2 : 0)),
            y: Math.max(20 - state.center.y, Math.min(140 - state.center.y, moved ? dy / 2 : 0))
        };
        state.stage = "move";
        if (action === "up") {
            if (state.delta.x !== 0 || state.delta.y !== 0) state.history++;
            state.center = { x: state.center.x + state.delta.x, y: state.center.y + state.delta.y };
            state.delta = { x: 0, y: 0 };
            state.dragging = false;
            state.stage = "up";
        }
    } else if (action === "cancel" && state.dragging) {
        state.delta = { x: 0, y: 0 };
        state.dragging = false;
        state.stage = "cancel";
    }
}

function initializeRoiDragLesson(root) {
    const find = id => root.querySelector(`#${id}`);
    const svg = find("roi-demo-scene");
    const roi = find("roi-demo-shape");
    const ghost = find("roi-demo-ghost");
    const cursor = find("roi-demo-pointer");
    const code = find("roi-demo-code");
    const stages = JSON.parse(find("roi-demo-stages").textContent);
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    let state = createRoiLessonState();
    let displayedStage = "";
    let capturedPointer = null;
    let frame = null;
    let running = false;
    let clockStart = 0;
    const format = value => Math.round(value * 10) / 10;
    const pair = point => `가로 ${format(point.x)} · 세로 ${format(point.y)}`;

    function render() {
        const shown = { x: state.center.x + state.delta.x, y: state.center.y + state.delta.y };
        roi.setAttribute("x", shown.x * 2 - 60);
        roi.setAttribute("y", shown.y * 2 - 40);
        ghost.setAttribute("x", state.center.x * 2 - 60);
        ghost.setAttribute("y", state.center.y * 2 - 40);
        ghost.setAttribute("visibility", state.dragging ? "visible" : "hidden");
        cursor.setAttribute("transform", `translate(${state.pointer.x} ${state.pointer.y})`);
        cursor.setAttribute("data-pressed", String(state.dragging));
        find("roi-demo-center").textContent = pair(state.center) + " px";
        find("roi-demo-preview").textContent = pair(state.delta) + " px";
        find("roi-demo-visible").textContent = pair(shown) + " px";
        find("roi-demo-history").textContent = `${state.history}개`;
        find("roi-demo-pointer-value").textContent = pair(state.pointer) + " DIP";
        find("roi-demo-cancel").disabled = !state.dragging;
        find("roi-demo-pause").disabled = !running;
        find("roi-demo-next").disabled = running || capturedPointer !== null;
        if (displayedStage === state.stage) return;
        displayedStage = state.stage;
        const stage = stages[state.stage];
        find("roi-demo-status").textContent = stage.title;
        find("roi-demo-explanation").textContent = stage.explanation;
        find("roi-demo-chain").textContent = stage.chain;
        find("roi-demo-code-caption").textContent = stage.caption;
        find("roi-demo-source").textContent = stage.source ? `${stage.source} · 실제 코드 발췌` : "아직 실행한 편집 코드가 없습니다.";
        code.textContent = stage.code;
        highlightCode(code, "csharp");
        for (const item of root.querySelectorAll("[data-roi-phase]")) {
            item.setAttribute("aria-current", item.dataset.roiPhase === state.stage ? "step" : "false");
        }
    }

    function stopPlayback() {
        if (frame !== null) cancelAnimationFrame(frame);
        frame = null;
        running = false;
    }

    function releasePointer() {
        const id = capturedPointer;
        capturedPointer = null;
        if (id !== null && svg.hasPointerCapture(id)) svg.releasePointerCapture(id);
    }

    function cancel() {
        stopPlayback();
        updateRoiLesson(state, "cancel");
        releasePointer();
        render();
    }

    function reset() {
        stopPlayback();
        releasePointer();
        state = createRoiLessonState();
        render();
    }

    function next() {
        if (state.stage === "up" || state.stage === "cancel") reset();
        if (state.stage === "ready") updateRoiLesson(state, "down", { x: 200, y: 160 });
        else if (state.stage === "down") updateRoiLesson(state, "move", { x: 224, y: 172 });
        else updateRoiLesson(state, "up", { x: 240, y: 180 });
        render();
    }

    function animate(now) {
        if (!running) return;
        if (clockStart === 0) clockStart = now;
        const elapsed = now - clockStart;
        if (elapsed < 900) {
            if (state.stage === "ready") updateRoiLesson(state, "down", { x: 200, y: 160 });
        } else if (elapsed < 3100) {
            const progress = reducedMotion ? 0.65 : Math.min(1, (elapsed - 900) / 2200);
            updateRoiLesson(state, "move", { x: 200 + 36 * progress, y: 160 + 18 * progress });
        } else {
            updateRoiLesson(state, "up", { x: 240, y: 180 });
            stopPlayback();
        }
        render();
        if (running) frame = requestAnimationFrame(animate);
    }

    function pointerPosition(event) {
        const point = svg.createSVGPoint();
        point.x = event.clientX;
        point.y = event.clientY;
        return point.matrixTransform(svg.getScreenCTM().inverse());
    }

    svg.addEventListener("pointerdown", event => {
        if (event.button !== 0 || capturedPointer !== null) return;
        cancel();
        const point = pointerPosition(event);
        const center = { x: state.center.x * 2, y: state.center.y * 2 };
        if (Math.abs(point.x - center.x) > 60 || Math.abs(point.y - center.y) > 40) return;
        updateRoiLesson(state, "down", point);
        capturedPointer = event.pointerId;
        svg.setPointerCapture(capturedPointer);
        svg.focus();
        event.preventDefault();
        render();
    });
    svg.addEventListener("pointermove", event => {
        if (event.pointerId !== capturedPointer) return;
        updateRoiLesson(state, "move", pointerPosition(event));
        render();
    });
    svg.addEventListener("pointerup", event => {
        if (event.pointerId !== capturedPointer) return;
        updateRoiLesson(state, "up", pointerPosition(event));
        releasePointer();
        render();
    });
    svg.addEventListener("pointercancel", cancel);
    svg.addEventListener("lostpointercapture", () => { if (capturedPointer !== null) cancel(); });
    root.addEventListener("keydown", event => {
        if (event.key === "Escape") { event.preventDefault(); cancel(); }
    });
    find("roi-demo-play").addEventListener("click", () => {
        reset(); running = true; clockStart = 0; render(); frame = requestAnimationFrame(animate);
    });
    find("roi-demo-pause").addEventListener("click", () => { stopPlayback(); render(); });
    find("roi-demo-next").addEventListener("click", next);
    find("roi-demo-cancel").addEventListener("click", cancel);
    find("roi-demo-reset").addEventListener("click", reset);
    window.addEventListener("blur", cancel);
    document.addEventListener("visibilitychange", () => { if (document.hidden) cancel(); });
    window.addEventListener("beforeprint", cancel);
    for (const button of root.querySelectorAll("button")) button.disabled = false;
    find("roi-demo-help").textContent = reducedMotion
        ? "움직임 줄이기 설정이 켜져 있어 재생 시 위치를 단계별로 바꿉니다. 다음 단계 버튼으로도 조작할 수 있습니다."
        : "초록 사각형을 직접 끌어 보세요. 키보드는 ‘다음 단계’로 진행하고 Escape로 취소할 수 있습니다.";
    render();
}

const roiDragLesson = document.getElementById("roi-drag-lab");
if (roiDragLesson) initializeRoiDragLesson(roiDragLesson);
