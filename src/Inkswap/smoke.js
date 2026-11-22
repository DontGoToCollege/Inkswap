// Self-check run by `Inkswap.exe --smoke` (see RunSmokeAsync in MainWindow.xaml.cs). Needs no AI model:
// opens a drawn test page, draws a box by hand, types a translation and checks the original text was
// erased and the new text lettered into the bubble. Throwing fails the check.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const c = Object.assign(document.createElement('canvas'), { width: 400, height: 300 });
  const g = c.getContext('2d');
  g.fillStyle = '#fff'; g.fillRect(0, 0, 400, 300);
  g.lineWidth = 3; g.beginPath(); g.ellipse(200, 150, 150, 100, 0, 0, Math.PI * 2); g.stroke();
  g.fillStyle = '#000'; g.font = 'bold 40px "Yu Gothic", sans-serif'; g.textAlign = 'center'; g.fillText('テスト', 200, 165);
  const files = new DataTransfer();
  files.items.add(new File([await new Promise(r => c.toBlob(r))], 'smoke.png', { type: 'image/png' }));
  const input = document.querySelector('#fileIn');
  input.files = files.files;
  input.dispatchEvent(new Event('change'));
  const app = window.inkswap;
  for (let i = 0; i < 50 && !app.pages.length; i++) await sleep(100);
  if (!app.pages.length) throw new Error('the test page did not open');
  document.querySelector('#modal').classList.remove('on');

  // draw a box around the text the way a user would
  const stage = document.querySelector('#stage'), r = stage.getBoundingClientRect();
  const at = (x, y) => ({ clientX: r.left + x / 400 * r.width, clientY: r.top + y / 300 * r.height, bubbles: true, button: 0 });
  stage.dispatchEvent(new PointerEvent('pointerdown', at(130, 120)));
  dispatchEvent(new PointerEvent('pointermove', at(270, 180)));
  dispatchEvent(new PointerEvent('pointerup', at(270, 180)));
  const page = app.pages[0];
  if (page.items.length !== 1) throw new Error('drawing a box made ' + page.items.length + ' items');

  const dark = () => {   // dark pixels inside the drawn box on the visible canvas
    const d = document.querySelector('#cv').getContext('2d').getImageData(130, 120, 140, 60).data;
    let n = 0; for (let i = 0; i < d.length; i += 4) if (d[i] < 100) n++; return n;
  };
  const before = dark();
  const ta = document.querySelector('.card textarea.dst');
  ta.value = ' '; ta.dispatchEvent(new Event('input', { bubbles: true }));
  await sleep(400);
  // a blank-but-present translation draws nothing, so check erasing with a real one and then compare
  ta.value = 'Hello there!'; ta.dispatchEvent(new Event('input', { bubbles: true }));
  await sleep(600);
  const it = page.items[0];
  const after = dark();
  if (!(before > 200)) throw new Error('test text not found before (' + before + ' dark pixels)');
  if (after === before) throw new Error('the page did not change after typing a translation');
  // the original glyph strokes must be gone: sample the middle of テ's top bar
  const px = document.querySelector('#cv').getContext('2d').getImageData(0, 0, 400, 300);
  const area = it.tbox.map(Math.round);
  if (!(area[2] - area[0] > 140)) throw new Error('text box did not grow to the bubble: ' + area);
  // nudging the erase area (E switches to it, arrows move it) must leave the text box where it is
  const key = k => dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
  const layer = document.querySelector('#sLayer');
  layer.value = 'both'; layer.dispatchEvent(new Event('change'));   // don't depend on the saved setting
  key('e'); key('ArrowLeft'); key('ArrowLeft'); key('ArrowLeft');
  await sleep(300);
  if (Math.round(it.box[0]) !== 127) throw new Error('arrow keys did not move the erase area: ' + it.box);
  if (it.tbox.map(Math.round).join() !== area.join()) throw new Error('moving the erase area moved the text: ' + it.tbox);
  key('e');
  // dragging the selected text box's bottom-right corner resizes only the text box
  const drag = (el, x2, y2) => {
    const b = el.getBoundingClientRect(), sx = b.left + b.width / 2, sy = b.top + b.height / 2;
    el.dispatchEvent(new PointerEvent('pointerdown', { clientX: sx, clientY: sy, bubbles: true, button: 0 }));
    dispatchEvent(new PointerEvent('pointermove', { clientX: sx + x2, clientY: sy + y2, bubbles: true }));
    dispatchEvent(new PointerEvent('pointerup', { clientX: sx + x2, clientY: sy + y2, bubbles: true }));
  };
  const boxBefore = it.box.join();
  drag(document.querySelector(`.bx.tx[data-id="${it.id}"] u.se`), -20, -10);
  if (it.tbox[2] >= area[2] || it.box.join() !== boxBefore) throw new Error('corner drag went wrong: ' + it.tbox + ' / ' + it.box);
  // in the erase layer, dragging the orange area moves it and leaves the text box alone
  const tboxBefore = it.tbox.join();
  key('e');
  await sleep(100);
  drag(document.querySelector(`.bx.er[data-id="${it.id}"]`), 15, 0);
  if (!(it.box[0] > 130) || it.tbox.join() !== tboxBefore) throw new Error('erase area drag went wrong: ' + it.box + ' / ' + it.tbox);
  key('e');
  // H hides every box (the page as it will be saved) and shows them again
  key('h');
  if (!document.querySelector('#boxes').classList.contains('hide') || document.querySelector('#cShow').checked) throw new Error('H did not hide the boxes');
  key('h');
  if (document.querySelector('#boxes').classList.contains('hide')) throw new Error('H did not show the boxes again');
  // page list: open two more pages, pick with Shift/Ctrl, close with the hover ✕ and the right-click menu
  const more = new DataTransfer();
  for (const n of ['b', 'c']) more.items.add(new File([await new Promise(r => c.toBlob(r))], n + '.png', { type: 'image/png' }));
  input.files = more.files; input.dispatchEvent(new Event('change'));
  for (let i = 0; i < 50 && app.pages.length < 3; i++) await sleep(100);
  const thumb = i => document.querySelectorAll('.thumb')[i];
  const click = (i, o = {}) => thumb(i).dispatchEvent(new MouseEvent('click', { bubbles: true, ...o }));
  const names = sel => [...document.querySelectorAll(sel + ' span')].map(s => s.textContent.replace('✓ ', '')).join();
  click(0); click(2, { shiftKey: true });
  if (names('.thumb.picked') !== 'smoke,b,c') throw new Error('Shift+click picked ' + names('.thumb.picked'));
  click(1, { ctrlKey: true });
  if (names('.thumb.picked') !== 'smoke,c') throw new Error('Ctrl+click left ' + names('.thumb.picked'));
  thumb(2).querySelector('.x').click();   // c has no work, so it closes without asking
  if (names('.thumb') !== 'smoke,b') throw new Error('the ✕ left ' + names('.thumb'));
  const confirmOk = window.confirm; window.confirm = () => true;   // the test page has work: answer the question with yes
  thumb(1).dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 60, clientY: 60 }));
  const menuItem = t => [...document.querySelectorAll('#ctx button')].find(b => b.textContent.startsWith(t));
  menuItem('Close all other pages').click();
  window.confirm = confirmOk;
  if (names('.thumb') !== 'b' || app.cur !== 0 || !document.querySelector('#ctx').hidden) throw new Error('Close others left ' + names('.thumb') + ' cur=' + app.cur);
  // groups: three more pages, two of them into a named group; sorting the top level must not reorder the group
  const more2 = new DataTransfer();
  for (const n of ['p10', 'p2', 'p1']) more2.items.add(new File([await new Promise(r => c.toBlob(r))], n + '.png', { type: 'image/png' }));
  input.files = more2.files; input.dispatchEvent(new Event('change'));
  for (let i = 0; i < 50 && app.pages.length < 4; i++) await sleep(100);
  const byName = n => [...document.querySelectorAll('.thumb')].find(t => t.querySelector('span').textContent.replace('✓ ', '') === n);
  byName('p10').dispatchEvent(new MouseEvent('click', { bubbles: true }));
  byName('p2').dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));
  document.querySelector('#bNewGroup').click();
  const nameBox = document.querySelector('.grename');
  if (!nameBox) throw new Error('new group did not ask for a name');
  nameBox.value = 'Chapter'; nameBox.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
  const inGroup = () => names('.gbody .thumb'), top = () => [...document.querySelectorAll('#pages > .thumb span, #pages > .grp .gname')].map(s => s.textContent.replace('✓ ', '')).join();
  if (document.querySelector('.gname')?.textContent !== 'Chapter' || inGroup() !== 'p2,p10') throw new Error('group: ' + document.querySelector('.gname')?.textContent + ' / ' + inGroup());
  const sortSel = document.querySelector('#sSort'); sortSel.value = 'name'; sortSel.dispatchEvent(new Event('change'));
  document.querySelector('#bSortDir').click();   // name, descending
  if (top() !== 'p1,Chapter,b' || inGroup() !== 'p2,p10') throw new Error('top-level sort: ' + top() + ' / group: ' + inGroup());
  document.querySelector('.grp .gsort button').click();   // the group's own direction
  if (inGroup() !== 'p10,p2' || top() !== 'p1,Chapter,b') throw new Error('group sort: ' + inGroup() + ' / top: ' + top());
  document.querySelector('.gh').dispatchEvent(new MouseEvent('click', { bubbles: true }));
  if (document.querySelector('.gbody')) throw new Error('group did not collapse');
  document.querySelector('.gh').dispatchEvent(new MouseEvent('click', { bubbles: true }));
  // drag p1 onto the group
  const dt = new DataTransfer();
  byName('p1').dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
  document.querySelector('.gh').dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
  document.querySelector('.gh').dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
  if (inGroup() !== 'p10,p2,p1') throw new Error('drag into group gave ' + inGroup());
  document.querySelector('.gh').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 60, clientY: 60 }));
  menuItem('Ungroup').click();
  if (document.querySelector('.grp') || app.pages.some(p => p.group)) throw new Error('ungroup left a group');
  sortSel.value = 'added'; sortSel.dispatchEvent(new Event('change')); document.querySelector('#bSortDir').click();
  // a reply that repeats itself and is cut off mid-entry still gives its finished, distinct bubbles
  const cutOff = '{"items": [\n {"bbox_2d": [10, 20, 300, 400], "text": "お前は\\"誰\\"だ"},\n' +
    ' {"bbox_2d": [500, 20, 900, 400], "text": "待って", "lang": "ja"},\n {"bbox_2d": [12, 22, 301, 398], "text": "お前は\\"誰\\"だ"},\n {"bbox_2d": [12, 2';
  const parsed = app.parseFound(cutOff);
  if (parsed.length !== 2 || parsed[0].text !== 'お前は"誰"だ' || parsed[1].bbox_2d[0] !== 500 || parsed[1].lang !== 'ja')
    throw new Error('cut-off reply parsed wrong: ' + JSON.stringify(parsed));
  return JSON.stringify({ before, after, area, parsed: parsed.length, fg: it.fg, bg: it.bg, outline: it.outline, ai: app.ai.state,
    fonts: document.querySelector('#sFont').options.length, w: px.width });
})()
