// grid-paper.js -------------------------------------------------------------
export function drawA4Grid(doc, {
  margin      = 10,    // 四邊留白(mm)
  majorStep   = 10,    // 主格線間距
  minorStep   = 5,     // 次格線間距
  majorWidth  = 0.3,   // 主格線線寬
  minorWidth  = 0.1,   // 次格線線寬
  majorColor  = 150,   // 主格線顏色 (0–255 灰階)
  minorColor  = 230    // 次格線顏色
} = {}) {

  const pageW = 210, pageH = 297;              // A4, 單位 mm
  const left  = margin, top = margin;
  const right = pageW - margin;
  const bottom = pageH - margin;

  // ---- 次格線 (5 mm) ----
  doc.setLineWidth(minorWidth);
  doc.setDrawColor(minorColor);

  for (let x = left; x <= right; x += minorStep) {
    doc.line(x, top, x, bottom);
  }
  for (let y = top; y <= bottom; y += minorStep) {
    doc.line(left, y, right, y);
  }

  // ---- 主格線 (10 mm) ----
  doc.setLineWidth(majorWidth);
  doc.setDrawColor(majorColor);

  for (let x = left; x <= right; x += majorStep) {
    doc.line(x, top, x, bottom);
  }
  for (let y = top; y <= bottom; y += majorStep) {
    doc.line(left, y, right, y);
  }

  // ---- 座標刻度 ----
  doc.setFontSize(6);
  doc.setTextColor(80);

  for (let x = left, i = 0; x <= right; x += majorStep, i++) {
    doc.text(String(i * majorStep), x + 1, top - 2, { align: 'left' });
  }
  for (let y = top, i = 0; y <= bottom; y += majorStep, i++) {
    doc.text(String(i * majorStep), left - 4, y + 2, { align: 'right' });
  }
}
