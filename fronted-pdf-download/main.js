
import { mappingToPdfText, addTextToPDF } from './invoicePdfMapper.js';
import invoiceTemplate from './invoiceTemplate.jpg';
import { drawA4Grid } from './drawTableLine.js';

// usage
async function generateInvoicePdf(data) {
    const jsPDF = await ensurePdfLib.call(this);
    const doc = new jsPDF('p', 'mm', 'a4');

    // 設定字型
    doc.addFileToVFS('eduSong_Unicode-normal.ttf', this.pdfReady.fontData);
    doc.addFont('eduSong_Unicode-normal.ttf', 'eduSong_Unicode-normal', 'normal');
    doc.setFont('eduSong_Unicode-normal');

    // 繪製背景圖片 or 繪製格線 擇一
    const img = await loadImage(invoiceTemplate);
    doc.addImage(img, 'JPEG', 0, 0, 210, 297);
    drawA4Grid(doc);

    // 自封裝function 繪製文字
    const pdfText = mappingToPdfText(data);
    addTextToPDF(doc, pdfText);
    // ────── 開新分頁&下載 PDF ──────
    const url = doc.output('bloburl');
    window.open(url);
    doc.save(`${data.invoiceNum}_電子發票證明`);
    return doc;
}

async function ensurePdfLib() {
    if (this.pdfReady) return this.pdfReady;

      // 一次載入 jsPDF 及字型檔
    this.pdfReady = Promise.all([
        import(/* webpackChunkName:"pdf-bundle" */ 'jspdf'),
        import(/* webpackChunkName:"pdf-bundle" */ './eduSong_Unicode-normal.js')
    ]).then(([mod]) => mod.jsPDF || mod.default);

    return this.pdfReady;
}

function loadImage(url) {
    return new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => resolve(img);
        img.onerror = reject;
        img.src = url;
    });
}