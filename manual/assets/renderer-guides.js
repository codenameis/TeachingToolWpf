"use strict";

// HTML 안에서만 실행되는 수업 모형이다. C# Core 또는 실제 렌더러를 호출하지 않는다.
for (const lab of document.querySelectorAll("[data-navigation-lab]")) {
    const stage = lab.querySelector("svg");
    const world = lab.querySelector("[data-world]");
    const target = lab.querySelector("[data-target]");
    const lock = lab.querySelector("[data-lock]");
    const output = lab.querySelector("output");
    const history = [];
    const roi = { x:150, y:100, width:120, height:80 };
    const other = { x:600, y:300, width:200, height:160 };
    let scale, offsetX, offsetY, frame;

    function fit(bounds) {
        scale = Math.min(600 / bounds.width, 360 / bounds.height) * .92;
        offsetX = (600 - bounds.width * scale) / 2 - bounds.x * scale;
        offsetY = (360 - bounds.height * scale) / 2 - bounds.y * scale;
    }

    function render() {
        world.setAttribute("transform", `translate(${offsetX} ${offsetY}) scale(${scale})`);
        const selected = lab.querySelector("[data-roi-a]");
        selected.setAttribute("x", roi.x);
        selected.setAttribute("y", roi.y);
        const label = lab.querySelector("[data-label-a]");
        label.setAttribute("x", roi.x + 8);
        label.setAttribute("y", roi.y + 28);
        lab.querySelector("[data-image]").setAttribute("fill", frame % 2 ? "#d5e2df" : "#c8d8e6");
        for (const control of lab.querySelectorAll("[data-edit]")) control.disabled = lock.checked;
        lab.querySelector("[data-undo]").disabled = lock.checked || history.length === 0;
        output.textContent = `편집 선택: A · A 위치 (${roi.x}, ${roi.y}) px · 목록 대상: ${target.value || "없음"} · 배율 ${scale.toFixed(3)} DIP/px · Offset (${offsetX.toFixed(1)}, ${offsetY.toFixed(1)}) DIP · Undo ${history.length}건 · 프레임 ${frame} · ${lock.checked ? "입력 잠김" : "입력 가능"}`;
    }

    function move(dx, dy, shift) {
        if (lock.checked) return;
        const step = shift ? 10 : 1;
        // 이 수업에서는 이미지의 사각 경계만 제한한다. 실제 부모·회전·마스크는 Core가 처리한다.
        const x = Math.max(0, Math.min(1000 - roi.width, roi.x + dx * step));
        const y = Math.max(0, Math.min(600 - roi.height, roi.y + dy * step));
        if (x === roi.x && y === roi.y) return;
        history.push({ x:roi.x, y:roi.y });
        roi.x = x;
        roi.y = y;
        render();
    }

    function focusTarget() {
        if (lock.checked || !target.value) return;
        fit(target.value === "A" ? roi : other);
        render();
    }

    function reset() {
        roi.x = 150;
        roi.y = 100;
        frame = 1;
        history.length = 0;
        lock.checked = false;
        target.value = "";
        fit({ x:0, y:0, width:1000, height:600 });
        render();
    }

    target.addEventListener("change", focusTarget);
    lock.addEventListener("change", render);
    lab.querySelector("[data-focus]").addEventListener("click", focusTarget);
    lab.querySelector("[data-fit]").addEventListener("click", () => {
        if (lock.checked) return;
        fit({ x:0, y:0, width:1000, height:600 });
        render();
    });
    lab.querySelector("[data-publish]").addEventListener("click", () => { frame++; render(); });
    lab.querySelector("[data-reset]").addEventListener("click", reset);
    lab.querySelector("[data-undo]").addEventListener("click", () => {
        if (lock.checked || history.length === 0) return;
        Object.assign(roi, history.pop());
        render();
    });
    for (const button of lab.querySelectorAll("[data-move]")) {
        button.addEventListener("click", event => {
            const [dx, dy] = button.dataset.move.split(",").map(Number);
            move(dx, dy, event.shiftKey);
        });
    }
    const arrows = { ArrowLeft:[-1,0], ArrowRight:[1,0], ArrowUp:[0,-1], ArrowDown:[0,1] };
    stage.addEventListener("keydown", event => {
        if (!arrows[event.key] || event.ctrlKey || event.altKey || event.metaKey) return;
        event.preventDefault();
        move(...arrows[event.key], event.shiftKey);
    });
    reset();
}

document.getElementById("print")?.addEventListener("click", () => window.print());
let closedForPrint = [];
window.addEventListener("beforeprint", () => {
    closedForPrint = [...document.querySelectorAll("details:not([open])")];
    for (const item of closedForPrint) item.open = true;
});
window.addEventListener("afterprint", () => {
    for (const item of closedForPrint) item.open = false;
    closedForPrint = [];
});
