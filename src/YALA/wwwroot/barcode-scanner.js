let stream;
let timer;

export function isSupported() {
    return Boolean(navigator.mediaDevices?.getUserMedia && window.BarcodeDetector);
}

export async function start(video, dotNet) {
    if (!isSupported()) throw new Error("Camera barcode scanning is not supported by this browser. Enter the barcode manually instead.");
    stop(video);
    const supported = await BarcodeDetector.getSupportedFormats();
    const formats = ["ean_13", "ean_8", "upc_a", "upc_e", "itf", "code_128"].filter(format => supported.includes(format));
    if (formats.length === 0) throw new Error("This browser cannot detect retail barcodes. Enter the barcode manually instead.");
    const detector = new BarcodeDetector({ formats });
    stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 }, height: { ideal: 720 } }, audio: false });
    video.srcObject = stream;
    await video.play();
    timer = window.setInterval(async () => {
        if (!stream || video.readyState < 2) return;
        try {
            const value = (await detector.detect(video))[0]?.rawValue;
            if (value) {
                stop(video);
                await dotNet.invokeMethodAsync("BarcodeDetected", value);
            }
        } catch { }
    }, 300);
}

export function stop(video) {
    if (timer) window.clearInterval(timer);
    timer = undefined;
    if (stream) stream.getTracks().forEach(track => track.stop());
    stream = undefined;
    if (video) video.srcObject = null;
}
