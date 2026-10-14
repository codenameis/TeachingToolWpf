"use strict";

// 색상 표시는 어휘 단위의 읽기 보조다. 컴파일러의 타입 판정은 하지 않는다.
const csharpKeywords = new Set((
    "abstract as async await base bool break byte case catch char checked class const continue " +
    "decimal default delegate do double else enum event explicit extern false finally fixed float " +
    "for foreach get global if implicit in init int interface internal is lock long namespace new " +
    "nint not null object operator or out override params partial private protected public readonly " +
    "record ref required return sbyte sealed set short sizeof stackalloc static string struct switch " +
    "this throw true try typeof uint ulong unchecked unsafe ushort using value var virtual void volatile " +
    "when where while with yield"
).split(" "));
const hlslKeywords = new Set((
    "bool break cbuffer const continue discard else false float float2 float3 float4 float4x4 " +
    "for if in inout int int2 int3 int4 out register return static struct true uint uint2 uint3 uint4 void while"
).split(" "));

function highlightCode(code, language) {
    if (!["csharp", "hlsl", "xml"].includes(language)) return;
    const source = code.textContent;
    const keywords = language === "hlsl" ? hlslKeywords : csharpKeywords;
    const tokens = language !== "xml"
        ? /\/\/[^\r\n]*|\/\*[\s\S]*?\*\/|(?:\$@|@\$|@)"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\b(?:0x[\da-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*)\b|@?[A-Za-z_]\w*/g
        : /<!--[\s\S]*?-->|<\/?[\w:.-]+|[\w:.-]+(?=\s*=)|"[^"]*"|'[^']*'|\/?>/g;
    const fragment = document.createDocumentFragment();
    let offset = 0;
    for (const match of source.matchAll(tokens)) {
        fragment.append(document.createTextNode(source.slice(offset, match.index)));
        const token = match[0];
        let kind = "";
        if (language === "xml") {
            kind = token.startsWith("<!--") ? "comment" : /^["']/.test(token) ? "string" :
                /^[<\/>]/.test(token) ? "keyword" : "type";
        } else {
            kind = token.startsWith("//") || token.startsWith("/*") ? "comment" :
                /^(?:\$@|@\$|@|\$)?["']/.test(token) ? "string" :
                /^\d/.test(token) ? "number" : keywords.has(token) ? "keyword" :
                /^[A-Z]/.test(token) ? "type" : "";
        }
        if (kind) {
            const span = document.createElement("span");
            span.className = `syntax-${kind}`;
            span.textContent = token;
            fragment.append(span);
        } else {
            fragment.append(document.createTextNode(token));
        }
        offset = match.index + token.length;
    }
    fragment.append(document.createTextNode(source.slice(offset)));
    code.replaceChildren(fragment);
}

async function copyCode(code, button, status) {
    button.disabled = true;
    status.textContent = "";
    try {
        await navigator.clipboard.writeText(code.textContent);
        status.textContent = "복사했습니다";
    } catch {
        // file URL 등에서 클립보드 권한이 없으면 사용자가 직접 복사할 수 있게 선택한다.
        code.parentElement.focus();
        const range = document.createRange();
        range.selectNodeContents(code);
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        status.textContent = "자동 복사를 사용할 수 없습니다. 선택된 코드를 Ctrl+C / ⌘C로 복사하세요.";
    } finally {
        button.disabled = false;
    }
}

for (const block of document.querySelectorAll("details.code-example")) {
    const code = block.querySelector("pre code");
    const button = block.querySelector("[data-copy-code]");
    const status = block.querySelector(".code-copy-status");
    let highlighted = false;
    const showCode = () => {
        if (!block.open || highlighted) return;
        highlightCode(code, code.dataset.language);
        highlighted = true;
    };
    block.addEventListener("toggle", showCode);
    showCode();
    button.hidden = false;
    button.addEventListener("click", () => copyCode(code, button, status));
}

function openContainingDetails(element) {
    for (let parent = element?.parentElement; parent; parent = parent.parentElement) {
        if (parent.tagName === "DETAILS") parent.open = true;
    }
}

function revealSourceCode(id) {
    const block = document.getElementById(id);
    if (!block || !block.matches("details.code-example")) return null;
    openContainingDetails(block);
    block.open = true;
    return block;
}

// 파일 URL을 열지 않고 이 페이지에 저장한 전체 소스를 펼친다.
for (const link of document.querySelectorAll("a[data-code-target]")) {
    link.addEventListener("click", event => {
        if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const block = revealSourceCode(link.dataset.codeTarget);
        block?.querySelector("summary").focus({ preventScroll: true });
        // 기본 앵커 이동을 유지해 주소 복사와 브라우저 뒤로 가기가 작동하게 한다.
    });
}
for (const link of document.querySelectorAll("a[data-reading-target]")) {
    link.addEventListener("click", () => openContainingDetails(document.getElementById(link.dataset.readingTarget)));
}
function revealLinkedContent() {
    const id = window.location.hash.slice(1);
    openContainingDetails(document.getElementById(id));
    revealSourceCode(id);
}
window.addEventListener("hashchange", revealLinkedContent);
revealLinkedContent();
