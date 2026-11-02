// Node의 DOM 대역으로 교육용 상태 전이를 검증한다. 실제 브라우저/접근성/레이아웃 시험은 아니다.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const script = fs.readFileSync(path.join(root, 'manual/assets/renderer-guides.js'), 'utf8');
const mutate = process.argv.includes('--mutation');
const evaluatedScript = mutate ? script.replace('if (lock.checked) return;', '// mutation: removed movement lock') : script;

class Element {
    constructor(tag, attrs) {
        this.tag = tag;
        this.attrs = attrs;
        this.dataset = {};
        for (const [key, value] of Object.entries(attrs)) {
            if (key.startsWith('data-')) this.dataset[key.slice(5)] = value;
        }
        this.events = new Map();
        this.checked = false;
        this.disabled = false;
        this.value = '';
        this.textContent = '';
    }
    addEventListener(type, handler) { this.events.set(type, handler); }
    setAttribute(name, value) { this.attrs[name] = String(value); }
    emit(type, event = {}) { this.events.get(type)?.(event); }
}

const reports = [];
for (const name of ['wpf', 'dx12', 'vulkan', 'wgpu', 'skia']) {
    const html = fs.readFileSync(path.join(root, 'manual', name, 'index.html'), 'utf8');
    const markup = html.match(/<div class="lesson-lab" data-navigation-lab>([\s\S]*?)<\/noscript><\/div>/)?.[1];
    assert.ok(markup, `${name}: 학습 모형 누락`);
    const elements = [...markup.matchAll(/<(button|select|input|output|svg|g|rect|text)\b([^>]*)>/g)].map(([,tag,source]) => {
        const attrs = {};
        for (const [,key,value] of source.matchAll(/([\w-]+)(?:="([^"]*)")?/g)) attrs[key] = value ?? '';
        return new Element(tag, attrs);
    });
    const matches = (element, selector) => selector.startsWith('[')
        ? Object.hasOwn(element.attrs, selector.slice(1,-1)) : element.tag === selector;
    const lab = {
        querySelector(selector) {
            const result = elements.find(item => matches(item, selector));
            assert.ok(result, `${name}: ${selector} 없음`);
            return result;
        },
        querySelectorAll(selector) { return elements.filter(item => matches(item, selector)); }
    };
    const printEvents = new Map();
    const printButton = new Element('button', {});
    let printed = false;
    const details = [{open:false}, {open:true}, {open:false}];
    const document = {
        querySelectorAll(selector) {
            if (selector === '[data-navigation-lab]') return [lab];
            if (selector === 'details:not([open])') return details.filter(item=>!item.open);
            throw Error(`검증되지 않은 selector: ${selector}`);
        },
        getElementById(id) { assert.equal(id, 'print'); return printButton; }
    };
    const window = { addEventListener(type,handler) { printEvents.set(type,handler); }, print() { printed=true; } };
    vm.runInNewContext(evaluatedScript, { document, window });
    const output = lab.querySelector('output');
    const control = attr => lab.querySelector(`[data-${attr}]`);
    const click = attr => control(attr).emit('click');
    const stage = lab.querySelector('svg');
    function key(key, extra={}) {
        let prevented = false;
        stage.emit('keydown', { key, preventDefault() { prevented=true; }, ...extra });
        return prevented;
    }

    assert.match(output.textContent, /A 위치 \(150, 100\).*배율 0\.552.*Offset \(24\.0, 14\.4\).*Undo 0건.*프레임 1/);
    assert.equal(key('ArrowRight'),true);
    assert.match(output.textContent,/A 위치 \(151, 100\).*Undo 1건/);
    key('ArrowRight',{shiftKey:true});
    assert.match(output.textContent,/A 위치 \(161, 100\).*Undo 2건/);
    const afterMoves = output.textContent;
    assert.equal(key('ArrowRight',{ctrlKey:true}),false);
    assert.equal(output.textContent,afterMoves);
    control('target').value = 'B';
    control('target').emit('change');
    // 600/200=3, 360/160=2.25 → min * .92 = 2.07.
    assert.match(output.textContent,/편집 선택: A.*A 위치 \(161, 100\).*목록 대상: B.*배율 2\.070.*Undo 2건/);
    assert.equal(control('target').events.has('keydown'),false);
    const fittedTransform = control('world').attrs.transform;
    control('lock').checked = true;
    control('lock').emit('change');
    assert.ok(lab.querySelectorAll('[data-edit]').every(item=>item.disabled));
    const locked = output.textContent;
    key('ArrowLeft');
    click('fit');
    click('focus');
    click('undo');
    assert.equal(output.textContent,locked,'잠금 중 입력은 상태를 바꾸면 안 된다.');
    click('publish');
    assert.match(output.textContent,/A 위치 \(161, 100\).*Undo 2건.*프레임 2.*입력 잠김/);
    assert.equal(control('world').attrs.transform,fittedTransform);
    assert.equal(control('image').attrs.fill,'#c8d8e6');
    control('lock').checked = false;
    control('lock').emit('change');
    click('fit');
    assert.match(output.textContent,/배율 0\.552.*Undo 2건/);
    click('undo');
    assert.match(output.textContent,/A 위치 \(151, 100\).*Undo 1건/);
    click('undo');
    assert.match(output.textContent,/A 위치 \(150, 100\).*Undo 0건/);
    assert.equal(control('undo').disabled,true);
    for (let i=0;i<15;i++) key('ArrowLeft',{shiftKey:true});
    assert.match(output.textContent,/A 위치 \(0, 100\).*Undo 15건/);
    key('ArrowLeft');
    assert.match(output.textContent,/A 위치 \(0, 100\).*Undo 15건/);
    click('reset');
    assert.match(output.textContent,/A 위치 \(150, 100\).*Undo 0건.*프레임 1/);
    lab.querySelectorAll('[data-move]')[3].emit('click',{shiftKey:true});
    assert.match(output.textContent,/A 위치 \(160, 100\).*Undo 1건/);
    control('target').value = 'A';
    control('target').emit('change');
    const beforeRefit = control('world').attrs.transform;
    key('ArrowRight');
    assert.equal(control('world').attrs.transform,beforeRefit);
    click('focus');
    assert.notEqual(control('world').attrs.transform,beforeRefit,'다시 맞춤은 현재 ROI 위치를 반영한다.');
    assert.match(output.textContent,/A 위치 \(161, 100\).*Undo 2건/);
    printButton.emit('click');
    assert.equal(printed,true);
    printEvents.get('beforeprint')();
    assert.ok(details.every(item=>item.open));
    printEvents.get('afterprint')();
    assert.deepEqual(details.map(item=>item.open),[false,true,false]);
    reports.push({ guide:name, status:'PASS', checks:'좌표·1/10px·목록/선택·잠금·프레임·Undo·경계·초기화·인쇄 상태 복원' });
}
console.log(JSON.stringify({mode:'DOM test doubles; no browser rendering',reports}, null, 2));
