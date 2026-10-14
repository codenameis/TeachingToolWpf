"use strict";

const chapters = [
    ["01-image", "01. 이미지는 숫자다", "픽셀·카메라·메모리와 stride"],
    ["02-coordinates", "02. 좌표·확대·회전", "단위, 마우스 고정 확대와 역변환"],
    ["03-processing", "03. 필터·윤곽·마스크", "작은 행렬로 직접 계산하는 영상처리"],
    ["04-design", "04. 책임을 나누는 설계", "실제 코드에 연결하는 SOLID 5개·GRASP 9개"],
    ["05-editing", "05. ROI·선택·Undo/Redo", "불변 문서, preview, 계층과 이력"],
    ["06-streaming", "06. 큰 영상과 버퍼의 수명", "타일·피라미드·최신 프레임·소유권"],
    ["07-rendering", "07. WPF와 DX12로 그리기", "스레드, 명령, 셰이더와 fence"],
    ["08-inspection", "08. 패턴을 학습하고 검사하기", "NCC·형상 검색·원본 검증·판정"],
    ["09-build", "09. 작은 캔버스를 직접 구축하기", "계산·입력·화면을 연결하는 실습 3개"],
    ["12-reference", "부록. 용어·수식·다음 읽기", "검색 가능한 용어 사전과 공식 출처"]
];

const chapterList = document.getElementById("chapter-list");
if (chapterList) {
    for (const [slug, title, description] of chapters) {
        const link = document.createElement("a");
        link.className = "chapter-card";
        link.href = `${slug}.html`;
        const heading = document.createElement("strong");
        heading.textContent = title;
        const summary = document.createElement("span");
        summary.textContent = description;
        link.append(heading, summary);
        chapterList.append(link);
    }
}

document.getElementById("print")?.addEventListener("click", () => window.print());
let closedAnswers = [];
window.addEventListener("beforeprint", () => {
    closedAnswers = [...document.querySelectorAll("details:not([open])")];
    for (const answer of closedAnswers) answer.open = true;
});
window.addEventListener("afterprint", () => {
    for (const answer of closedAnswers) answer.open = false;
    closedAnswers = [];
});

const number = id => Number(document.getElementById(id).value);
const format = value => value.toLocaleString("ko-KR", { maximumFractionDigits: 3 });
function connectLab(id, update) {
    const lab = document.getElementById(id);
    if (!lab) return;
    lab.addEventListener("input", update);
    lab.addEventListener("change", update);
    update();
}

connectLab("memory-lab", () => {
    const width = number("mem-width");
    const height = number("mem-height");
    const bytes = number("mem-bytes");
    const fps = number("mem-fps");
    const output = document.getElementById("memory-output");
    if (![...document.querySelectorAll("#memory-lab input")].every(input => input.checkValidity()) ||
        ![width, height, bytes, fps].every(value => Number.isInteger(value) && value > 0)) {
        output.textContent = "표시된 범위 안의 양의 정수를 입력하세요. 너비·높이 상한은 131072입니다.";
        return;
    }
    const frameBytes = width * height * bytes;
    output.textContent = `${format(frameBytes)} bytes, 즉 ${format(frameBytes / 2 ** 20)} MiB/장. ` +
        `${format(fps)}장/초 전체 복사 가정: ${format(frameBytes * fps / 2 ** 30)} GiB/초. 실제 처리 속도 측정값이 아닙니다.`;
});

connectLab("coordinate-lab", () => {
    const degrees = number("angle");
    const scale = number("zoom");
    const radians = degrees * Math.PI / 180;
    const x = 160 + 70 * Math.cos(radians);
    const y = 110 + 70 * Math.sin(radians);
    const point = document.getElementById("rotated-point");
    point.setAttribute("cx", x);
    point.setAttribute("cy", y);
    const arm = document.getElementById("rotation-arm");
    arm.setAttribute("x2", x);
    arm.setAttribute("y2", y);
    // 그림 안에서는 중심이 고정되도록 Offset을 역산한다. 문서의 원본 좌표는 그대로다.
    const offsetX = 160 * (1 - scale);
    const offsetY = 110 * (1 - scale);
    document.getElementById("coordinate-scene").setAttribute("transform",
        `translate(${offsetX} ${offsetY}) scale(${scale})`);
    document.getElementById("coordinate-output").textContent = `${degrees}°: 원본 (${x.toFixed(2)}, ${y.toFixed(2)}). ` +
        `배율 ${scale.toFixed(2)}, 중심 고정 Offset (${offsetX.toFixed(2)}, ${offsetY.toFixed(2)}). ` +
        `화면 (${(x * scale + offsetX).toFixed(2)}, ${(y * scale + offsetY).toFixed(2)}) DIP.`;
});

