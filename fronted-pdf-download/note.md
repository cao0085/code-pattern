

### 畫面截圖（html2canvas、dom-to-image…）

原理大多是用`document.query(ID / class)`抓取指定的`DOM`，在套件內**模擬**JS排版樣式引擎(所以可能會跑版)，繪圖在 HTML` <canvas> `Element 後再轉成 base64/Blob 格式輸出。

要注意的是瀏覽器對於能當作繪製`<canvas>`的來源控管較為嚴格，例如在瀏覽器渲染的 img 來源可以使用，套件內被擋下來的問題，再來就是錯誤是發生在套件內部不好除錯。

### 向量型（jsPDF、pdf-lib…）

用程式碼直接描述格線、文字 ，也可以插入圖片當作背景。要注意的一樣是外部來源的合法(圖片、字體)，圖片可先在.js 轉換成 base64 當作來源給套件使用，減少套件內轉換失敗的風險。

### jsPDF

添加字體方式有兩種方式讀取.ttf和.js，一樣讓套件讀取處理過的資源部屬比較穩定

網站下載 .ttf 字體向量檔案

```js
// var pdfDoc = 建立一個 jsPDF 提供的物件({ unit: 'mm', format: 'a4' })
pdfDoc.addFont('./eduSong_Unicode.ttf', 'eduSong_Unicode', 'normal');
pdfDoc.setFont('eduSong_Unicode', 'normal');
```

把 .ttf 轉成.js 再import到模組，因為字型檔案很大要用到再載入就好

```js
// 有呼叫函式再import
async ensurePdfLib() {
      // 若 import 過就不動作
    if (this.pdfReady) return this.pdfReady;

    this.pdfReady = Promise.all([
    import(/* webpackChunkName:"pdf-bundle" */ 'jspdf'),
    import(/* webpackChunkName:"pdf-bundle" */ '@/assets/fonts/eduSong_Unicode-normal.js')
    ]).then(([mod]) => mod.jsPDF || mod.default);
    return this.pdfReady;
},
// var pdfDoc = 建立一個 jsPDF 提供的物件({ unit: 'mm', format: 'a4' })
pdfDoc.setFont('eduSong_Unicode', 'normal');
```

圖片轉URL

```js
const img = await this.loadImage(targetImage);
    loadImage(url) {
      return new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => resolve(img);
        img.onerror = reject;
        img.src = url;
      });
    },

// 讀取圖片
const img = await this.loadImage(targetImage);

// 先轉成 Base64 Data URL
const canvas = document.createElement('canvas');
canvas.width = img.naturalWidth || img.width;
canvas.height = img.naturalHeight || img.height;
const ctx = canvas.getContext('2d');
ctx.drawImage(img, 0, 0);
const dataUrl = canvas.toDataURL('image/jpeg', 1.0);

// 加入 jsPDF 當作底稿
pdfDoc.addImage(dataUrl, 'JPEG', 0, 0, 210, 297);
pdfDoc.setFont('eduSong_Unicode', 'normal');
```

下載和預覽

```js
const blob = pdfDoc.output('blob');
const pdfUrl = URL.createObjectURL(blob); // 可插入 DOM 
pdfDoc.save("fileName.pdf")
```
