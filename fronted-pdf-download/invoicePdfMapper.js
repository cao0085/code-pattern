export function mappingToPdfText(data) {
  const receiptListProcess = data.receiptList.map(item => ({
    ...item,
    unitNonTax: toThousandFilter(item.unitNonTax),
    amount: toThousandFilter(item.amount)
  }));

  const pdfText = {
    InvoiceDate: (data.invInfo_Date?.split('T')[0]) ?? '　',
    InvoiceNo: data.invoiceNum ?? '　',
    Buyer: data.invInfo_BuyerName ?? '　',
    BuyerUniformNo: data.invInfo_BuyerNum ?? '　',
    BuyerAddress: data.buyerAddress ?? '　',
    RandomNo: data.gui ?? '　',
    Memo: data.memo ?? '　',

      // ───── List ─────
    ReceiptList: receiptListProcess,

      // ───── Seller 區塊 ─────
    Seller: data.comName ?? '　',
    SellerAddress: data.companyRegisterNo ?? '　',
    SellerUniformNo: data.siteAddress ?? '　',

      // ───── Totals ─────
    NonTax: toThousandFilter(data.totalNonTaxAmount),
    Tax: toThousandFilter(data.totalTax) ?? '　',
    Price: toThousandFilter(data.totalPrice),
    PriceCh: convertToChineseNum(data.totalPrice) + '元整'
  };

  return pdfText;
}

export function addTextToPDF(doc, pdfText) {
  const {
    InvoiceDate = '　',
    InvoiceNo = '　',
    Buyer = '　',
    BuyerUniformNo = '　',
    BuyerAddress = '　',
    RandomNo = '　',
    Memo = '　',
    /* 明細表 */
    ReceiptList = [],

    /* 賣方區塊對應 position.seller* */
    Seller = '　',
    SellerUniformNo = '　',
    SellerAddress = '　',

    /* 總計區塊 */
    NonTax = '　',
    Tax = '　',
    Price = '　',
    PriceCh = '　'
  } = pdfText;

  const position = {
    // Top
    InvoiceDate: { x: 96, y: 40 },
    InvoiceNo: { x: 32, y: 50 },
    Buyer: { x: 32, y: 56 },
    BuyerUniformNo: { x: 32, y: 62 },
    BuyerAddress: { x: 32, y: 68, maxWidth: 70 },
    RandomNo: { x: 161, y: 56 },
    Memo: { x: 146, y: 90 },
    // Center (list)
    listBaseY: 90,
    listGapY: 7,
    listCols: { item: 14, quantity: 84, unitPrice: 110, amount: 142, memo: 147 },
    // Seller company
    Seller: { x: 166, y: 239 },
    SellerAddress: { x: 166, y: 253 },
    SellerUniformNo: { x: 166, y: 259 },
    // Total
    NonTax: { x: 142, y: 231 },
    Tax: { x: 142, y: 239 },
    TaxType: { x: 56, y: 239 },
    Price: { x: 142, y: 247 },
    PriceCh: { x: 142, y: 258, fontSize: 12 }
  };

  doc.setFontSize(11);

  /* ---------- 3. TOP 區 ---------- */
  doc.text(InvoiceDate, position.InvoiceDate.x, position.InvoiceDate.y, { baseline: 'bottom' });
  doc.text(InvoiceNo, position.InvoiceNo.x, position.InvoiceNo.y, { baseline: 'bottom' });
  doc.text(Buyer, position.Buyer.x, position.Buyer.y, { baseline: 'bottom' });
  doc.text(BuyerUniformNo, position.BuyerUniformNo.x, position.BuyerUniformNo.y, { baseline: 'bottom' });
  doc.text(BuyerAddress, position.BuyerAddress.x, position.BuyerAddress.y,
           { baseline: 'bottom', maxWidth: position.BuyerAddress.maxWidth });
  doc.text(RandomNo, position.RandomNo.x, position.RandomNo.y, { baseline: 'bottom' });

  /* ---------- 4. 明細表 ---------- */
  const wrappedMemo = doc.splitTextToSize(Memo, 53);
  doc.text(wrappedMemo, position.Memo.x, position.Memo.y, { baseline: 'bottom' });
  ReceiptList.forEach((row, idx) => {
    const y = position.listBaseY + idx * position.listGapY;
    doc.text(`${row.item || '　'}`, position.listCols.item, y);
    doc.text(`${row.quantity || '　'}`, position.listCols.quantity, y, { align: 'right' });
    doc.text(`${row.unitNonTax || '　'}`, position.listCols.unitPrice, y, { align: 'right' });
    doc.text(`${row.amount || '　'}`, position.listCols.amount, y, { align: 'right' });
    // doc.text(`${row.memo || ''}`, position.listCols.memo, y);
  });

  /* ---------- 5. 賣方資訊 ---------- */
  processText(doc, Seller, position.Seller.x, position.Seller.y);
  doc.text(SellerAddress, position.SellerAddress.x, position.SellerAddress.y, { baseline: 'bottom' });
  processText(doc, SellerUniformNo, position.SellerUniformNo.x, position.SellerUniformNo.y);

  function processText(doc, fullText, startX, startY, options = {}) {
    const {
      maxLines = 3,
      lineLengths = [8, 13, 13],
      xOffsets = [0, -20, -20],
      yGap = 5,
      align = 'left',
      baseline = 'bottom'
    } = options;
    const text = fullText || '　';
    const lines = [];
    let cursor = 0;
    for (let i = 0; i < maxLines; i++) {
      const len = lineLengths[i] || 10;
      lines.push(text.slice(cursor, cursor + len));
      cursor += len;
      if (cursor >= text.length) break;
    }
    lines.forEach((line, idx) => {
      const x = startX + (xOffsets[idx] || 0);
      const y = startY + idx * yGap;
      doc.text(line, x, y, { baseline, align });
    });
  }

  /* ---------- 6. 總計 ---------- */
  doc.text(`${NonTax}`, position.NonTax.x, position.NonTax.y, { baseline: 'bottom', align: 'right' });
  doc.text(`${Tax}`, position.Tax.x, position.Tax.y, { baseline: 'bottom', align: 'right' });
  doc.text('V', position.TaxType.x, position.TaxType.y, { baseline: 'bottom' });
  doc.text(`${Price}`, position.Price.x, position.Price.y, { baseline: 'bottom', align: 'right' });

  doc.setFontSize(position.PriceCh.fontSize);
  doc.text(`${PriceCh}`, position.PriceCh.x, position.PriceCh.y, { baseline: 'bottom', align: 'right' });
  doc.setFontSize(11);

  return doc;
}

