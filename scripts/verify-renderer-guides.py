"""렌더러 교육 HTML의 원본 발췌·링크·기본 구조를 검사한다. 브라우저 렌더링 검사는 아니다."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
import json

ROOT = Path(__file__).resolve().parents[1]
GUIDES = [ROOT / "manual" / name / "index.html" for name in ("wpf", "dx12", "vulkan", "wgpu", "skia")]


class GuideParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.ids = []
        self.links = []
        self.sources = []
        self.current_source = None
        self.headings = []
        self.references = []
        self.language = None
        self.stack = []
        self.nesting_errors = []

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag not in {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}:
            self.stack.append(tag)
        if tag == "html":
            self.language = attrs.get("lang")
        if "id" in attrs:
            self.ids.append(attrs["id"])
        if tag in ("a", "link", "script", "img"):
            value = attrs.get("href") or attrs.get("src")
            if value:
                self.links.append((tag, value))
        if tag in ("h1", "h2", "h3", "h4"):
            self.headings.append(int(tag[1]))
        for name in ("aria-labelledby", "aria-describedby", "for"):
            self.references.extend(attrs.get(name, "").split())
        if tag == "code" and "data-source" in attrs:
            self.current_source = (attrs, [])

    def handle_data(self, data):
        if self.current_source:
            self.current_source[1].append(data)

    def handle_endtag(self, tag):
        if not self.stack or self.stack[-1] != tag:
            self.nesting_errors.append(tag)
        else:
            self.stack.pop()
        if tag == "code" and self.current_source:
            attrs, pieces = self.current_source
            self.sources.append((attrs, "".join(pieces)))
            self.current_source = None


def parse(file):
    parser = GuideParser()
    parser.feed(file.read_text(encoding="utf-8"))
    parser.close()
    return parser


reports = []
for file in GUIDES:
    page = parse(file)
    assert page.language == "ko", file
    assert not page.stack and not page.nesting_errors, f"태그 닫힘: {file} {page.stack} {page.nesting_errors}"
    assert len(page.ids) == len(set(page.ids)), f"중복 ID: {file}"
    assert page.headings.count(1) == 1, f"h1: {file}"
    assert page.headings.count(2) == 13, f"장 수: {file}"
    assert all(b <= a + 1 for a, b in zip(page.headings, page.headings[1:])), f"제목 단계: {file}"
    assert all(ref in page.ids for ref in page.references), f"접근성 연결: {file}"
    assert len(page.sources) >= 15, f"소스 발췌 누락: {file}"
    for attributes, text in page.sources:
        source = (file.parent / attributes["data-source"]).resolve()
        assert source.is_relative_to(ROOT), source
        lines = source.read_text(encoding="utf-8-sig").splitlines()
        start, end = int(attributes["data-start"]), int(attributes["data-end"])
        assert 1 <= start <= end <= len(lines), (source, start, end)
        assert text == "\n".join(lines[start - 1:end]), f"원본 불일치: {file} → {source}:{start}"
    for tag, value in page.links:
        url = urlsplit(value)
        assert not url.scheme and not url.netloc, f"외부 의존/링크: {file} {value}"
        target = (file.parent / unquote(url.path)).resolve() if url.path else file
        assert target.is_file(), f"없는 링크: {file} → {target}"
        if url.fragment and target.suffix == ".html":
            assert unquote(url.fragment) in parse(target).ids, f"없는 앵커: {file} {value}"
    reports.append({"guide": file.parent.name, "source_excerpts": len(page.sources), "links": len(page.links), "chapters": 13})

print(json.dumps({"status": "PASS", "source_excerpts": sum(r["source_excerpts"] for r in reports), "guides": reports}, ensure_ascii=False, indent=2))
