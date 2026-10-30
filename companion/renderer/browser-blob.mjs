// NW.js mixed context replaces even window.Blob with Node's incompatible Blob.
// A DOM-produced Blob retains Chromium's original constructor. The one-pixel
// canvas only discovers that constructor; it is never a model texture or output.
export const BrowserBlob = await new Promise((resolve, reject) => {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 1;
  canvas.toBlob((blob) => {
    if (!blob) reject(new Error("renderer-failed"));
    else resolve(blob.constructor);
  }, "image/png");
});