function toThousandFilter(num) {
  return (+num || 0).toString().replace(/^-?\d+/g, m => m.replace(/(?=(?!\b)(\d{3})+$)/g, ','));
}

function convertToChineseNum(num) {
  const digitChars = ['零', '壹', '貳', '參', '肆', '伍', '陸', '柒', '捌', '玖'];
  const units = ['', '拾', '佰', '仟'];
  const sections = ['', '萬', '億', '兆'];

  if (num === 0) return '台幣零圓整';

  const numStr = num.toString();
  let result = '';
  let sectionCount = 0;

  let str = numStr;
  while (str.length > 0) {
    const section = str.slice(-4);
    str = str.slice(0, -4);

    let sectionResult = '';
    let zeroFlag = false;

    for (let i = 0; i < section.length; i++) {
      const digit = parseInt(section[section.length - 1 - i]);
      const unit = units[i];

      if (digit === 0) {
        if (!zeroFlag) {
          sectionResult = '零' + sectionResult;
          zeroFlag = true;
        }
      } else {
        sectionResult = digitChars[digit] + unit + sectionResult;
        zeroFlag = false;
      }
    }

    sectionResult = sectionResult.replace(/零+$/, ''); // 去尾部多餘零
    if (sectionResult !== '') {
      sectionResult += sections[sectionCount];
    }
    result = sectionResult + result;
    sectionCount++;
  }

  result = result.replace(/零+/g, '零');
  result = result.replace(/零(萬|億|兆)/g, '$1');
  result = result.replace(/億萬/g, '億');
  result = result.replace(/零+$/, '');

  return result;
}