const pixelValues = [0, 40, 80, 120, 20, 100, 160, 220, 30, 90, 180, 255, 10, 60, 140, 200];
const thresholdLab = document.getElementById("threshold-lab");
if (thresholdLab) {
    for (const id of ["original-pixels", "binary-pixels"]) {
        const grid = document.getElementById(id);
        for (let i = 0; i < pixelValues.length; i++) {
            const cell = document.createElement("span");
            cell.className = "pixel";
            grid.append(cell);
        }
    }
    connectLab("threshold-lab", () => {
        const threshold = number("threshold");
        const binary = pixelValues.map(value => value > threshold ? 255 : 0);
        for (const [id, values] of [["original-pixels", pixelValues], ["binary-pixels", binary]]) {
            [...document.getElementById(id).children].forEach((cell, index) => {
                const value = values[index];
                cell.style.backgroundColor = `rgb(${value},${value},${value})`;
                cell.style.color = value >= 118 ? "#000" : "#fff";
                cell.textContent = value;
            });
        }
        document.getElementById("threshold-output").textContent =
            `임계값 ${threshold} · 입력 밝기가 임계값보다 크면 흰색` +
            ` · 흰 픽셀은 전체16개 중 ${binary.filter(value => value === 255).length}개. 임계값과 같은 밝기는 검정입니다.`;
    });
}

connectLab("tile-lab", () => {
    const scale = number("tile-scale");
    const dpi = number("tile-dpi");
    const level = Math.min(4, Math.max(0, Math.floor(Math.log2(1 / (scale * dpi)))));
    const span = 512 * 2 ** level;
    const columns = Math.ceil(8192 / span);
    document.getElementById("tile-output").textContent = `원본1픽셀의 화면 길이 ${format(scale * dpi)} 물리 px, ` +
        `단계 ${level}. 한 타일이 덮는 원본 길이 ${span}px. 전체 단계 격자는 ${columns}행 ${columns}열 (${columns ** 2}개). ` +
        "실제 요청은 가시 영역과 교차하는 일부입니다.";
});

connectLab("visible-tile-lab", () => {
    // 6장의 고정 level-0 예제. 실제 Planner와 같은 격자 범위/제외 끝을 사용한다.
    const left = number("visible-x");
    const top = number("visible-y");
    const width = number("visible-size");
    const height = width === 512 ? 512 : 400;
    const clamp = (value, end) => Math.min(end, Math.max(0, value));
    const firstColumn = clamp(Math.floor(left / 512), 3);
    const columnEnd = clamp(Math.ceil((left + width) / 512), 3);
    const firstRow = clamp(Math.floor(top / 512), 2);
    const rowEnd = clamp(Math.ceil((top + height) / 512), 2);
    const keys = [];
    for (let row = 0; row < 2; row++) {
        for (let column = 0; column < 3; column++) {
            const requested = column >= firstColumn && column < columnEnd &&
                row >= firstRow && row < rowEnd;
            document.getElementById(`visible-cell-${column}-${row}`)
                .setAttribute("fill", requested ? "#bce6dc" : "#edf1f3");
            if (requested) keys.push(`(${column},${row})`);
        }
    }
    const viewport = document.getElementById("visible-window");
    for (const [name, value] of Object.entries({ x: left, y: top, width, height })) {
        viewport.setAttribute(name, value);
    }
    document.getElementById("visible-output").textContent =
        `원본 가로 ${left} 이상 ${left + width} 미만, 세로 ${top} 이상 ${top + height} 미만. ` +
        `열 ${firstColumn} 이상 ${columnEnd} 미만, 행 ${firstRow} 이상 ${rowEnd} 미만. ` +
        `${keys.length}개: ${keys.length ? keys.join(", ") : "요청 없음"}. ` +
        "개요·캐시는 제외한 level 0 계획입니다.";
});

const glossarySearch = document.getElementById("glossary-search");
if (glossarySearch) {
    const terms = [...document.querySelectorAll("#glossary dt")];
    function filterGlossary() {
        const query = glossarySearch.value.trim().toLocaleLowerCase();
        let count = 0;
        for (const term of terms) {
            const meaning = term.nextElementSibling;
            const visible = `${term.textContent} ${meaning.textContent}`.toLocaleLowerCase().includes(query);
            term.hidden = !visible;
            meaning.hidden = !visible;
            if (visible) count++;
        }
        document.getElementById("glossary-count").textContent = `${count}개 용어 표시`;
    }
    glossarySearch.addEventListener("input", filterGlossary);
    filterGlossary();
}
