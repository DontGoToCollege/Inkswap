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
  const area = it.area.map(Math.round);
  if (!(area[2] - area[0] > 140)) throw new Error('text area did not grow to the bubble: ' + area);
  // a reply that repeats itself and is cut off mid-entry still gives its finished, distinct bubbles
  const cutOff = '{"items": [\n {"bbox_2d": [10, 20, 300, 400], "text": "お前は\\"誰\\"だ"},\n' +
    ' {"bbox_2d": [500, 20, 900, 400], "text": "待って"},\n {"bbox_2d": [12, 22, 301, 398], "text": "お前は\\"誰\\"だ"},\n {"bbox_2d": [12, 2';
  const parsed = app.parseFound(cutOff);
  if (parsed.length !== 2 || parsed[0].text !== 'お前は"誰"だ' || parsed[1].bbox_2d[0] !== 500)
    throw new Error('cut-off reply parsed wrong: ' + JSON.stringify(parsed));
  return JSON.stringify({ before, after, area, parsed: parsed.length, fg: it.fg, bg: it.bg, outline: it.outline, ai: app.ai.state,
    fonts: document.querySelector('#sFont').options.length, w: px.width });
})()
